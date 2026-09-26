using System.Globalization;

namespace Parquet.SourceGenerator.Tools.DerivedReport;

/// <summary>The state view: one tree's derived outputs as they are, with no comparison.</summary>
public sealed record StateReport(
    string Label,
    IReadOnlyList<Table> Overview,
    IReadOnlyList<StateSection> Sections
);

/// <summary>Every file of one output kind, in the review's reading order.</summary>
public sealed record StateSection(string Title, bool Expanded, IReadOnlyList<StateFile> Files);

/// <summary>One derived file and its full content.</summary>
public sealed record StateFile(string Path, IReadOnlyList<string> Lines);

/// <summary>
/// Builds the state view from one tree written by scripts/DerivedOutputs.cs: an overview of the
/// numbers each report carries (API surface, generated and hand-written code metrics, duplication,
/// call graph), then every file in full. Everything is read from the tree, ordinal-sorted, so the
/// same tree always gives the same report.
/// </summary>
internal sealed class State(string tree)
{
    public StateReport Build(string label) =>
        new(
            label,
            new[] { Api(), GeneratedMetrics(), SourceMetrics(), Duplication(), CallGraph() }
                .OfType<Table>()
                .ToList(),
            Sections
                .All.Select(rule => new StateSection(
                    rule.Title,
                    rule.Expanded,
                    Files()
                        .Where(p => Sections.Of(p) == rule.Title)
                        .Select(p => new StateFile(p, Reports.Lines(tree, p)))
                        .ToList()
                ))
                .Where(s => s.Files.Count > 0)
                .ToList()
        );

    private Table? Api()
    {
        var rows = Models("golden/", ".api.txt")
            .Select(model =>
            {
                var shape = Reports
                    .Lines(tree, $"golden/{model}.api.shape.txt")
                    .Where(l => !l.StartsWith('#'))
                    .Select(Reports.Pairs)
                    .FirstOrDefault(p => p.Count > 0);
                var entries = Reports.Entries(tree, $"golden/{model}.api.txt");
                return (IReadOnlyList<string>)
                    [
                        model,
                        shape?.GetValueOrDefault("MEMBERS") ?? "—",
                        shape?.GetValueOrDefault("PARAMETERS") ?? "—",
                        Count(entries.Count(Drift.IsType)),
                        Count(entries.Count),
                    ];
            })
            .ToList();
        return rows.Count == 0
            ? null
            : new Table(
                "Emitted public API",
                ["Model", "Public members", "Parameters", "Public types", "Signatures"],
                rows,
                []
            );
    }

    private Table? GeneratedMetrics()
    {
        string[] fields =
        [
            "EMITTER",
            "ELOC",
            "CC",
            "METHODS",
            "MAXCC",
            "ELOC_PER_MEMBER",
            "ERRORS",
        ];
        var rows = Models("metrics/generated/", ".metrics.txt")
            .Select(model =>
            {
                var summary = Reports.Summary(tree, $"metrics/generated/{model}.metrics.txt", "S:");
                return (IReadOnlyList<string>)
                    [model, .. fields.Select(f => summary.GetValueOrDefault(f) ?? "—")];
            })
            .ToList();
        return rows.Count == 0
            ? null
            : new Table(
                "Emitted code",
                [
                    "Model",
                    "Emitter",
                    "Executable lines",
                    "Complexity",
                    "Methods",
                    "Worst method",
                    "Lines per member",
                    "Compile errors",
                ],
                rows,
                []
            );
    }

    private Table? SourceMetrics()
    {
        var rows = Files()
            .Where(p =>
                p.StartsWith("metrics/", StringComparison.Ordinal)
                && p.EndsWith(".metrics.txt", StringComparison.Ordinal)
                && !p.StartsWith("metrics/generated/", StringComparison.Ordinal)
            )
            .Select(file =>
            {
                var summary = Reports.Summary(tree, file, "A:");
                return (IReadOnlyList<string>)
                    [
                        Path.GetFileName(file)[..^".metrics.txt".Length],
                        summary.GetValueOrDefault("MI") ?? "—",
                        summary.GetValueOrDefault("CC") ?? "—",
                        summary.GetValueOrDefault("CL") ?? "—",
                        summary.GetValueOrDefault("ELOC") ?? "—",
                        Count(Reports.Keyed(tree, file).Count),
                    ];
            })
            .ToList();
        return rows.Count == 0
            ? null
            : new Table(
                "Hand-written code (src/)",
                [
                    "Assembly",
                    "Maintainability",
                    "Complexity",
                    "Coupling",
                    "Executable lines",
                    "Entries",
                ],
                rows,
                []
            );
    }

    private Table? Duplication()
    {
        const string File = "metrics/duplication.txt";
        var totals = Reports.Header(tree, File, "# totals:");
        return totals.Count == 0
            ? null
            : new Table(
                "Duplication",
                ["Clusters", "Duplicated tokens"],
                [
                    [
                        totals.GetValueOrDefault("clusters") ?? "—",
                        totals.GetValueOrDefault("duplicated-tokens") ?? "—",
                    ],
                ],
                []
            );
    }

    private Table? CallGraph()
    {
        var rows = Files()
            .Where(p =>
                p.StartsWith("callgraph/", StringComparison.Ordinal)
                && p.EndsWith(".callgraph.txt", StringComparison.Ordinal)
            )
            .Select(file =>
            {
                var stats = Reports.Header(tree, file, "# stats:");
                return (IReadOnlyList<string>)
                    [
                        Path.GetFileName(file)[..^".callgraph.txt".Length],
                        stats.GetValueOrDefault("nodes") ?? "—",
                        stats.GetValueOrDefault("edges") ?? "—",
                        stats.GetValueOrDefault("call-sites") ?? "—",
                        stats.GetValueOrDefault("unresolved") ?? "—",
                    ];
            })
            .ToList();
        return rows.Count == 0
            ? null
            : new Table(
                "Call graph",
                ["Graph", "Nodes", "Edges", "Call sites", "Unresolved call sites"],
                rows,
                []
            );
    }

    private List<string> Files() => Sections.Files(tree).ToList();

    private List<string> Models(string prefix, string suffix) =>
        Files()
            .Where(p =>
                p.StartsWith(prefix, StringComparison.Ordinal)
                && p.EndsWith(suffix, StringComparison.Ordinal)
                && p.IndexOf('/', prefix.Length) < 0
            )
            .Select(p => p[prefix.Length..^suffix.Length])
            .ToList();

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}
