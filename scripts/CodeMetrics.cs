#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

#:package Microsoft.Build.Locator@1.7.8
#:package Microsoft.CodeAnalysis.CSharp@4.14.0
#:package Microsoft.CodeAnalysis.CSharp.Workspaces@4.14.0
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild@4.14.0
#:package Microsoft.CodeAnalysis.AnalyzerUtilities@5.6.0

// Layer 2 only. The V5 emitter's output targets the classic Parquet.Net 4.x/5.x DataColumn API and
// does not compile against Parquet.Net 6 at all, so the legacy golden file has to be measured
// against the version the repository actually tests it against — 4.25.0, the same pin as
// test/PackageConsumptionLegacy. The v6 golden files get their references from the test project
// (Parquet.Net 6.1.0 + Apache.Arrow), so nothing here is a second source of truth for those.
#:package Parquet.Net@4.25.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

// -----------------------------------------------------------------------------
// CodeMetrics.cs
//
// Layers 1 and 2 of #251. See docs/21-CODE-METRICS.md and docs/22-GENERATED-CODE-METRICS.md.
//
//   Layer 1 — the hand-written product code under src/, one report per project, written to
//             artifacts/metrics/ (gitignored). NOT checked in and NOT gated on drift: the numbers
//             are a pure function of src/, so a committed copy only restated the code and forced
//             a refresh commit on every change. CI publishes the reports as a build artifact and
//             step summary; the gate on hand-written code is CA1502/CA1505/CA1506 against
//             CodeMetricsConfig.txt, which this script validates.
//   Layer 2 — the GENERATED code: every golden model the test suite publishes to artifacts/golden/
//             (GoldenCorpus), compiled against its declaration in
//             test/Parquet.SourceGenerator.Tests/GoldenModels/, one report per model in
//             artifacts/metrics/generated/. Also a report, not a drift gate — CI diffs it against
//             the pull request's base. The one thing that fails the run is emitted code that does
//             not compile (ERRORS > 0): that is an emitter defect, not a number that moved.
//
// The metrics are Roslyn's own — Maintainability Index, Cyclomatic Complexity, Class Coupling,
// Depth of Inheritance, Source/Executable Lines — computed by
// Microsoft.CodeAnalysis.CodeMetrics.CodeAnalysisMetricData, the exact type the CA1502/CA1505/
// CA1506 analyzers and the Microsoft `Metrics.exe` tool use.
//
// Why this script and not `Microsoft.CodeAnalysis.Metrics`: that package's entry point is
// `Metrics/Metrics.exe`, a .NET Framework, Windows-only executable (PE32 / MS Windows, with an
// `.exe.config` beside it). Invoking its `/t:Metrics` MSBuild target on Linux or macOS fails with
// "cannot execute binary file" (exit 126), so it cannot run on this repository's ubuntu-latest CI
// or on a macOS or Linux developer machine. `Microsoft.CodeAnalysis.AnalyzerUtilities` ships the
// same computation as a netstandard2.0 library, so this script is that tool, cross-platform, with
// a diff-friendly renderer instead of XML.
//
// Determinism: the Roslyn version that parses the source and computes the metrics is pinned above,
// not taken from the SDK, so the numbers do not move when a developer or a runner updates their
// SDK. Every ordering is `StringComparer.Ordinal` (see docs/17-GENERATED-API-BASELINES.md for why
// that matters here).
//
// Usage (layer 2 reads the golden models the test suite publishes, so run
// `dotnet test --filter GoldenCodeGenRegressionTests` first):
//   dotnet run scripts/CodeMetrics.cs                        # report src/ and the generated code
//   dotnet run scripts/CodeMetrics.cs -- --summary out.md    # also write a Markdown report
//   dotnet run scripts/CodeMetrics.cs -- --out <dir>         # where reports go (default
//                                                            # artifacts/metrics)
//   dotnet run scripts/CodeMetrics.cs -- --golden <dir>      # published golden models (default
//                                                            # artifacts/golden)
//   dotnet run scripts/CodeMetrics.cs -- --src-only          # layer 1 only
// -----------------------------------------------------------------------------

// The measured set is the hand-written product code. Test, benchmark, sample and tooling projects
// are deliberately out of scope: their metrics churn with every new test case and say nothing
// about the maintainability of the shipped generator. Generated code is layer 2 of #251.
var measured = new (string Project, string? TargetFramework)[]
{
    ("src/Parquet.SourceGenerator/Parquet.SourceGenerator.csproj", null),
    ("src/Parquet.SourceGenerator.Legacy/Parquet.SourceGenerator.Legacy.csproj", null),
    // Multi-targeted. netstandard2.0 is the widest target and the one every other TFM is a
    // superset of, so it is pinned here: measuring "whichever TFM MSBuild happened to hand back
    // first" would not be deterministic.
    (
        "src/Parquet.SourceGenerator.Attributes/Parquet.SourceGenerator.Attributes.csproj",
        "netstandard2.0"
    ),
};

// Layer 2. The golden models are the emitter's real output, published by the test suite together
// with their .api.txt (see docs/17-GENERATED-API-BASELINES.md). The model declarations under
// GoldenModels/ are the consumer-side types the emitted fragments extend: without them the
// compilation has unresolved types and the metrics are fiction.
const string GoldenModelDirectory = "test/Parquet.SourceGenerator.Tests/GoldenModels";
const string GoldenHostProject =
    "test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj";

bool srcOnly = args.Contains("--src-only", StringComparer.Ordinal);
string? summaryPath = null;
string reportDirectory = Path.Combine("artifacts", "metrics");
string goldenDirectory = Path.Combine("artifacts", "golden");
for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--golden", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        goldenDirectory = args[i + 1];
    }

    if (string.Equals(args[i], "--summary", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        summaryPath = args[i + 1];
    }

    if (string.Equals(args[i], "--out", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        reportDirectory = args[i + 1];
    }
}

string repoRoot = FindRepositoryRoot();
Directory.SetCurrentDirectory(repoRoot);
Directory.CreateDirectory(reportDirectory);

ValidateCodeMetricsConfig("CodeMetricsConfig.txt");

MSBuildLocator.RegisterDefaults();

var failures = new List<string>();
var allRows = new List<MetricRow>();

foreach (var (projectPath, tfm) in measured)
{
    string name = Path.GetFileNameWithoutExtension(projectPath);
    Console.WriteLine($"Measuring {name}...");

    var rows = await MeasureAsync(projectPath, tfm).ConfigureAwait(false);
    allRows.AddRange(rows);

    string reportPath = Path.Combine(reportDirectory, name + ".metrics.txt");
    WriteIfChanged(reportPath, Render(name, rows));
    Console.WriteLine($"  wrote {reportPath} ({rows.Count} entries)");
}

// -------------------------------------------------------------------------------------------
// Layer 2: the generated code.
//
// Every published *.g.cs is measured against the model declaration it was generated for. The
// member count on the summary line is READ from the published .api.txt rather than recomputed, so
// there is exactly one definition of "an emitted public member" in the repository
// (docs/17-GENERATED-API-BASELINES.md owns it).
// -------------------------------------------------------------------------------------------

var generatedSummaries = new List<GeneratedSummary>();
var generatedRows = new List<MetricRow>();

if (!srcOnly)
{
    Console.WriteLine($"Measuring generated code in {goldenDirectory}...");
    var goldenFiles = Directory.Exists(goldenDirectory)
        ? Directory
            .GetFiles(goldenDirectory, "*.g.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList()
        : new List<string>();
    if (goldenFiles.Count == 0)
    {
        throw new InvalidOperationException(
            $"No golden models found under {goldenDirectory}. They are published by the test suite:\n"
                + "  dotnet test test/Parquet.SourceGenerator.Tests --filter GoldenCodeGenRegressionTests\n"
                + "Run that first, or pass --src-only to measure the hand-written code alone."
        );
    }

    string generatedDirectory = Path.Combine(reportDirectory, "generated");
    Directory.CreateDirectory(generatedDirectory);
    var host = await LoadGoldenHostAsync(GoldenHostProject).ConfigureAwait(false);

    foreach (string goldenPath in goldenFiles)
    {
        string stem = Path.GetFileName(goldenPath)[..^".g.cs".Length];
        string modelPath = Path.Combine(GoldenModelDirectory, ModelFileNameFor(stem));
        string apiBaselinePath = Path.Combine(goldenDirectory, stem + ".api.txt");

        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException(
                $"{goldenPath} has no model declaration at {modelPath}.\n"
                    + "  Generated code is a fragment: it extends a type the consumer wrote, so it cannot be\n"
                    + "  compiled — and therefore cannot be measured — on its own. Add the declaration the\n"
                    + "  golden model generates from. See docs/22-GENERATED-CODE-METRICS.md."
            );
        }

        if (!File.Exists(apiBaselinePath))
        {
            throw new InvalidOperationException(
                $"{goldenPath} has no API file at {apiBaselinePath}; the emitted member count on the\n"
                    + "  metrics summary line is read from it and must not be recomputed here."
            );
        }

        var (rows, summary) = await MeasureGeneratedAsync(
                goldenPath,
                modelPath,
                apiBaselinePath,
                stem,
                host
            )
            .ConfigureAwait(false);
        generatedSummaries.Add(summary);
        generatedRows.AddRange(rows);

        string reportPath = Path.Combine(generatedDirectory, stem + ".metrics.txt");
        WriteIfChanged(reportPath, RenderGenerated(stem, summary, rows));
        Console.WriteLine($"  wrote {reportPath} ({rows.Count} entries)");

        if (summary.CompileErrors > 0)
        {
            failures.Add(
                $"Emitted code for '{stem}' does not compile against {modelPath}: "
                    + $"{summary.CompileErrors} error(s), listed above. This is an emitter defect "
                    + "(or a model declaration that no longer mirrors its GoldenCorpus entry). "
                    + "See docs/22-GENERATED-CODE-METRICS.md."
            );
        }
    }
}

if (summaryPath is not null)
{
    File.WriteAllText(
        summaryPath,
        RenderSummary(allRows)
            + (srcOnly ? string.Empty : RenderGeneratedSummary(generatedSummaries, generatedRows)),
        new UTF8Encoding(false)
    );
    Console.WriteLine($"Wrote metrics summary to {summaryPath}");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    foreach (string f in failures)
    {
        Console.Error.WriteLine(f);
        Console.Error.WriteLine();
    }
    return 1;
}

Console.WriteLine($"Code metrics written to {reportDirectory}.");
return 0;

// ---------------------------------------------------------------------------------------------

// CodeMetricsConfig.txt is what CA1502 / CA1505 / CA1506 read their thresholds from, and it has a
// dangerous failure mode: a single unrecognised entry — `NamedType` where the parser wants `Type`,
// say — makes all three rules stop reporting ENTIRELY and SILENTLY. No CA1509 diagnostic is
// emitted, the build goes green, and the gate is gone. Verified empirically against
// Microsoft.CodeAnalysis.NetAnalyzers as shipped with the .NET 9 SDK. This validator is the guard:
// the gate that the build depends on is checked by the gate that CI depends on.
static void ValidateCodeMetricsConfig(string path)
{
    if (!File.Exists(path))
    {
        throw new InvalidOperationException(
            $"{path} is missing. CA1502/CA1505/CA1506 read their thresholds from it; without it the\n"
                + "build-time half of the code metrics gate is silently disabled."
        );
    }

    // Empirically verified accepted spellings. `NamedType` is NOT one of them.
    string[] validKinds = ["Assembly", "Namespace", "Type", "Method", "Field", "Property", "Event"];
    string[] validRules = ["CA1501", "CA1502", "CA1505", "CA1506"];

    int lineNumber = 0;
    foreach (string raw in ReadAllLines(path))
    {
        lineNumber++;
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            continue;
        }

        int colon = line.IndexOf(':', StringComparison.Ordinal);
        string left = colon < 0 ? string.Empty : line[..colon].Trim();
        string right = colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
        string rule = left;
        string? kind = null;
        int paren = left.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0 && left.EndsWith(")", StringComparison.Ordinal))
        {
            rule = left[..paren].Trim();
            kind = left[(paren + 1)..^1].Trim();
        }

        bool ok =
            colon > 0
            && validRules.Contains(rule, StringComparer.Ordinal)
            && (kind is null || validKinds.Contains(kind, StringComparer.Ordinal))
            && int.TryParse(
                right,
                System.Globalization.NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _
            );

        if (!ok)
        {
            throw new InvalidOperationException(
                $"{path}({lineNumber}): malformed entry '{raw}'.\n"
                    + "  Expected 'RuleId: Threshold' or 'RuleId(SymbolKind): Threshold'.\n"
                    + $"  Valid rules: {string.Join(", ", validRules)}.\n"
                    + $"  Valid symbol kinds: {string.Join(", ", validKinds)}.\n"
                    + "  A malformed entry silently disables CA1502, CA1505 and CA1506 for the whole build."
            );
        }
    }
}

static string FindRepositoryRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (
        dir is not null && !File.Exists(Path.Combine(dir.FullName, "Parquet.SourceGenerator.slnx"))
    )
    {
        dir = dir.Parent;
    }

    return dir?.FullName
        ?? throw new InvalidOperationException("Could not locate the repository root.");
}

static async Task<List<MetricRow>> MeasureAsync(string projectPath, string? targetFramework)
{
    var properties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Release so the measured compilation is the one CI builds and ships.
        ["Configuration"] = "Release",
    };
    if (targetFramework is not null)
    {
        properties["TargetFramework"] = targetFramework;
    }

    using var workspace = MSBuildWorkspace.Create(properties);
    var loadFailures = new List<string>();
    workspace.WorkspaceFailed += (_, e) =>
    {
        if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
        {
            loadFailures.Add(e.Diagnostic.Message);
        }
    };

    var project = await workspace.OpenProjectAsync(projectPath).ConfigureAwait(false);
    if (loadFailures.Count > 0)
    {
        throw new InvalidOperationException(
            $"MSBuild could not load {projectPath}:\n  " + string.Join("\n  ", loadFailures)
        );
    }

    var compilation =
        await project.GetCompilationAsync().ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No compilation produced for {projectPath}.");

    // A compilation with unresolved references produces degraded IOperation trees and therefore
    // wrong metrics. Failing loudly here is the difference between a baseline and a fiction.
    var errors = compilation
        .GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d => d.ToString())
        .Distinct(StringComparer.Ordinal)
        .OrderBy(s => s, StringComparer.Ordinal)
        .Take(10)
        .ToList();
    if (errors.Count > 0)
    {
        throw new InvalidOperationException(
            $"{projectPath} does not compile cleanly under the metrics host; metrics would be\n"
                + "unreliable. First errors:\n  "
                + string.Join("\n  ", errors)
        );
    }

    var context = new CodeMetricsAnalysisContext(compilation, CancellationToken.None);
    var data = await CodeAnalysisMetricData.ComputeAsync(context).ConfigureAwait(false);

    var rows = new List<MetricRow>();
    Collect(data, rows);
    rows.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
    return rows;
}

static void Collect(CodeAnalysisMetricData data, List<MetricRow> rows)
{
    string? key = KeyFor(data.Symbol);
    if (key is not null)
    {
        rows.Add(
            new MetricRow(
                key,
                data.MaintainabilityIndex,
                data.CyclomaticComplexity,
                data.CoupledNamedTypes.Count,
                data.DepthOfInheritance,
                data.SourceLines,
                data.ExecutableLines
            )
        );
    }

    foreach (var child in data.Children)
    {
        Collect(child, rows);
    }
}

static string? KeyFor(ISymbol symbol) =>
    symbol.Kind switch
    {
        SymbolKind.Assembly => "A:" + symbol.Name,
        // The global namespace has an empty name and no metrics of its own worth a line.
        SymbolKind.Namespace => ((INamespaceSymbol)symbol).IsGlobalNamespace
            ? null
            : "N:" + symbol.ToDisplayString(Display.Format),
        SymbolKind.NamedType => "T:" + symbol.ToDisplayString(Display.Format),
        SymbolKind.Method => "M:" + symbol.ToDisplayString(Display.Format),
        SymbolKind.Property => "P:" + symbol.ToDisplayString(Display.Format),
        SymbolKind.Event => "E:" + symbol.ToDisplayString(Display.Format),
        // Fields carry no complexity — every field is MI=100, CC=0 — so a line per field would be
        // pure churn on rename with no signal. Deliberately omitted; see docs/21-CODE-METRICS.md.
        SymbolKind.Field => null,
        _ => null,
    };

static string Render(string projectName, List<MetricRow> rows)
{
    var sb = new StringBuilder();
    sb.Append("# Code metrics report: ").Append(projectName).Append('\n');
    sb.Append("# Generated by scripts/CodeMetrics.cs from src/. Not checked in.\n");
    sb.Append("# Regenerate: dotnet run scripts/CodeMetrics.cs\n");
    sb.Append("# Kinds: A assembly, N namespace, T type, M method, P property, E event.\n");
    sb.Append(
        "# MI maintainability index 0-100 (higher is better), CC cyclomatic complexity,\n"
            + "# CL class coupling, DIT depth of inheritance, SLOC source lines, ELOC executable lines.\n"
            + "# Ordinal-sorted, so two reports diff cleanly.\n"
    );
    foreach (var row in rows)
    {
        sb.Append(row.Key).Append(" | ").Append(row.Metrics).Append('\n');
    }

    return sb.ToString();
}

static string[] ReadAllLines(string path) =>
    File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

static string RenderSummary(List<MetricRow> allRows)
{
    // TargetParser and friends are linked into both generator projects, so the same entity is
    // measured twice. One row per entity in the report.
    var rows = allRows.GroupBy(r => r.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();

    var sb = new StringBuilder();
    sb.Append("## Code metrics (hand-written `src/`)\n\n");

    AppendTable(
        sb,
        "Least maintainable types (lowest MI)",
        rows.Where(r => r.Key.StartsWith("T:", StringComparison.Ordinal))
            .OrderBy(r => r.MaintainabilityIndex)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(10)
    );
    AppendTable(
        sb,
        "Most complex methods (highest cyclomatic complexity)",
        rows.Where(r => r.Key.StartsWith("M:", StringComparison.Ordinal))
            .OrderByDescending(r => r.CyclomaticComplexity)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(10)
    );
    AppendTable(
        sb,
        "Highest class coupling (types)",
        rows.Where(r => r.Key.StartsWith("T:", StringComparison.Ordinal))
            .OrderByDescending(r => r.ClassCoupling)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(10)
    );

    sb.Append(
        "\n> The Maintainability Index is a Halstead-derived 1990s formula and a poor absolute\n"
            + "> judgement of quality. It is used here as a change detector, not a score to optimise.\n"
            + "> See `docs/21-CODE-METRICS.md`.\n"
    );
    return sb.ToString();
}

static void AppendTable(StringBuilder sb, string title, IEnumerable<MetricRow> rows)
{
    sb.Append("### ").Append(title).Append("\n\n");
    sb.Append("| Entity | MI | CC | CL | SLOC |\n|:--|--:|--:|--:|--:|\n");
    foreach (var r in rows)
    {
        // The parameter list is what makes an entity unique in the full report; in a summary it is
        // unreadable noise, so the summary shows the name and an arity.
        string name = r.Key[2..];
        int paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            string parameters = name[(paren + 1)..^1];
            int arity = parameters.Length == 0 ? 0 : Depth(parameters) + 1;
            name = string.Create(CultureInfo.InvariantCulture, $"{name[..paren]}(..{arity}..)");
        }

        sb.Append("| `")
            .Append(name)
            .Append("` | ")
            .Append(r.MaintainabilityIndex.ToString(CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(r.CyclomaticComplexity.ToString(CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(r.ClassCoupling.ToString(CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(r.SourceLines.ToString(CultureInfo.InvariantCulture))
            .Append(" |\n");
    }

    sb.Append('\n');
}

// Counts top-level commas in a parameter list, ignoring those inside generic arguments.
static int Depth(string parameters)
{
    int commas = 0;
    int nesting = 0;
    foreach (char c in parameters)
    {
        if (c is '<' or '[')
        {
            nesting++;
        }
        else if (c is '>' or ']')
        {
            nesting--;
        }
        else if (c == ',' && nesting == 0)
        {
            commas++;
        }
    }

    return commas;
}

// ---------------------------------------------------------------------------------------------
// Layer 2: the generated code.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Opens the project the golden files belong to, once, and keeps its reference closure and parse
/// options. Those are what make the measured compilation the one a real consumer gets: the same
/// Parquet.Net, the same Apache.Arrow, and — this matters — the same preprocessor symbols, so the
/// `#if NET6_0_OR_GREATER` branches that are live for a modern consumer are the branches measured.
/// </summary>
static async Task<GoldenHost> LoadGoldenHostAsync(string hostProjectPath)
{
    var properties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Configuration"] = "Release",
    };

    using var workspace = MSBuildWorkspace.Create(properties);
    var loadFailures = new List<string>();
    workspace.WorkspaceFailed += (_, e) =>
    {
        if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
        {
            loadFailures.Add(e.Diagnostic.Message);
        }
    };

    var project = await workspace.OpenProjectAsync(hostProjectPath).ConfigureAwait(false);
    if (loadFailures.Count > 0)
    {
        throw new InvalidOperationException(
            $"MSBuild could not load {hostProjectPath}:\n  " + string.Join("\n  ", loadFailures)
        );
    }

    var compilation =
        await project.GetCompilationAsync().ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No compilation produced for {hostProjectPath}.");

    var modernReferences = compilation.References.ToList();
    if (modernReferences.Count < 10)
    {
        throw new InvalidOperationException(
            $"{hostProjectPath} resolved only {modernReferences.Count} references. The project has not\n"
                + "  been restored; measuring the generated code against a stub reference set would produce\n"
                + "  fictional metrics. Run `dotnet restore` first."
        );
    }

    // The V5 emitter's output needs the classic Parquet.Net API. Swap the v6 assembly out of the
    // closure and the 4.25.0 one — pinned by this script's own #:package directive — in.
    string legacyParquet = Path.Combine(AppContext.BaseDirectory, "Parquet.dll");
    if (!File.Exists(legacyParquet))
    {
        throw new InvalidOperationException(
            $"Expected the pinned Parquet.Net 4.25.0 assembly at {legacyParquet}. It is the reference\n"
                + "  set for the legacy golden file, which cannot compile against Parquet.Net 6."
        );
    }

    var legacyReferences = modernReferences
        .Where(r =>
            r is not PortableExecutableReference pe
            || !Path.GetFileName(pe.FilePath ?? string.Empty)
                .StartsWith("Parquet.", StringComparison.Ordinal)
        )
        .ToList();
    legacyReferences.Add(MetadataReference.CreateFromFile(legacyParquet));

    return new GoldenHost(
        (CSharpParseOptions)project.ParseOptions!,
        modernReferences,
        legacyReferences
    );
}

/// <summary>Maps <c>FooParquetExtensions</c> / <c>FooParquetLegacyExtensions</c> to <c>Foo.cs</c>.</summary>
static string ModelFileNameFor(string stem) =>
    (
        stem.EndsWith("ParquetLegacyExtensions", StringComparison.Ordinal)
            ? stem[..^"ParquetLegacyExtensions".Length]
        : stem.EndsWith("ParquetExtensions", StringComparison.Ordinal)
            ? stem[..^"ParquetExtensions".Length]
        : throw new InvalidOperationException(
            $"Golden file stem '{stem}' does not end in ParquetExtensions or "
                + "ParquetLegacyExtensions; the model declaration it belongs to cannot be resolved."
        )
    ) + ".cs";

static async Task<(List<MetricRow> Rows, GeneratedSummary Summary)> MeasureGeneratedAsync(
    string goldenPath,
    string modelPath,
    string apiBaselinePath,
    string stem,
    GoldenHost host
)
{
    bool legacy = stem.EndsWith("ParquetLegacyExtensions", StringComparison.Ordinal);

    var goldenTree = CSharpSyntaxTree.ParseText(
        File.ReadAllText(goldenPath),
        host.ParseOptions,
        path: goldenPath
    );
    var modelTree = CSharpSyntaxTree.ParseText(
        File.ReadAllText(modelPath),
        host.ParseOptions,
        path: modelPath
    );

    var compilation = CSharpCompilation.Create(
        stem,
        [goldenTree, modelTree],
        legacy ? host.LegacyReferences : host.ModernReferences,
        new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable
        )
    );

    // Unlike layer 1 this does NOT throw on a compile error: the errors are listed, counted as
    // ERRORS on the summary line, and fail the run once every model has been measured. Emitted
    // code that does not compile is a defect in the emitter, not a broken measurement host. See
    // docs/22-GENERATED-CODE-METRICS.md.
    var errors = compilation
        .GetDiagnostics()
        .Where(d =>
            d.Severity == DiagnosticSeverity.Error
            && string.Equals(d.Location.SourceTree?.FilePath, goldenPath, StringComparison.Ordinal)
        )
        .ToList();

    var context = new CodeMetricsAnalysisContext(compilation, CancellationToken.None);
    var data = await CodeAnalysisMetricData.ComputeAsync(context).ConfigureAwait(false);

    // Only entities physically declared in the golden file. The model declaration is scaffolding
    // for the compiler, not emitted code, and must never reach the baseline.
    var rows = new List<MetricRow>();
    var topLevelTypes = new List<CodeAnalysisMetricData>();
    CollectGenerated(data, goldenPath, rows, topLevelTypes);
    rows.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));

    int members = CountApiMembers(apiBaselinePath);
    var coupled = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var type in topLevelTypes)
    {
        foreach (var coupledType in type.CoupledNamedTypes)
        {
            coupled.Add(coupledType.ToDisplayString(Display.Format));
        }
    }

    var summary = new GeneratedSummary(
        stem,
        legacy ? "V5" : "v6",
        members,
        topLevelTypes.Sum(t => t.CyclomaticComplexity),
        coupled.Count,
        topLevelTypes.Sum(t => t.SourceLines),
        topLevelTypes.Sum(t => t.ExecutableLines),
        errors.Count,
        rows.Count(r => r.Key.StartsWith("M:", StringComparison.Ordinal)),
        rows.Where(r => r.Key.StartsWith("M:", StringComparison.Ordinal))
            .Select(r => r.CyclomaticComplexity)
            .DefaultIfEmpty(0)
            .Max()
    );

    if (errors.Count > 0)
    {
        Console.WriteLine(
            $"  NOTE: {stem} emits {errors.Count} C# compile error(s); recorded as ERRORS on its summary line."
        );
        foreach (
            var group in errors
                .GroupBy(d => d.Id + ": " + d.GetMessage(CultureInfo.InvariantCulture))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            Console.WriteLine($"    {group.Count()}x {group.Key}");
        }
    }

    return (rows, summary);
}

/// <summary>
/// Walks the metric tree keeping only what the golden file itself declares. Assembly and namespace
/// rows are skipped: the namespace contains the model scaffolding too, so its numbers would not be
/// numbers about emitted code. The per-model totals live on the S: line instead.
/// </summary>
static void CollectGenerated(
    CodeAnalysisMetricData data,
    string goldenPath,
    List<MetricRow> rows,
    List<CodeAnalysisMetricData> topLevelTypes
)
{
    if (data.Symbol.Kind is SymbolKind.NamedType)
    {
        if (!DeclaredIn(data.Symbol, goldenPath))
        {
            return;
        }

        if (data.Symbol.ContainingType is null)
        {
            topLevelTypes.Add(data);
        }
    }

    string? key =
        data.Symbol.Kind is SymbolKind.Assembly or SymbolKind.Namespace ? null
        : DeclaredIn(data.Symbol, goldenPath) ? KeyFor(data.Symbol)
        : null;
    if (key is not null)
    {
        rows.Add(
            new MetricRow(
                key,
                data.MaintainabilityIndex,
                data.CyclomaticComplexity,
                data.CoupledNamedTypes.Count,
                data.DepthOfInheritance,
                data.SourceLines,
                data.ExecutableLines
            )
        );
    }

    foreach (var child in data.Children)
    {
        CollectGenerated(child, goldenPath, rows, topLevelTypes);
    }
}

static bool DeclaredIn(ISymbol symbol, string path) =>
    symbol.DeclaringSyntaxReferences.Any(r =>
        string.Equals(r.SyntaxTree.FilePath, path, StringComparison.Ordinal)
    );

/// <summary>
/// The emitted public member count, read from the sibling <c>.api.txt</c> with the same rule
/// <c>GeneratedApiBaseline.CountMembers</c> uses — every non-empty line that is not the
/// <c>#nullable enable</c> header. Deliberately a read, not a second computation: #244 already
/// established that this repository cannot afford two definitions of "emitted surface".
/// </summary>
static int CountApiMembers(string apiBaselinePath) =>
    ReadAllLines(apiBaselinePath)
        .Count(line =>
            line.Length > 0 && !string.Equals(line, "#nullable enable", StringComparison.Ordinal)
        );

static string RenderGenerated(string stem, GeneratedSummary summary, List<MetricRow> rows)
{
    var sb = new StringBuilder();
    sb.Append("# Generated code metrics report: ").Append(stem).Append('\n');
    sb.Append("# Generated by scripts/CodeMetrics.cs (layer 2 of #251). Not checked in.\n");
    sb.Append(
        "# Measured over "
            + stem
            + ".g.cs compiled against GoldenModels/"
            + ModelFileNameFor(stem)
            + ".\n"
    );
    sb.Append("# Kinds: S model summary, T type, M method, P property, E event.\n");
    sb.Append(
        "# S: MEMBERS emitted public members (read from the sibling .api.txt), CC cyclomatic\n"
            + "# complexity, CL class coupling, SLOC source lines, ELOC executable lines, ERRORS C#\n"
            + "# compile errors in the emitted code, METHODS emitted methods, MAXCC worst method CC,\n"
            + "# ELOC_PER_MEMBER emitted executable lines per emitted public member.\n"
            + "# Ordinal-sorted, so two reports diff cleanly. ERRORS must be 0. MI carries little\n"
            + "# signal for generated code. See docs/22-GENERATED-CODE-METRICS.md.\n"
    );
    sb.Append(summary.Render()).Append('\n');
    foreach (var row in rows)
    {
        sb.Append(row.Key).Append(" | ").Append(row.Metrics).Append('\n');
    }

    return sb.ToString();
}

static string RenderGeneratedSummary(
    List<GeneratedSummary> summaries,
    List<MetricRow> generatedRows
)
{
    var sb = new StringBuilder();
    sb.Append("\n## Generated code metrics (emitted `*.g.cs`)\n\n");
    sb.Append(
        "| Model | Emitter | Members | SLOC | ELOC | CC | Max method CC | CL | ELOC/member |\n"
    );
    sb.Append("|:--|:--|--:|--:|--:|--:|--:|--:|--:|\n");
    foreach (var s in summaries.OrderBy(s => s.Model, StringComparer.Ordinal))
    {
        sb.Append(
            string.Create(
                CultureInfo.InvariantCulture,
                $"| `{s.Model}` | {s.Emitter} | {s.Members} | {s.SourceLines} | {s.ExecutableLines} | {s.CyclomaticComplexity} | {s.MaxMethodComplexity} | {s.ClassCoupling} | {s.ExecutableLinesPerMember} |\n"
            )
        );
    }

    var methods = generatedRows
        .Where(r => r.Key.StartsWith("M:", StringComparison.Ordinal))
        .OrderByDescending(r => r.CyclomaticComplexity)
        .ThenBy(r => r.Key, StringComparer.Ordinal)
        .Take(5)
        .ToList();
    sb.Append("\n### Most complex emitted methods\n\n");
    sb.Append("| Method | CC | SLOC | ELOC |\n|:--|--:|--:|--:|\n");
    foreach (var r in methods)
    {
        sb.Append(
            string.Create(
                CultureInfo.InvariantCulture,
                $"| `{r.Key[2..]}` | {r.CyclomaticComplexity} | {r.SourceLines} | {r.ExecutableLines} |\n"
            )
        );
    }

    sb.Append(
        "\n> The Maintainability Index carries little signal for generated code: it is dominated\n"
            + "> by method length, and emitted methods are long by construction. See\n"
            + "> `docs/22-GENERATED-CODE-METRICS.md`.\n"
    );
    return sb.ToString();
}

static void WriteIfChanged(string path, string content)
{
    if (
        File.Exists(path)
        && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal)
    )
    {
        return;
    }

    File.WriteAllText(path, content, new UTF8Encoding(false));
}

internal sealed record MetricRow(
    string Key,
    int MaintainabilityIndex,
    int CyclomaticComplexity,
    int ClassCoupling,
    int? DepthOfInheritance,
    long SourceLines,
    long ExecutableLines
)
{
    public string Metrics =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"MI={MaintainabilityIndex} CC={CyclomaticComplexity} CL={ClassCoupling} DIT={(DepthOfInheritance.HasValue ? DepthOfInheritance.Value.ToString(CultureInfo.InvariantCulture) : "-")} SLOC={SourceLines} ELOC={ExecutableLines}"
        );
}

/// <summary>
/// The measurement host for the generated code: the reference closure and parse options of the
/// project the golden files belong to, loaded once.
/// </summary>
internal sealed record GoldenHost(
    CSharpParseOptions ParseOptions,
    List<MetadataReference> ModernReferences,
    List<MetadataReference> LegacyReferences
);

/// <summary>
/// The per-model summary line — the one line in the artifact that answers #251's actual question:
/// is the emitted code growing faster than the capability it emits?
/// </summary>
internal sealed record GeneratedSummary(
    string Model,
    string Emitter,
    int Members,
    int CyclomaticComplexity,
    int ClassCoupling,
    long SourceLines,
    long ExecutableLines,
    int CompileErrors,
    int Methods,
    int MaxMethodComplexity
)
{
    /// <summary>
    /// Emitted executable lines per emitted public member: volume over capability. Both halves are
    /// exact integers, so the quotient is deterministic; one decimal place is as much precision as
    /// the underlying counts justify.
    /// </summary>
    public string ExecutableLinesPerMember =>
        Members == 0
            ? "-"
            : ((double)ExecutableLines / Members).ToString("F1", CultureInfo.InvariantCulture);

    public string Render() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"S:{Model} | EMITTER={Emitter} MEMBERS={Members} CC={CyclomaticComplexity} CL={ClassCoupling} SLOC={SourceLines} ELOC={ExecutableLines} ERRORS={CompileErrors} METHODS={Methods} MAXCC={MaxMethodComplexity} ELOC_PER_MEMBER={ExecutableLinesPerMember}"
        );
}

internal static class Display
{
    // The display format. Fully qualified, no `global::`, parameter types but not parameter names —
    // a parameter rename is not a maintainability change and must not produce a diff.
    public static readonly SymbolDisplayFormat Format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.ExpandNullable
    );
}
