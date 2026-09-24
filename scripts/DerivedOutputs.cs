#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

// -----------------------------------------------------------------------------
// DerivedOutputs.cs
//
// Produces every derived review output for a checkout into one directory:
//
//   <out>/golden/     emitted source of each golden model (*.g.cs), its signature-only API
//                     (*.api.txt) and shape summary (*.api.shape.txt) — GoldenCodeGenRegressionTests
//   <out>/metrics/    code metrics for src/ and, under generated/, for each golden model;
//                     duplication.txt — CodeMetrics.cs, Duplication.cs
//   <out>/callgraph/  static call-graph edges and Mermaid pages — CallGraph.cs
//   <out>/*.md        step-summary fragments
//
// None of it is checked in: the code is the source of truth. CI runs this for a pull request's
// head and for its merge base, renders the difference with scripts/RenderDerivedDiff.cs, uploads
// both trees as the `derived-outputs` artifact and posts the diff as a sticky PR comment.
//
// The gates that remain run as part of producing the head: the golden models must parse and (for
// driver-generated ones) compile, emitted code must compile against its GoldenModels/ declaration
// (ERRORS=0), CodeMetricsConfig.txt must be valid, and the call graph must satisfy its cycle,
// fan-out and layering rules. A failing gate fails this script.
//
// A base commit from before derived outputs existed has no GoldenCorpus; for it the checked-in
// golden files, metrics and call-graph baselines ARE its derived output, so they are copied into
// the same layout instead of regenerated. Everything is always generated with THIS script's
// recipe, run against the --repo tree.
//
// Usage:
//   dotnet run scripts/DerivedOutputs.cs                         # this checkout -> artifacts/
//   dotnet run scripts/DerivedOutputs.cs -- --out <dir> --repo <checkout>
// Needs the .NET 8 and 10 SDKs; restores what it builds.
// -----------------------------------------------------------------------------

string? repo = null;
string? outArg = null;
string[] argv = Environment.GetCommandLineArgs();
for (int i = 1; i < argv.Length; i++)
{
    if (argv[i] == "--repo" && i + 1 < argv.Length)
        repo = argv[++i];
    else if (argv[i] == "--out" && i + 1 < argv.Length)
        outArg = argv[++i];
    else
    {
        Console.Error.WriteLine($"Unknown argument: {argv[i]}");
        return 2;
    }
}

repo = Path.GetFullPath(repo ?? FindRepoRoot(Directory.GetCurrentDirectory()));
string output = Path.GetFullPath(outArg ?? Path.Combine(repo, "artifacts"));
string golden = Path.Combine(output, "golden");
string metrics = Path.Combine(output, "metrics");
string callgraph = Path.Combine(output, "callgraph");
foreach (string dir in new[] { golden, metrics, callgraph })
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}
Directory.CreateDirectory(output);

const string TestProject =
    "test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj";

if (!File.Exists(Path.Combine(repo, "test/Parquet.SourceGenerator.Tests/GoldenCorpus.cs")))
{
    Console.WriteLine($"{repo} predates derived outputs; copying its checked-in equivalents.");
    CopyLegacy(repo, golden, metrics, callgraph);
    return 0;
}

Run(
    repo,
    "dotnet",
    new Dictionary<string, string> { ["GOLDEN_OUTPUT_DIR"] = golden },
    "test",
    TestProject,
    "--configuration",
    "Release",
    "--filter",
    "FullyQualifiedName~GoldenCodeGenRegressionTests"
);
Run(
    repo,
    "dotnet",
    null,
    "run",
    "scripts/CodeMetrics.cs",
    "--",
    "--out",
    metrics,
    "--golden",
    golden,
    "--summary",
    Path.Combine(output, "code-metrics.md")
);
Run(
    repo,
    "dotnet",
    null,
    "run",
    "scripts/Duplication.cs",
    "--",
    "--report",
    Path.Combine(metrics, "duplication.txt"),
    "--summary",
    Path.Combine(output, "duplication.md")
);
Run(
    repo,
    "dotnet",
    null,
    "run",
    "scripts/CallGraph.cs",
    "--",
    "--out",
    callgraph,
    "--golden",
    golden
);

Console.WriteLine($"Derived outputs written to {output}.");
return 0;

static void Run(
    string workingDirectory,
    string fileName,
    Dictionary<string, string>? environment,
    params string[] arguments
)
{
    var start = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
    };
    foreach (string argument in arguments)
        start.ArgumentList.Add(argument);
    foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        start.Environment[key] = value;

    Console.WriteLine($"> {fileName} {string.Join(' ', arguments)}");
    using Process process =
        Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start");
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine(
            $"'{fileName} {arguments[0]} {arguments[1]}' exited {process.ExitCode}."
        );
        Environment.Exit(process.ExitCode);
    }
}

static void CopyLegacy(string repo, string golden, string metrics, string callgraph)
{
    string goldenFiles = Path.Combine(repo, "test/Parquet.SourceGenerator.Tests/GoldenFiles");
    Copy(goldenFiles, golden, f => !f.EndsWith(".metrics.txt", StringComparison.Ordinal));
    Copy(
        goldenFiles,
        Path.Combine(metrics, "generated"),
        f => f.EndsWith(".metrics.txt", StringComparison.Ordinal)
    );
    Copy(Path.Combine(repo, "metrics"), metrics, _ => true);
    Copy(
        Path.Combine(repo, "graph"),
        callgraph,
        f => f.EndsWith(".callgraph.txt", StringComparison.Ordinal)
    );
    Copy(
        Path.Combine(repo, "docs"),
        callgraph,
        f => f is "callgraph.md" or "callgraph-generated.md"
    );
}

static void Copy(string from, string to, Func<string, bool> include)
{
    if (!Directory.Exists(from))
        return;
    Directory.CreateDirectory(to);
    foreach (
        string file in Directory
            .GetFiles(from)
            .Where(f => include(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
    )
    {
        File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }
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
