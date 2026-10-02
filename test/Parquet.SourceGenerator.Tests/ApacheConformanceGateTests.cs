using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Guards <c>scripts/VerifyApacheConformance.cs</c> against reporting "OK" having examined no
/// fixtures (#436). A fixture whose file could not be found used to be skipped, so a moved
/// directory left the verdict green. The refusal happens before any Java process starts, which is
/// why these tests need neither Java nor the pinned toolchain.
/// </summary>
[Trait("Category", "Integration")]
public class ApacheConformanceGateTests
{
    private const string EmptyToolManifest =
        """{ "repository": "https://example.invalid", "artifacts": [] }""";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManifestWhoseFixturesDoNotResolveFailsTheGateAsync()
    {
        const string manifest = """
            { "fixtures": [
              { "path": "test/data/v1/does-not-exist-a.parquet" },
              { "path": "test/data/v1/does-not-exist-b.parquet" }
            ] }
            """;

        ScriptResult result = await RunAsync(manifest, createFixtureFiles: false);

        result.ExitCode.ShouldBe(
            1,
            $"A manifest whose fixtures are all missing must fail.\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}"
        );
        result.Stderr.ShouldContain("MISSING fixture: test/data/v1/does-not-exist-a.parquet");
        result.Stderr.ShouldContain("2 of 2 manifest fixtures do not exist");
        result.Stdout.ShouldNotContain("Apache conformance OK");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManifestWithOneMissingFixtureAmongPresentOnesFailsTheGateAsync()
    {
        const string manifest = """
            { "fixtures": [
              { "path": "test/data/v1/present.parquet" },
              { "path": "test/data/v1/missing.parquet" }
            ] }
            """;

        ScriptResult result = await RunAsync(
            manifest,
            createFixtureFiles: true,
            fixtureToCreate: "test/data/v1/present.parquet"
        );

        result.ExitCode.ShouldBe(1, $"Stdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
        result.Stderr.ShouldContain("MISSING fixture: test/data/v1/missing.parquet");
        result.Stderr.ShouldContain("1 of 2 manifest fixtures do not exist");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManifestListingNoFixturesFailsTheGateAsync()
    {
        ScriptResult result = await RunAsync("""{ "fixtures": [] }""", createFixtureFiles: false);

        result.ExitCode.ShouldBe(1, $"Stdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
        result.Stderr.ShouldContain("lists no fixtures");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManifestWhoseFixturesExistIsNotRefusedByTheExaminedFixturesGuardAsync()
    {
        // Positive control: when every listed fixture exists, the guard must stay out of the way.
        // The run then goes on to the Java stage, which cannot succeed here (no toolchain), so it
        // exits non-zero for that reason; what matters is that it is not the guard's refusal.
        const string manifest =
            """{ "fixtures": [ { "path": "test/data/v1/present.parquet" } ] }""";

        ScriptResult result = await RunAsync(
            manifest,
            createFixtureFiles: true,
            fixtureToCreate: "test/data/v1/present.parquet"
        );

        (result.Stderr + result.Stdout).ShouldNotContain("refusing to report OK");
        (result.Stderr + result.Stdout).ShouldNotContain("MISSING fixture");
    }

    private static async Task<ScriptResult> RunAsync(
        string fixtureManifestJson,
        bool createFixtureFiles,
        string? fixtureToCreate = null
    )
    {
        string repoRoot = FindRepoRoot();
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "parquet-apache-gate-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempDir);

        try
        {
            string fixtureRoot = Path.Combine(tempDir, "root");
            Directory.CreateDirectory(fixtureRoot);
            string toolsDir = Path.Combine(tempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            string toolManifest = Path.Combine(tempDir, "tool-manifest.json");
            string fixtureManifest = Path.Combine(tempDir, "fixture-manifest.json");
            await global::System.IO.File.WriteAllTextAsync(toolManifest, EmptyToolManifest);
            await global::System.IO.File.WriteAllTextAsync(fixtureManifest, fixtureManifestJson);

            if (createFixtureFiles && fixtureToCreate is not null)
            {
                string path = Path.Combine(fixtureRoot, fixtureToCreate);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await global::System.IO.File.WriteAllBytesAsync(path, [0x50, 0x41, 0x52, 0x31]);
            }

            ProcessStartInfo psi = CreateProcessStartInfo(
                repoRoot,
                toolManifest,
                toolsDir,
                fixtureRoot,
                fixtureManifest,
                Path.Combine(tempDir, "evidence")
            );

            using Process? process = Process.Start(psi);
            process.ShouldNotBeNull();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup only.
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (
                global::System.IO.File.Exists(
                    Path.Combine(dir.FullName, "scripts", "VerifyApacheConformance.cs")
                )
            )
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find repository root containing scripts/VerifyApacheConformance.cs"
        );
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string repoRoot,
        string toolManifest,
        string toolsDir,
        string fixtureRoot,
        string fixtureManifest,
        string outputDir
    )
    {
        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");
        string dotnetHost = global::System.IO.File.Exists(homeDotnet) ? homeDotnet : "dotnet";

        var psi = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            string argument in new[]
            {
                "run",
                "scripts/VerifyApacheConformance.cs",
                "--",
                "--tool-manifest",
                toolManifest,
                "--tools-dir",
                toolsDir,
                "--root",
                fixtureRoot,
                "--fixture-manifest",
                fixtureManifest,
                "--output-dir",
                outputDir,
            }
        )
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        psi.Environment["DOTNET_BUILD_SERVER_DISABLE"] = "1";

        if (Directory.Exists(homeDotnetDir))
        {
            psi.Environment["DOTNET_ROOT"] = homeDotnetDir;
            psi.Environment["PATH"] =
                Path.Combine(homeDotnetDir, "tools")
                + Path.PathSeparator
                + homeDotnetDir
                + Path.PathSeparator
                + Environment.GetEnvironmentVariable("PATH");
        }

        return psi;
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);
}
