using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Guards the merge in <c>scripts/MutationSummary.cs</c> (#668). The nightly sweep is split into
/// partitions, so a partition that timed out, matched nothing or overlapped another could leave a
/// score that looks complete and is not. These are the negative controls: each removes one piece
/// of evidence and the merge must fail, naming it. The score itself never fails the run (#261).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MutationSummaryGateTests : IDisposable
{
    private const string Partitions = """
        {
          "projects": { "Gen.csproj": "Generator" },
          "partitions": [
            { "name": "a", "project": "Gen.csproj", "mutate": ["**/A.cs"] },
            { "name": "rest", "project": "Gen.csproj", "rest": true }
          ]
        }
        """;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "parquet-mutation-summary-" + Guid.NewGuid().ToString("N")
    );

    public MutationSummaryGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CompletePartitionsMergeIntoOneScoreAsync()
    {
        WriteReport("a", ("A.cs", "1", "Killed"), ("A.cs", "2", "Survived"), ("B.cs", "3", null));
        WriteReport("rest", ("A.cs", "1", null), ("A.cs", "2", null), ("B.cs", "3", "Killed"));

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(0, result.Describe());
        result.Stdout.ShouldContain(
            "Mutation score **66.7%** — 2 detected (2 killed, 0 timed out) / 3 tested"
        );
        result.Stdout.ShouldContain("Merged from 2 of 2 partition report(s)");
        result.Stdout.ShouldContain("| `src/Gen/A.cs` | 1 |");
        result.Stdout.ShouldNotContain("Incomplete report");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AMissingPartitionReportFailsTheMergeAsync()
    {
        WriteReport("a", ("A.cs", "1", "Killed"), ("B.cs", "3", null));

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("Partition 'rest': no report at");
        result.Stdout.ShouldContain("Incomplete report");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task APartitionThatTestedNothingFailsTheMergeAsync()
    {
        // Compile errors are reported by every partition, so they do not count as examined.
        WriteReport("a", ("A.cs", "1", null), ("B.cs", "3", null), ("C.cs", "4", "CompileError"));
        WriteReport("rest", ("A.cs", "1", "Killed"), ("B.cs", "3", "Killed"));

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("Partition 'a': its report tested no mutant");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OverlappingPartitionsFailTheMergeAsync()
    {
        WriteReport("a", ("A.cs", "1", "Killed"), ("B.cs", "3", null));
        WriteReport("rest", ("A.cs", "1", "Survived"), ("B.cs", "3", "Killed"));

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("Mutant 1 in src/Gen/A.cs was tested by both 'a' and 'rest'");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnUnreadableReportFailsTheMergeAsync()
    {
        WriteReport("a", ("A.cs", "1", "Killed"));
        Directory.CreateDirectory(Path.Combine(_dir, "stryker-report-rest"));
        await IOFile.WriteAllTextAsync(
            Path.Combine(_dir, "stryker-report-rest", "mutation-report.json"),
            "{ truncated"
        );

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("Partition 'rest':");
        result.Stderr.ShouldContain("is not valid JSON");
    }

    /// <summary>
    /// Writes a Stryker JSON report (schema 2) for one partition. A null status is a mutant outside
    /// the partition's globs, which Stryker reports as ignored by the mutate filter.
    /// </summary>
    private void WriteReport(string partition, params (string File, string Id, string? Status)[] m)
    {
        var files = new JsonObject();
        foreach (
            IGrouping<string, (string File, string Id, string? Status)> file in m.GroupBy(x =>
                x.File
            )
        )
        {
            var mutants = new JsonArray();
            foreach ((_, string id, string? status) in file)
            {
                mutants.Add(
                    new JsonObject
                    {
                        ["id"] = id,
                        ["mutatorName"] = "Equality mutation",
                        ["status"] = status ?? "Ignored",
                        ["statusReason"] = status is null ? "Removed by mutate filter" : "",
                    }
                );
            }

            files["/runner/work/src/Gen/" + file.Key] = new JsonObject
            {
                ["language"] = "cs",
                ["source"] = "",
                ["mutants"] = mutants,
            };
        }

        var report = new JsonObject
        {
            ["schemaVersion"] = "2",
            ["projectRoot"] = "/runner/work",
            ["files"] = files,
        };
        string dir = Path.Combine(_dir, "stryker-report-" + partition);
        Directory.CreateDirectory(dir);
        IOFile.WriteAllText(Path.Combine(dir, "mutation-report.json"), report.ToJsonString());
    }

    private async Task<ScriptResult> RunAsync()
    {
        string partitions = Path.Combine(_dir, "stryker-partitions.json");
        await IOFile.WriteAllTextAsync(partitions, Partitions);

        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");
        var psi = new ProcessStartInfo(IOFile.Exists(homeDotnet) ? homeDotnet : "dotnet")
        {
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            string argument in new[]
            {
                "run",
                "scripts/MutationSummary.cs",
                "--",
                "--partitions",
                partitions,
                "--report-path",
                Path.Combine(_dir, "stryker-report-{name}", "mutation-report.json"),
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
                homeDotnetDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (IOFile.Exists(Path.Combine(directory.FullName, "scripts", "MutationSummary.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root containing scripts/MutationSummary.cs."
        );
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Describe() => $"Stdout:\n{Stdout}\nStderr:\n{Stderr}";
    }
}
