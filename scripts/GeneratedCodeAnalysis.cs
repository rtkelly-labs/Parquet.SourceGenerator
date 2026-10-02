#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

// -----------------------------------------------------------------------------
// GeneratedCodeAnalysis.cs
//
// Runs every analyzer the shipped src/ projects run — NetAnalyzers, Meziantou, Roslynator,
// the IDE code-style rules, the CA1502/CA1505/CA1506 metric gates and the trim/AOT analyzers —
// over the golden models' emitted source, and FAILS on any finding. See
// docs/50-GENERATED-CODE-ANALYSIS.md.
//
// The analysis itself is two ordinary builds, analysis/GeneratedCode.Modern and
// analysis/GeneratedCode.Legacy, so a finding here is exactly the warning a consumer with the same
// rules enabled would get. This script runs them against a published golden directory and turns
// their SARIF logs into:
//
//   <out>/generated-diagnostics.txt   models examined, then findings per rule and per golden model
//   --summary <file>                  a Markdown table for the step summary
//
// Gate: any finding at all fails the script, whatever analysis/GeneratedCodeBaseline.props says
// (it is empty and stays empty). The failure names each rule, its count and the first locations.
//
// The gate cannot pass by examining nothing. It also fails when a project compiled no golden file,
// when the number of emitted files compiled differs from the number of golden model declarations
// in test/Parquet.SourceGenerator.Tests/GoldenModels/, when either backend is missing, or when a
// build left no SARIF log.
//
// --self-test is the positive control: it runs the same pipeline over a seeded golden directory
// whose two files each break CA2007, and succeeds only if the gate fails and names that rule for
// both backends. If it ever passes with no finding, the gate is blind.
//
// Usage (reads the golden models the test suite publishes, so run
// `dotnet test --filter GoldenCodeGenRegressionTests` first):
//   dotnet run scripts/GeneratedCodeAnalysis.cs
//   dotnet run scripts/GeneratedCodeAnalysis.cs -- --golden <dir> --out <dir> --summary <file>
//   dotnet run scripts/GeneratedCodeAnalysis.cs -- --self-test
// -----------------------------------------------------------------------------

string repo = FindRepoRoot(Directory.GetCurrentDirectory());
string golden = Path.Combine(repo, "artifacts", "golden");
string output = Path.Combine(repo, "artifacts", "analysis");
string? summaryPath = null;
bool selfTest = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--golden" && i + 1 < args.Length)
        golden = Path.GetFullPath(args[++i]);
    else if (args[i] == "--out" && i + 1 < args.Length)
        output = Path.GetFullPath(args[++i]);
    else if (args[i] == "--summary" && i + 1 < args.Length)
        summaryPath = Path.GetFullPath(args[++i]);
    else if (args[i] == "--self-test")
        selfTest = true;
    else
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        return 2;
    }
}

if (selfTest)
    return SelfTest(repo);

Directory.CreateDirectory(output);
int modelDeclarations = Directory
    .GetFiles(Path.Combine(repo, "test", "Parquet.SourceGenerator.Tests", "GoldenModels"), "*.cs")
    .Length;
Result result = Analyze(repo, golden, output, modelDeclarations, announce: true);

if (summaryPath is not null)
    File.WriteAllText(summaryPath, Summarize(result));

Console.WriteLine(
    $"Generated-code analysis: examined {result.Examined.Sum(e => e.Value)} emitted files "
        + $"({string.Join(", ", result.Examined.Select(e => $"{e.Key} {e.Value}"))}); "
        + $"{result.Findings.Count} findings in {result.Findings.Select(f => f.Rule).Distinct().Count()} rules; "
        + $"report at {Path.Combine(output, "generated-diagnostics.txt")}."
);
return result.Problems.Count == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------

static Result Analyze(string repo, string golden, string output, int expectedFiles, bool announce)
{
    string[] projects = ["GeneratedCode.Modern", "GeneratedCode.Legacy"];
    string sarifDirectory = Path.Combine(
        Path.GetTempPath(),
        "psg-generated-code-analysis-" + Environment.ProcessId
    );
    Directory.CreateDirectory(sarifDirectory);

    var findings = new List<Finding>();
    var descriptions = new SortedDictionary<string, string>(StringComparer.Ordinal);
    var examined = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var problems = new List<string>();

    int goldenFiles = Directory.Exists(golden) ? Directory.GetFiles(golden, "*.g.cs").Length : 0;
    if (goldenFiles != expectedFiles)
    {
        problems.Add(
            $"{golden} holds {goldenFiles} emitted files but {expectedFiles} golden models are declared; "
                + "the analysis would not examine every model. Publish them first: "
                + "dotnet test test/Parquet.SourceGenerator.Tests --filter GoldenCodeGenRegressionTests"
        );
    }

    foreach (string project in projects)
    {
        string sarif = Path.Combine(sarifDirectory, project + ".sarif");
        if (File.Exists(sarif))
            File.Delete(sarif);

        // Warnings are errors here, so findings print through the build and land in the SARIF log
        // either way; only errors reach the console, to keep a failing log readable.
        int exit = Run(
            repo,
            "dotnet",
            "build",
            $"analysis/{project}/{project}.csproj",
            "--configuration",
            "Release",
            "--no-incremental",
            "-consoleLoggerParameters:ErrorsOnly;NoSummary",
            $"-p:GoldenDirectory={golden}",
            $"-p:ErrorLog={sarif}%2Cversion=2.1"
        );

        // Examined = what the build copied in and compiled. The copy is recreated on every build.
        string copied = Path.Combine(repo, "analysis", project, "obj", "golden");
        int files = Directory.Exists(copied) ? Directory.GetFiles(copied, "*.g.cs").Length : 0;
        examined[
            project.Replace("GeneratedCode.", "", StringComparison.Ordinal).ToLowerInvariant()
        ] = files;
        if (files == 0)
            problems.Add($"{project} examined no emitted file.");

        if (!File.Exists(sarif))
        {
            // No log means the compiler never ran (restore or golden-copy failure); the build
            // output above says why. Nothing to count, and certainly not a pass.
            problems.Add($"{project}: no SARIF log was produced (build exit {exit}).");
            continue;
        }

        int before = findings.Count;
        ReadSarif(sarif, findings, descriptions);
        if (exit != 0 && findings.Count == before)
            problems.Add($"{project}: the build failed without reporting a finding (exit {exit}).");
    }

    // Only findings in the emitted source count. The model declarations it is compiled with are
    // scaffolding (their analyzer diagnostics are off in .editorconfig), and a compiler error in
    // them already failed the build above.
    var generated = findings
        .Where(f => f.File.EndsWith(".g.cs", StringComparison.Ordinal))
        .ToList();
    int total = examined.Values.Sum();
    if (total != expectedFiles)
    {
        problems.Add(
            $"{total} emitted files were compiled ({string.Join(", ", examined.Select(e => $"{e.Key} {e.Value}"))}) "
                + $"but {expectedFiles} golden models are declared."
        );
    }

    var byRule = generated
        .GroupBy(f => f.Rule, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .ToList();
    foreach (var rule in byRule)
    {
        descriptions.TryGetValue(rule.Key, out string? description);
        problems.Add($"{rule.Key} x{rule.Count()}: {description}");
    }

    var report = new StringBuilder();
    report.Append("# Analyzer findings on the golden models' emitted source.\n");
    report.Append(
        "# Generated by scripts/GeneratedCodeAnalysis.cs; see docs/50-GENERATED-CODE-ANALYSIS.md.\n"
    );
    report.Append(
        $"# examined {total} emitted files ({string.Join(", ", examined.Select(e => $"{e.Key} {e.Value}"))})\n"
    );
    report.Append($"# total {generated.Count}\n\n");
    foreach (var rule in byRule)
    {
        descriptions.TryGetValue(rule.Key, out string? description);
        report.Append($"{rule.Key} {rule.Count()}  {description}\n");
        foreach (
            var model in rule.GroupBy(f => f.File, StringComparer.Ordinal)
                .OrderBy(m => m.Key, StringComparer.Ordinal)
        )
        {
            report.Append($"  {model.Key} {model.Count()}\n");
        }
    }
    File.WriteAllText(Path.Combine(output, "generated-diagnostics.txt"), report.ToString());
    Directory.Delete(sarifDirectory, recursive: true);

    if (announce && problems.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("GENERATED-CODE ANALYSIS FAILED");
        foreach (string problem in problems)
            Console.WriteLine($"::error title=Generated-code analysis::{problem}");
        foreach (var rule in byRule)
        {
            foreach (var f in rule.Take(5))
                Console.WriteLine($"  {rule.Key} {f.File}:{f.Line}");
            if (rule.Count() > 5)
                Console.WriteLine($"  {rule.Key} ... and {rule.Count() - 5} more");
        }
        Console.WriteLine(
            "Fix the emitter (src/**/Emitter). Never add the rule to the baseline, NoWarn or .editorconfig."
        );
    }

    return new Result(generated, descriptions, examined, problems);
}

static int SelfTest(string repo)
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "psg-generated-code-selftest-" + Environment.ProcessId
    );
    string golden = Path.Combine(root, "golden");
    string output = Path.Combine(root, "out");
    Directory.CreateDirectory(golden);
    Directory.CreateDirectory(output);

    // No usings, as emitted: the await has no ConfigureAwait(false), which CA2007 and MA0004 reject.
    const string Violation =
        "// <auto-generated/>\n#nullable enable\nnamespace Seeded;\n"
        + "internal static class @@NAME@@\n{\n"
        + "    internal static async global::System.Threading.Tasks.Task RunAsync()\n    {\n"
        + "        await global::System.Threading.Tasks.Task.Delay(1);\n    }\n}\n";
    File.WriteAllText(
        Path.Combine(golden, "SeededParquetExtensions.g.cs"),
        Violation.Replace("@@NAME@@", "SeededParquetExtensions", StringComparison.Ordinal)
    );
    File.WriteAllText(
        Path.Combine(golden, "SeededParquetLegacyExtensions.g.cs"),
        Violation.Replace("@@NAME@@", "SeededParquetLegacyExtensions", StringComparison.Ordinal)
    );

    try
    {
        Result result = Analyze(repo, golden, output, expectedFiles: 2, announce: false);
        var seeded = result
            .Findings.Where(f => f.Rule == "CA2007")
            .Select(f => f.File)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = ["SeededParquetExtensions.g.cs", "SeededParquetLegacyExtensions.g.cs"];
        if (result.Problems.Count == 0 || !seeded.SequenceEqual(expected, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"::error title=Generated-code analysis::POSITIVE CONTROL FAILED: a seeded CA2007 "
                    + $"violation in both backends was not reported (reported for: "
                    + $"{(seeded.Length == 0 ? "neither" : string.Join(", ", seeded))}; problems: {result.Problems.Count}). "
                    + "The gate would pass vacuously."
            );
            return 1;
        }
        Console.WriteLine(
            $"Positive control passed: the seeded violation failed the gate ({string.Join("; ", result.Problems)})."
        );
        return 0;
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static string Summarize(Result result)
{
    var summary = new StringBuilder();
    summary.Append("### Generated-code analysis\n\n");
    summary.Append(
        $"Examined {result.Examined.Sum(e => e.Value)} emitted files "
            + $"({string.Join(", ", result.Examined.Select(e => $"{e.Key} {e.Value}"))}). "
            + $"**{result.Findings.Count} findings.** Any finding fails the job.\n\n"
    );
    if (result.Problems.Count > 0)
    {
        summary.Append("**Failed:**\n\n");
        foreach (string problem in result.Problems)
            summary.Append($"- {problem}\n");
        summary.Append('\n');
    }
    if (result.Findings.Count > 0)
    {
        summary.Append("| Rule | Findings | Description |\n|---|---:|---|\n");
        foreach (
            var rule in result
                .Findings.GroupBy(f => f.Rule, StringComparer.Ordinal)
                .OrderByDescending(r => r.Count())
                .ThenBy(r => r.Key, StringComparer.Ordinal)
        )
        {
            result.Descriptions.TryGetValue(rule.Key, out string? description);
            summary.Append(
                $"| {rule.Key} | {rule.Count()} | {description?.Replace("|", "\\|", StringComparison.Ordinal)} |\n"
            );
        }
    }
    return summary.ToString();
}

static void ReadSarif(
    string path,
    List<Finding> findings,
    SortedDictionary<string, string> descriptions
)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
    {
        if (
            run.TryGetProperty("tool", out var tool)
            && tool.GetProperty("driver").TryGetProperty("rules", out var rules)
        )
        {
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.TryGetProperty("shortDescription", out var text))
                    descriptions[rule.GetProperty("id").GetString()!] = text.GetProperty("text")
                        .GetString()!;
            }
        }

        if (!run.TryGetProperty("results", out var results))
            continue;
        foreach (var result in results.EnumerateArray())
        {
            // Suppressed results (#pragma, [SuppressMessage]) are not findings.
            if (result.TryGetProperty("suppressions", out var s) && s.GetArrayLength() > 0)
                continue;
            // The log also records suggestion- and info-level results ("note"), which the build
            // does not report. Only what the build reports is a finding. Absent means "warning".
            string level = result.TryGetProperty("level", out var l) ? l.GetString()! : "warning";
            if (level is not ("warning" or "error"))
                continue;
            string rule = result.GetProperty("ruleId").GetString()!;
            string file = "(no location)";
            int line = 0;
            if (
                result.TryGetProperty("locations", out var locations)
                && locations.GetArrayLength() > 0
                && locations[0].TryGetProperty("physicalLocation", out var physical)
            )
            {
                file = Path.GetFileName(
                    Uri.UnescapeDataString(
                        physical.GetProperty("artifactLocation").GetProperty("uri").GetString()!
                    )
                );
                if (
                    physical.TryGetProperty("region", out var region)
                    && region.TryGetProperty("startLine", out var startLine)
                )
                    line = startLine.GetInt32();
            }
            findings.Add(new Finding(rule, file, line));
        }
    }
}

static int Run(string workingDirectory, string fileName, params string[] arguments)
{
    var start = new ProcessStartInfo(fileName)
    {
        UseShellExecute = false,
        WorkingDirectory = workingDirectory,
    };
    foreach (string argument in arguments)
        start.ArgumentList.Add(argument);
    Console.WriteLine($"> {fileName} {string.Join(' ', arguments)}");
    using Process process =
        Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start");
    process.WaitForExit();
    return process.ExitCode;
}

static string FindRepoRoot(string start)
{
    for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Parquet.SourceGenerator.slnx")))
            return dir.FullName;
    }
    throw new InvalidOperationException("Could not locate the repository root.");
}

internal sealed record Finding(string Rule, string File, int Line);

internal sealed record Result(
    List<Finding> Findings,
    SortedDictionary<string, string> Descriptions,
    SortedDictionary<string, int> Examined,
    List<string> Problems
);
