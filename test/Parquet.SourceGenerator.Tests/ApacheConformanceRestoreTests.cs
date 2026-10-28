using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Drives <c>scripts/VerifyApacheConformance.cs --restore</c> against a loopback HTTP server that
/// plays two Maven mirrors. A transient 404 from repo1.maven.org on a jar that was published and
/// unchanged failed CI on a pull request that touched nothing Parquet-related (#692), so the
/// restore walks an ordered mirror list with retries. These tests pin that it still refuses every
/// byte that does not match the pinned SHA-256, and that it says exactly what failed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ApacheConformanceRestoreTests : IDisposable
{
    private const string ArtifactFile = "tool-1.0.jar";
    private const string ArtifactPath = "org/example/tool/1.0/tool-1.0.jar";

    private static readonly byte[] ArtifactBytes = "pinned conformance tool bytes"u8.ToArray();

    // Sentinel body: the server sends 200 headers and half the bytes, then never finishes.
    private static readonly byte[] StallBody = [];
    private static readonly string ArtifactSha = Convert
        .ToHexString(SHA256.HashData(ArtifactBytes))
        .ToLowerInvariant();

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "parquet-apache-restore-" + Guid.NewGuid().ToString("N")
    );
    private readonly ConcurrentQueue<string> _requests = new();

    public ApacheConformanceRestoreTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FallsBackToTheNextMirrorWhenTheFirstReturns404Async()
    {
        // Positive control: this is the #692 shape, and the restore must survive it.
        ApacheConformanceGateTests.ScriptResult result = await RestoreAsync(
            (mirror, _) => mirror == "a" ? (404, null) : (200, ArtifactBytes),
            ArtifactSha
        );

        result.ExitCode.ShouldBe(0, Describe(result));
        result.Stdout.ShouldContain("HTTP 404");
        result.Stdout.ShouldContain($"verified: {ArtifactFile}");
        result.Stdout.ShouldContain("restore OK: 1 artifacts verified");
        global::System.IO.File.ReadAllBytes(ToolPath()).ShouldBe(ArtifactBytes);
        _requests.ToArray().ShouldBe([$"a/{ArtifactPath}", $"b/{ArtifactPath}"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AMirrorThatStallsMidBodyTimesOutAndFallsBackAsync()
    {
        // HttpClient.Timeout does not cover a body read after ResponseHeadersRead, so without the
        // per-download deadline this restore would hang on mirror "a" until the CI job timeout.
        // The outer bound turns a regression into a failure rather than a hung test run.
        ApacheConformanceGateTests.ScriptResult result = await RestoreAsync(
                (mirror, _) => mirror == "a" ? (200, StallBody) : (200, ArtifactBytes),
                ArtifactSha,
                "--download-timeout-seconds",
                "2"
            )
            .WaitAsync(TimeSpan.FromSeconds(90));

        result.ExitCode.ShouldBe(0, Describe(result));
        result.Stdout.ShouldContain("timed out after 2 s");
        result.Stdout.ShouldContain($"verified: {ArtifactFile}");
        global::System.IO.File.ReadAllBytes(ToolPath()).ShouldBe(ArtifactBytes);
        _requests.ToArray().ShouldBe([$"a/{ArtifactPath}", $"b/{ArtifactPath}"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetriesATransientFailureWithBackoffAsync()
    {
        int served = 0;
        ApacheConformanceGateTests.ScriptResult result = await RestoreAsync(
            (_, _) => Interlocked.Increment(ref served) <= 2 ? (503, null) : (200, ArtifactBytes),
            ArtifactSha
        );

        result.ExitCode.ShouldBe(0, Describe(result));
        result.Stdout.ShouldContain("HTTP 503");
        result.Stdout.ShouldContain($"retrying {ArtifactFile}");
        global::System.IO.File.ReadAllBytes(ToolPath()).ShouldBe(ArtifactBytes);
        _requests.Count.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EveryMirrorReturning404FailsWithOneErrorNamingTheArtifactAndUrlsAsync()
    {
        // Negative control for the fallback: when the artifact is gone everywhere the gate is red.
        ApacheConformanceGateTests.ScriptResult result = await RestoreAsync(
            (_, _) => (404, null),
            ArtifactSha
        );

        result.ExitCode.ShouldBe(1, Describe(result));
        string error = SingleErrorLine(result);
        error.ShouldStartWith(
            $"::error title=Apache conformance tool restore failed::{ArtifactFile} ({ArtifactPath})"
        );
        error.ShouldContain($"/a/{ArtifactPath} -> HTTP 404 Not Found (round 1)");
        error.ShouldContain($"/b/{ArtifactPath} -> HTTP 404 Not Found (round 1)");
        error.ShouldContain($"/b/{ArtifactPath} -> HTTP 404 Not Found (round 2)");
        result.Stdout.ShouldContain("1 of 1 artifacts could not be restored");
        _requests.Count.ShouldBe(4, "two mirrors times two rounds");
        global::System.IO.File.Exists(ToolPath()).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BytesThatDoNotMatchThePinnedShaAreRejectedFromEveryMirrorAsync()
    {
        // Negative control for integrity: a mirror is never trusted, only the pinned hash is.
        string wrongSha = new('0', 64);
        ApacheConformanceGateTests.ScriptResult result = await RestoreAsync(
            (_, _) => (200, ArtifactBytes),
            wrongSha
        );

        result.ExitCode.ShouldBe(1, Describe(result));
        string error = SingleErrorLine(result);
        error.ShouldContain($"SHA-256 mismatch: got {ArtifactSha}, pinned {wrongSha}");
        global::System
            .IO.File.Exists(ToolPath())
            .ShouldBeFalse("unverified bytes must never be left in place");
        Directory.GetFiles(ToolsDir()).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AFileAlreadyMatchingThePinIsKeptWithoutTouchingTheNetworkAsync()
    {
        // The CI cache only helps if a restored tools dir is recognised as current. The pin is
        // lowercase hex; the comparison must not depend on the case the hash is printed in.
        Directory.CreateDirectory(ToolsDir());
        await global::System.IO.File.WriteAllBytesAsync(ToolPath(), ArtifactBytes);

        ApacheConformanceGateTests.ScriptResult result = await RestoreAsync(
            (_, _) => (500, null),
            ArtifactSha
        );

        result.ExitCode.ShouldBe(0, Describe(result));
        result.Stdout.ShouldContain($"up to date: {ArtifactFile}");
        _requests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ACorruptCachedFileIsReplacedByVerifiedBytesAsync()
    {
        Directory.CreateDirectory(ToolsDir());
        await global::System.IO.File.WriteAllBytesAsync(ToolPath(), "stale"u8.ToArray());

        ApacheConformanceGateTests.ScriptResult result = await RestoreAsync(
            (_, _) => (200, ArtifactBytes),
            ArtifactSha
        );

        result.ExitCode.ShouldBe(0, Describe(result));
        global::System.IO.File.ReadAllBytes(ToolPath()).ShouldBe(ArtifactBytes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
    }

    private string ToolsDir() => Path.Combine(_tempDir, "tools");

    private string ToolPath() => Path.Combine(ToolsDir(), ArtifactFile);

    private async Task<ApacheConformanceGateTests.ScriptResult> RestoreAsync(
        Func<string, string, (int Status, byte[]? Body)> respond,
        string pinnedSha,
        params string[] extraArgs
    )
    {
        int port = FreeLoopbackPort();
        string prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        using var stop = new CancellationTokenSource();
        Task server = ServeAsync(listener, respond, stop.Token);

        string manifest = Path.Combine(_tempDir, "tool-manifest.json");
        await global::System.IO.File.WriteAllTextAsync(
            manifest,
            $$"""
            {
              "mirrors": [ "{{prefix}}a", "{{prefix}}b/" ],
              "artifacts": [
                { "file": "{{ArtifactFile}}", "path": "{{ArtifactPath}}", "sha256": "{{pinnedSha}}" }
              ]
            }
            """
        );

        try
        {
            string[] scriptArgs =
            [
                "--tool-manifest",
                manifest,
                "--restore",
                ToolsDir(),
                "--attempts",
                "2",
                "--retry-delay-ms",
                "10",
                .. extraArgs,
            ];
            return await ApacheConformanceGateTests.RunScriptAsync(
                ApacheConformanceGateTests.FindRepoRoot(),
                scriptArgs
            );
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await server;
        }
    }

    private async Task ServeAsync(
        HttpListener listener,
        Func<string, string, (int Status, byte[]? Body)> respond,
        CancellationToken stop
    )
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            string path = context.Request.Url!.AbsolutePath.TrimStart('/');
            int slash = path.IndexOf('/', StringComparison.Ordinal);
            string mirror = path[..slash];
            string artifactPath = path[(slash + 1)..];
            _requests.Enqueue($"{mirror}/{artifactPath}");

            (int status, byte[]? body) =
                artifactPath == ArtifactPath ? respond(mirror, artifactPath) : (404, null);
            if (ReferenceEquals(body, StallBody))
            {
                // Answered off the accept loop so the stalled connection cannot block the next mirror.
                _ = StallAsync(context, stop);
                continue;
            }
            context.Response.StatusCode = status;
            if (body is not null)
            {
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, stop);
            }
            context.Response.Close();
        }
    }

    private static async Task StallAsync(HttpListenerContext context, CancellationToken stop)
    {
        try
        {
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = ArtifactBytes.Length;
            await context.Response.OutputStream.WriteAsync(
                ArtifactBytes.AsMemory(0, ArtifactBytes.Length / 2),
                stop
            );
            await context.Response.OutputStream.FlushAsync(stop);
            await Task.Delay(Timeout.Infinite, stop);
        }
        catch (Exception ex)
            when (ex
                    is OperationCanceledException
                        or HttpListenerException
                        or ObjectDisposedException
            )
        {
            // The listener stopped or the client gave up; either way the stall has served its purpose.
        }
        finally
        {
            context.Response.Abort();
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string SingleErrorLine(ApacheConformanceGateTests.ScriptResult result)
    {
        string[] errors = Array.FindAll(
            result.Stdout.Split('\n'),
            line => line.StartsWith("::error", StringComparison.Ordinal)
        );
        errors.Length.ShouldBe(1, Describe(result));
        return errors[0].TrimEnd('\r');
    }

    private static string Describe(ApacheConformanceGateTests.ScriptResult result) =>
        $"Exit {result.ExitCode}\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}";
}
