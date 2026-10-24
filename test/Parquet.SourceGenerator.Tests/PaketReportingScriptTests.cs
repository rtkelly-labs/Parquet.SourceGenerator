using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Guards the two scripts behind the Paket pilot's reporting (ADR 0004):
/// <c>scripts/PaketOutdated.cs</c>, the weekly outdated report, and
/// <c>scripts/DependencySnapshot.cs</c>, which turns a pinned component-detection scan into the
/// dependency submission snapshot. Both are fed fixtures, so no test touches nuget.org or GitHub.
/// </summary>
[Trait("Category", "Integration")]
public class PaketReportingScriptTests
{
    // ------------------------------------------------------------------ PaketOutdated.cs

    private const string OutdatedResults = """
        {
          "declared": [
            { "group": "Main", "name": "BenchmarkDotNet" },
            { "group": "Main", "name": "Parquet.Net" },
            { "group": "Main", "name": "System.Memory" }
          ],
          "inRange": [
            { "group": "Main", "name": "Parquet.Net", "locked": "6.1.0", "latest": "6.2.0" },
            { "group": "Main", "name": "System.Memory", "locked": "4.6.0", "latest": "4.6.3" }
          ],
          "anyVersion": [
            { "group": "Main", "name": "BenchmarkDotNet", "locked": "0.14.0", "latest": "0.15.8" },
            { "group": "Main", "name": "Parquet.Net", "locked": "6.1.0", "latest": "7.0.0" },
            { "group": "Main", "name": "System.Memory", "locked": "4.6.0", "latest": "4.6.3" },
            { "group": "Main", "name": "Perfolizer", "locked": "0.3.17", "latest": "0.6.1" }
          ]
        }
        """;

    [Fact]
    public async Task OutdatedReportListsOnlyDeclaredPackagesOutsideTheirRangesAsync()
    {
        OutdatedRun run = await RunPaketOutdatedAsync(OutdatedResults);

        run.Result.ExitCode.ShouldBe(0, run.Result.ToString());
        run.Verdict.ShouldBe("true");
        string inside = Section(run.Report, "Inside the ranges");
        string outside = Section(run.Report, "Outside the ranges");

        inside.ShouldContain("| Main | Parquet.Net | 6.1.0 | 6.2.0 |");
        inside.ShouldContain("| Main | System.Memory | 4.6.0 | 4.6.3 |");

        // Declared, newest outside the range: listed.
        outside.ShouldContain("| Main | BenchmarkDotNet | 0.14.0 | 0.15.8 |");
        // Declared, with an in-range update AND a newer one outside: the outside one is listed.
        outside.ShouldContain("| Main | Parquet.Net | 6.1.0 | 7.0.0 |");
        // Declared, newest version inside the range: already in the first section only.
        outside.ShouldNotContain("System.Memory");
        // Transitive (not declared): never listed.
        run.Report.ShouldNotContain("Perfolizer");
    }

    [Fact]
    public async Task OutdatedReportVerdictIsFalseWhenNothingInsideTheRangesIsOutdatedAsync()
    {
        string results = OutdatedResults.Replace(
            """
            "inRange": [
                { "group": "Main", "name": "Parquet.Net", "locked": "6.1.0", "latest": "6.2.0" },
                { "group": "Main", "name": "System.Memory", "locked": "4.6.0", "latest": "4.6.3" }
              ],
            """,
            "\"inRange\": [],",
            StringComparison.Ordinal
        );
        results.ShouldNotBe(OutdatedResults);

        OutdatedRun run = await RunPaketOutdatedAsync(results);

        run.Result.ExitCode.ShouldBe(0, run.Result.ToString());
        run.Verdict.ShouldBe("false");
        Section(run.Report, "Inside the ranges").ShouldContain("Nothing to take.");
        // With nothing in range, System.Memory's newest version counts as outside it.
        Section(run.Report, "Outside the ranges").ShouldContain("System.Memory");
    }

    [Fact]
    public async Task OutdatedReportWritesNoVerdictWhenItFailsAsync()
    {
        OutdatedRun run = await RunPaketOutdatedAsync("""{ "declared": [] }""");

        run.Result.ExitCode.ShouldNotBe(0, run.Result.ToString());
        run.Verdict.ShouldBeNull("a failed run must not leave a verdict the workflow could read");
    }

    [Fact]
    public void PaketCoreInTheScriptMatchesThePaketTool()
    {
        string root = FindRepoRoot();
        string script = IOFile.ReadAllText(Path.Combine(root, "scripts", "PaketOutdated.cs"));
        string core = Regex
            .Match(script, @"^#:package Paket\.Core@(?<v>\S+)$", RegexOptions.Multiline)
            .Groups["v"]
            .Value;

        using JsonDocument tools = JsonDocument.Parse(
            IOFile.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json"))
        );
        string tool = tools
            .RootElement.GetProperty("tools")
            .GetProperty("paket")
            .GetProperty("version")
            .GetString()!;

        core.ShouldNotBeEmpty();
        core.ShouldBe(tool, "scripts/PaketOutdated.cs and the paket tool read the same paket.lock");
    }

    // ------------------------------------------------------------------ DependencySnapshot.cs

    private const string Scan = """
        {
          "resultCode": "Success",
          "sourceDirectory": "/repo",
          "componentsFound": [
            { "component": { "id": "A 1.0.0 - NuGet", "packageUrl": { "Type": "nuget", "Name": "A", "Version": "1.0.0" } } },
            { "component": { "id": "B 2.0.0 - NuGet", "packageUrl": { "Type": "nuget", "Name": "B", "Version": "2.0.0" } } },
            { "component": { "id": "T 3.0.0 - NuGet", "packageUrl": { "Type": "nuget", "Name": "T", "Version": "3.0.0" } }, "isDevelopmentDependency": true },
            { "component": { "id": "sdk 8.0.414 - DotNet" } }
          ],
          "dependencyGraphs": {
            "/repo/src/Lib/Lib.csproj": {
              "graph": { "A 1.0.0 - NuGet": ["B 2.0.0 - NuGet"], "B 2.0.0 - NuGet": null },
              "explicitlyReferencedComponentIds": ["A 1.0.0 - NuGet"]
            },
            "/repo/benchmarks/Bench/Bench.csproj": {
              "graph": { "T 3.0.0 - NuGet": null },
              "explicitlyReferencedComponentIds": ["T 3.0.0 - NuGet"]
            },
            "/repo/global.json": {
              "graph": { "sdk 8.0.414 - DotNet": null },
              "explicitlyReferencedComponentIds": ["sdk 8.0.414 - DotNet"]
            }
          }
        }
        """;

    [Fact]
    public async Task SnapshotHasOneManifestPerProjectWithRelationshipsAndEdgesAsync()
    {
        SnapshotRun run = await RunDependencySnapshotAsync(Scan, "benchmarks/Bench/Bench.csproj");

        run.Result.ExitCode.ShouldBe(0, run.Result.ToString());
        using JsonDocument snapshot = JsonDocument.Parse(run.Snapshot!);
        JsonElement root = snapshot.RootElement;
        root.GetProperty("sha").GetString().ShouldBe("abc123");
        root.GetProperty("ref").GetString().ShouldBe("refs/heads/main");
        root.GetProperty("detector").GetProperty("version").GetString().ShouldBe("8.0.4");

        JsonElement manifests = root.GetProperty("manifests");
        // global.json resolves no package URL, so it is left out.
        manifests.TryGetProperty("global.json", out _).ShouldBeFalse();

        JsonElement lib = manifests.GetProperty("src/Lib/Lib.csproj");
        lib.GetProperty("file")
            .GetProperty("source_location")
            .GetString()
            .ShouldBe("src/Lib/Lib.csproj");
        JsonElement a = lib.GetProperty("resolved").GetProperty("pkg:nuget/A@1.0.0");
        a.GetProperty("relationship").GetString().ShouldBe("direct");
        a.GetProperty("scope").GetString().ShouldBe("runtime");
        a.GetProperty("dependencies")[0].GetString().ShouldBe("pkg:nuget/B@2.0.0");
        lib.GetProperty("resolved")
            .GetProperty("pkg:nuget/B@2.0.0")
            .GetProperty("relationship")
            .GetString()
            .ShouldBe("indirect");

        manifests
            .GetProperty("benchmarks/Bench/Bench.csproj")
            .GetProperty("resolved")
            .GetProperty("pkg:nuget/T@3.0.0")
            .GetProperty("scope")
            .GetString()
            .ShouldBe("development");
    }

    [Theory]
    // The required (Paket-managed) project is missing from the scan.
    [InlineData(
        "\"/repo/benchmarks/Bench/Bench.csproj\"",
        "\"/repo/benchmarks/Other/Other.csproj\""
    )]
    // component-detection did not finish cleanly.
    [InlineData("\"resultCode\": \"Success\"", "\"resultCode\": \"PartialSuccess\"")]
    // Nothing resolves to a package URL, so the snapshot would be empty.
    [InlineData("\"packageUrl\"", "\"noPackageUrl\"")]
    public async Task SnapshotFailsClosedAsync(string original, string broken)
    {
        string scan = Scan.Replace(original, broken, StringComparison.Ordinal);
        scan.ShouldNotBe(Scan);

        SnapshotRun run = await RunDependencySnapshotAsync(scan, "benchmarks/Bench/Bench.csproj");

        run.Result.ExitCode.ShouldNotBe(0, run.Result.ToString());
        run.Snapshot.ShouldBeNull("a refused scan must not leave a snapshot to submit");
    }

    // ------------------------------------------------------------------ plumbing

    private static string Section(string report, string heading)
    {
        int start = report.IndexOf("## " + heading, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, report);
        int next = report.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        return next < 0 ? report[start..] : report[start..next];
    }

    private sealed record OutdatedRun(ScriptResult Result, string Report, string? Verdict);

    private sealed record SnapshotRun(ScriptResult Result, string? Snapshot);

    private static async Task<OutdatedRun> RunPaketOutdatedAsync(string results)
    {
        using var temp = new TempDirectory();
        string resultsPath = temp.Write("results.json", results);
        string report = Path.Combine(temp.Path, "report.md");
        string verdict = Path.Combine(temp.Path, "verdict.txt");

        ScriptResult result = await RunScriptAsync(
            "scripts/PaketOutdated.cs",
            $"--results \"{resultsPath}\" --out \"{report}\" --verdict \"{verdict}\""
        );
        return new OutdatedRun(
            result,
            IOFile.Exists(report) ? await IOFile.ReadAllTextAsync(report) : "",
            IOFile.Exists(verdict) ? await IOFile.ReadAllTextAsync(verdict) : null
        );
    }

    private static async Task<SnapshotRun> RunDependencySnapshotAsync(string scan, string required)
    {
        using var temp = new TempDirectory();
        string scanPath = temp.Write("scan.json", scan);
        string snapshot = Path.Combine(temp.Path, "snapshot.json");

        ScriptResult result = await RunScriptAsync(
            "scripts/DependencySnapshot.cs",
            $"--manifest \"{scanPath}\" --out \"{snapshot}\" --sha abc123 --ref refs/heads/main "
                + $"--job-id 1 --correlator test --detector-version 8.0.4 --require {required}"
        );
        return new SnapshotRun(
            result,
            IOFile.Exists(snapshot) ? await IOFile.ReadAllTextAsync(snapshot) : null
        );
    }

    private static async Task<ScriptResult> RunScriptAsync(string script, string arguments)
    {
        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");

        var psi = new ProcessStartInfo
        {
            FileName = IOFile.Exists(homeDotnet) ? homeDotnet : "dotnet",
            Arguments = $"run {script} -- {arguments}",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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

        using Process process = Process.Start(psi).ShouldNotBeNull();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (IOFile.Exists(Path.Combine(dir.FullName, "scripts", "PaketOutdated.cs")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find repository root containing scripts/PaketOutdated.cs"
        );
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public override string ToString() =>
            $"Exit {ExitCode}\nStdout:\n{Stdout}\nStderr:\n{Stderr}";
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "paket-reporting-" + Guid.NewGuid().ToString("N")
            );

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Write(string name, string content)
        {
            string file = System.IO.Path.Combine(Path, name);
            IOFile.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup only.
            }
        }
    }
}
