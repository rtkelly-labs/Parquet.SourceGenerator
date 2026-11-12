using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Drives <c>scripts/BenchmarkDiffraPayload.cs</c>, the shadow step that turns the regression
/// gate's BenchmarkDotNet export into a Diffra <c>diffra/benchmark@1</c> payload and a collection
/// document, and <c>scripts/JsonSchemaCheck.cs</c>, which checks both against the vendored schemas.
/// </summary>
/// <remarks>
/// The payload is only useful if its keys, units, stratum and methodology never drift without
/// the benchmark changing, and if a run that measured nothing produces no payload rather than an
/// empty one (which Diffra would read as every benchmark removed).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class BenchmarkDiffraPayloadTests : IDisposable
{
    private const string Host = """
        "HostEnvironmentInfo": {
          "BenchmarkDotNetVersion": "0.14.0",
          "RuntimeVersion": ".NET 8.0.33 (8.0.3326.52219)",
          "Architecture": "X64",
          "ProcessorName": "AMD EPYC 9V74"
        }
        """;

    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private const string Write10k = """
        {
          "Type": "WriteBenchmarks",
          "Method": "WriteAsync",
          "Parameters": "Count=10000",
          "Statistics": { "Mean": 239597.0412 },
          "Memory": {
            "Gen0Collections": 300,
            "Gen1Collections": 20,
            "Gen2Collections": 0,
            "TotalOperations": 2048,
            "BytesAllocatedPerOperation": 1135142
          }
        }
        """;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bench-diffra-");

    private string ResultsDirectory => Path.Combine(_root.FullName, "results");

    private string OutputDirectory => Path.Combine(_root.FullName, "payload");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task AMissingResultsDirectoryEmitsNothingAsync()
    {
        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.ToString());
        Directory.Exists(OutputDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task ACaseWithoutTheMemoryDiagnoserEmitsNothingAsync()
    {
        WriteExport(
            "WriteBenchmarks",
            """
            {
              "Type": "WriteBenchmarks",
              "Method": "WriteAsync",
              "Parameters": "Count=1000",
              "Statistics": { "Mean": 1000.5 }
            }
            """
        );

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.ToString());
        result.Stderr.ShouldContain("WriteBenchmarks.WriteAsync(Count=1000)");
        Directory.Exists(OutputDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AShortCommitIsAUsageErrorAsync()
    {
        WriteExport("WriteBenchmarks", Write10k);

        ScriptResult result = await RunAsync(
            "scripts/BenchmarkDiffraPayload.cs",
            ResultsDirectory,
            OutputDirectory,
            "--commit",
            "0123456"
        );

        result.ExitCode.ShouldBe(2, result.ToString());
        Directory.Exists(OutputDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AGatedRunBecomesOnePayloadThatFitsTheSchemaAsync()
    {
        // A benchmark without parameters keeps the bare Type.Method key the baseline uses.
        WriteExport(
            "WriteBenchmarks",
            Write10k
                + """
                ,
                {
                  "Type": "TransposeBenchmarks",
                  "Method": "TransposeOnly",
                  "Parameters": "",
                  "Statistics": { "Mean": 12.04 },
                  "Memory": {
                    "Gen0Collections": 0,
                    "Gen1Collections": 0,
                    "Gen2Collections": 0,
                    "TotalOperations": 4096,
                    "BytesAllocatedPerOperation": 0
                  }
                }
                """
        );

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(0, result.ToString());
        result.Stdout.ShouldContain("Examined 2 benchmark case(s) from 1 file(s)");

        const string Key = "WriteBenchmarks.WriteAsync(Count=10000)";
        const string Bare = "TransposeBenchmarks.TransposeOnly";
        const string Methodology =
            "diffra/benchmark@1;parquet-sourcegenerator.prototype.benchmarkdotnet@0.1.0;benchmarkdotnet-json-full@0.14";
        string payloadPath = Path.Combine(OutputDirectory, "benchmarks.json");
        string collectionPath = Path.Combine(OutputDirectory, "collection.json");

        using (JsonDocument payload = ReadPayload("benchmarks.json"))
        {
            JsonElement root = payload.RootElement;
            root.GetProperty("scope").GetString().ShouldBe("parquet-sourcegenerator");

            // The methodology names the converter and the export format, nothing about the host.
            root.GetProperty("methodology").GetString().ShouldBe(Methodology);

            // The host is the stratum. The runtime patch is left out.
            JsonElement stratum = root.GetProperty("stratum");
            stratum.GetProperty("runtime").GetString().ShouldBe("net8.0");
            stratum.GetProperty("arch").GetString().ShouldBe("x64");
            stratum.GetProperty("cpu").GetString().ShouldBe("AMD EPYC 9V74");
            stratum.GetProperty("os").GetString().ShouldBeOneOf("linux", "macos", "windows");

            root.GetProperty("plan")
                .GetProperty("benchmarks")
                .GetProperty("keys")
                .GetArrayLength()
                .ShouldBe(2);

            JsonElement fields = root.GetProperty("metrics").GetProperty("benchmarks");

            // Every value is a canonical decimal string: no trailing zero, no trailing point.
            JsonElement allocated = fields.GetProperty("allocatedBytes").GetProperty(Key);
            allocated.GetProperty("value").GetString().ShouldBe("1135142");
            allocated.GetProperty("unit").GetString().ShouldBe("B");

            // GC counts are normalised per 1000 operations.
            JsonElement gen0 = fields.GetProperty("gen0PerKOps").GetProperty(Key);
            gen0.GetProperty("value").GetString().ShouldBe("146.484375");
            gen0.GetProperty("unit").GetString().ShouldBe("count/1000-ops");
            fields
                .GetProperty("gen1PerKOps")
                .GetProperty(Key)
                .GetProperty("value")
                .GetString()
                .ShouldBe("9.765625");
            fields
                .GetProperty("gen2PerKOps")
                .GetProperty(Key)
                .GetProperty("value")
                .GetString()
                .ShouldBe("0");

            JsonElement mean = fields.GetProperty("meanTime").GetProperty(Key);
            mean.GetProperty("value").GetString().ShouldBe("239597");
            mean.GetProperty("unit").GetString().ShouldBe("ns");

            fields
                .GetProperty("meanTime")
                .GetProperty(Bare)
                .GetProperty("value")
                .GetString()
                .ShouldBe("12");
            fields
                .GetProperty("allocatedBytes")
                .GetProperty(Bare)
                .GetProperty("value")
                .GetString()
                .ShouldBe("0");
        }

        using (JsonDocument collection = ReadPayload("collection.json"))
        {
            JsonElement root = collection.RootElement;
            root.GetProperty("status").GetString().ShouldBe("complete");
            root.GetProperty("subject").GetProperty("commit").GetString().ShouldBe(Commit);

            // One entry per field, all pointing at the one payload.
            JsonElement metrics = root.GetProperty("metrics");
            metrics.GetArrayLength().ShouldBe(5);
            string evidenceId =
                "sha256:"
                + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(payloadPath)));
            foreach (JsonElement metric in metrics.EnumerateArray())
            {
                metric.GetProperty("metric").GetString().ShouldBe("benchmarks");
                metric.GetProperty("methodology").GetString().ShouldBe(Methodology);
                JsonElement entry = metric.GetProperty("strata")[0];
                entry.GetProperty("status").GetString().ShouldBe("collected");
                entry
                    .GetProperty("evidenceId")
                    .GetString()
                    .ShouldBe(evidenceId, StringCompareShould.IgnoreCase);

                // Allocations compare across processors; GC counts and time only within one.
                bool perProcessor = metric.GetProperty("field").GetString() != "allocatedBytes";
                entry.GetProperty("stratum").TryGetProperty("cpu", out _).ShouldBe(perProcessor);
            }
        }

        ScriptResult valid = await CheckAsync(payloadPath, collectionPath);
        valid.ExitCode.ShouldBe(0, valid.ToString());
        valid.Stdout.ShouldContain("Examined 2 document(s); 0 invalid.");

        // Negative control: the pre-standard value format, with a trailing zero.
        string text = System.IO.File.ReadAllText(payloadPath);
        string broken = text.Replace("\"239597\"", "\"239597.0\"", StringComparison.Ordinal);
        broken.ShouldNotBe(text);
        System.IO.File.WriteAllText(payloadPath, broken);

        ScriptResult invalid = await CheckAsync(payloadPath, collectionPath);
        invalid.ExitCode.ShouldBe(1, invalid.ToString());
        invalid.Stdout.ShouldContain("Examined 2 document(s); 1 invalid.");
    }

    private void WriteExport(string name, string benchmark)
    {
        Directory.CreateDirectory(ResultsDirectory);
        System.IO.File.WriteAllText(
            Path.Combine(ResultsDirectory, $"{name}-report-full-compressed.json"),
            "{" + Host + ", \"Benchmarks\": [" + benchmark + "]}"
        );
    }

    private JsonDocument ReadPayload(string fileName) =>
        JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(OutputDirectory, fileName)));

    private Task<ScriptResult> RunAsync() =>
        RunAsync(
            "scripts/BenchmarkDiffraPayload.cs",
            ResultsDirectory,
            OutputDirectory,
            "--commit",
            Commit
        );

    private static Task<ScriptResult> CheckAsync(string payload, string collection) =>
        RunAsync(
            "scripts/JsonSchemaCheck.cs",
            "--schema",
            "diffra/schemas/benchmark-payload.schema.json",
            payload,
            "--schema",
            "diffra/schemas/collection-v1.schema.json",
            collection
        );

    private static async Task<ScriptResult> RunAsync(string script, params string[] arguments)
    {
        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");

        var psi = new ProcessStartInfo
        {
            FileName = System.IO.File.Exists(homeDotnet) ? homeDotnet : "dotnet",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--");
        foreach (string argument in arguments)
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
                homeDotnetDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        using var process = Process.Start(psi);
        process.ShouldNotBeNull();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (
                System.IO.File.Exists(
                    Path.Combine(dir.FullName, "scripts", "BenchmarkDiffraPayload.cs")
                )
            )
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root containing scripts/BenchmarkDiffraPayload.cs"
        );
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public override string ToString() =>
            $"Exit {ExitCode}\nStdout:\n{Stdout}\nStderr:\n{Stderr}";
    }
}
