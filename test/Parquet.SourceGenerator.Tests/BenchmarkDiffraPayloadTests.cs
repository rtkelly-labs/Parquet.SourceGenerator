using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Drives <c>scripts/BenchmarkDiffraPayload.cs</c>, the shadow step that turns the regression
/// gate's BenchmarkDotNet export into Diffra metrics payloads.
/// </summary>
/// <remarks>
/// The payload is only useful if its keys, units and methodology never drift without the
/// benchmark changing, and if a run that measured nothing produces no payload rather than an
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
    public async Task AGatedRunBecomesThreePayloadsWithStableKeysAndUnitsAsync()
    {
        WriteExport("WriteBenchmarks", Write10k);

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(0, result.ToString());
        result.Stdout.ShouldContain("Examined 1 benchmark case(s) from 1 file(s)");

        const string Key = "WriteBenchmarks.WriteAsync(Count=10000)";

        using (JsonDocument allocations = ReadPayload("allocations.json"))
        {
            JsonElement root = allocations.RootElement;
            root.GetProperty("scope")
                .GetString()
                .ShouldBe("parquet-sourcegenerator/benchmarks/regression-gate");
            string methodology = root.GetProperty("methodology").GetString()!;
            methodology.ShouldStartWith("benchmarkdotnet-0.14.0;net8.0;");
            methodology.ShouldEndWith("-x64;allocated-bytes-per-op-v1");
            methodology.ShouldNotContain("EPYC");

            JsonElement allocated = root.GetProperty("metrics")
                .GetProperty(Key)
                .GetProperty("allocatedBytes");
            allocated.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Number);
            allocated.GetProperty("value").GetInt64().ShouldBe(1_135_142);
            allocated.GetProperty("unit").GetString().ShouldBe("bytes");
        }

        using (JsonDocument gc = ReadPayload("gc.json"))
        {
            // The gen0 budget follows the CPU cache, so GC counts compare per processor model.
            gc.RootElement.GetProperty("methodology")
                .GetString()
                .ShouldEndWith(";AMD EPYC 9V74;gc-collections-per-1000-ops-v1");
            JsonElement metrics = gc.RootElement.GetProperty("metrics").GetProperty(Key);

            // Normalised per 1000 operations, and a string because Diffra's canonical JSON
            // accepts only integer number tokens.
            JsonElement gen0 = metrics.GetProperty("gen0CollectionsPer1000Ops");
            gen0.GetProperty("value").GetString().ShouldBe("146.484375");
            gen0.GetProperty("unit").GetString().ShouldBe("count/1000-ops");
            metrics
                .GetProperty("gen2CollectionsPer1000Ops")
                .GetProperty("value")
                .GetString()
                .ShouldBe("0.0");
        }

        using (JsonDocument time = ReadPayload("time.json"))
        {
            // Time compares only on the same processor model.
            time.RootElement.GetProperty("methodology")
                .GetString()
                .ShouldEndWith(";AMD EPYC 9V74;mean-ns-per-op-v1");
            JsonElement mean = time
                .RootElement.GetProperty("metrics")
                .GetProperty(Key)
                .GetProperty("meanNanoseconds");
            mean.GetProperty("value").GetString().ShouldBe("239597.0");
            mean.GetProperty("unit").GetString().ShouldBe("ns");
        }
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

    private async Task<ScriptResult> RunAsync()
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
        psi.ArgumentList.Add("scripts/BenchmarkDiffraPayload.cs");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(ResultsDirectory);
        psi.ArgumentList.Add(OutputDirectory);

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
