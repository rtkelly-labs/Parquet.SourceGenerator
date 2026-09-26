using System.Globalization;

namespace Parquet.SourceGenerator.Tools.DerivedReport;

/// <summary>
/// What the PR did to the derived outputs, read from the two trees' own reports (never from the
/// diff text): API member counts and types, generated and hand-written code metrics, duplication
/// and the call graph. A section appears only when one of its files changed. Values that did not
/// move are shown once; moved values read "before → after (delta)".
/// </summary>
internal sealed class Drift(string baseDir, string headDir, IReadOnlyList<FileChange> changes)
{
    public IReadOnlyList<Table> Build() =>
        new[] { Api(), GeneratedMetrics(), SourceMetrics(), Duplication(), CallGraph(), Other() }
            .OfType<Table>()
            .ToList();

    private Table? Api()
    {
        var models = ChangedModels("golden/", ".api.txt", ".api.shape.txt");
        if (models.Count == 0)
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>>();
        var typesAdded = new SortedSet<string>(StringComparer.Ordinal);
        var typesRemoved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string model in models)
        {
            var before = Reports.Entries(baseDir, $"golden/{model}.api.txt");
            var after = Reports.Entries(headDir, $"golden/{model}.api.txt");
            rows.Add([
                model,
                Moved(Shape(baseDir, model), Shape(headDir, model)),
                Count(after.Count(s => !before.Contains(s))),
                Count(before.Count(s => !after.Contains(s))),
            ]);
            typesAdded.UnionWith(after.Where(s => IsType(s) && !before.Contains(s)));
            typesRemoved.UnionWith(before.Where(s => IsType(s) && !after.Contains(s)));
        }

        var notes = new List<Note>();
        if (typesAdded.Count > 0)
        {
            notes.Add(new Note($"Public types added ({typesAdded.Count})", [.. typesAdded]));
        }

        if (typesRemoved.Count > 0)
        {
            notes.Add(new Note($"Public types removed ({typesRemoved.Count})", [.. typesRemoved]));
        }

        return new Table(
            "Emitted public API",
            ["Model", "Public members", "Signatures added", "Signatures removed"],
            rows,
            notes
        );

        static string? Shape(string root, string model) =>
            Reports
                .Lines(root, $"golden/{model}.api.shape.txt")
                .Where(l => !l.StartsWith('#'))
                .Select(l => Reports.Pairs(l).GetValueOrDefault("MEMBERS"))
                .FirstOrDefault(v => v is not null);
    }

    private Table? GeneratedMetrics()
    {
        string[] fields = ["ELOC", "CC", "METHODS", "MAXCC", "ELOC_PER_MEMBER", "ERRORS"];
        var rows = new List<IReadOnlyList<string>>();
        foreach (string model in ChangedModels("metrics/generated/", ".metrics.txt"))
        {
            string file = $"metrics/generated/{model}.metrics.txt";
            var before = Reports.Summary(baseDir, file, "S:");
            var after = Reports.Summary(headDir, file, "S:");
            if (fields.All(f => before.GetValueOrDefault(f) == after.GetValueOrDefault(f)))
            {
                continue;
            }

            rows.Add([
                model,
                .. fields.Select(f =>
                    Moved(before.GetValueOrDefault(f), after.GetValueOrDefault(f))
                ),
            ]);
        }

        return rows.Count == 0
            ? null
            : new Table(
                "Emitted code",
                [
                    "Model",
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
        var files = ChangedPaths(p =>
            p.StartsWith("metrics/", StringComparison.Ordinal)
            && p.EndsWith(".metrics.txt", StringComparison.Ordinal)
            && !p.StartsWith("metrics/generated/", StringComparison.Ordinal)
        );
        if (files.Count == 0)
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (string file in files)
        {
            var before = Reports.Summary(baseDir, file, "A:");
            var after = Reports.Summary(headDir, file, "A:");
            // Member lines keyed by id, the text before " | ": a key on both sides with different
            // numbers is a changed member.
            var beforeMembers = Reports.Keyed(baseDir, file);
            var afterMembers = Reports.Keyed(headDir, file);
            rows.Add([
                Path.GetFileName(file)[..^".metrics.txt".Length],
                Moved(before.GetValueOrDefault("MI"), after.GetValueOrDefault("MI")),
                Moved(before.GetValueOrDefault("CC"), after.GetValueOrDefault("CC")),
                Moved(before.GetValueOrDefault("CL"), after.GetValueOrDefault("CL")),
                Moved(before.GetValueOrDefault("ELOC"), after.GetValueOrDefault("ELOC")),
                Count(afterMembers.Keys.Count(k => !beforeMembers.ContainsKey(k))),
                Count(beforeMembers.Keys.Count(k => !afterMembers.ContainsKey(k))),
                Count(
                    afterMembers.Count(kv =>
                        beforeMembers.TryGetValue(kv.Key, out string? old)
                        && !string.Equals(old, kv.Value, StringComparison.Ordinal)
                    )
                ),
            ]);
        }

        return new Table(
            "Hand-written code (src/)",
            [
                "Assembly",
                "Maintainability",
                "Complexity",
                "Coupling",
                "Executable lines",
                "Members added",
                "Members removed",
                "Members changed",
            ],
            rows,
            []
        );
    }

    private Table? Duplication()
    {
        const string File = "metrics/duplication.txt";
        if (ChangedPaths(p => p == File).Count == 0)
        {
            return null;
        }

        var before = Reports.Header(baseDir, File, "# totals:");
        var after = Reports.Header(headDir, File, "# totals:");
        var beforeClusters = Reports.Entries(baseDir, File);
        var afterClusters = Reports.Entries(headDir, File);
        return new Table(
            "Duplication",
            ["Clusters", "Duplicated tokens", "Clusters new", "Clusters gone"],
            [
                [
                    Moved(
                        before.GetValueOrDefault("clusters"),
                        after.GetValueOrDefault("clusters")
                    ),
                    Moved(
                        before.GetValueOrDefault("duplicated-tokens"),
                        after.GetValueOrDefault("duplicated-tokens")
                    ),
                    Count(afterClusters.Count(k => !beforeClusters.Contains(k))),
                    Count(beforeClusters.Count(k => !afterClusters.Contains(k))),
                ],
            ],
            []
        );
    }

    private Table? CallGraph()
    {
        var graphs = ChangedPaths(p =>
            p.StartsWith("callgraph/", StringComparison.Ordinal)
            && p.EndsWith(".callgraph.txt", StringComparison.Ordinal)
        );
        if (graphs.Count == 0)
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (string file in graphs)
        {
            var before = Reports.Header(baseDir, file, "# stats:");
            var after = Reports.Header(headDir, file, "# stats:");
            var beforeEdges = Reports.Entries(baseDir, file);
            var afterEdges = Reports.Entries(headDir, file);
            rows.Add([
                Path.GetFileName(file)[..^".callgraph.txt".Length],
                Moved(before.GetValueOrDefault("nodes"), after.GetValueOrDefault("nodes")),
                Moved(before.GetValueOrDefault("edges"), after.GetValueOrDefault("edges")),
                Moved(
                    before.GetValueOrDefault("unresolved"),
                    after.GetValueOrDefault("unresolved")
                ),
                Count(afterEdges.Count(e => !beforeEdges.Contains(e))),
                Count(beforeEdges.Count(e => !afterEdges.Contains(e))),
            ]);
        }

        return new Table(
            "Call graph",
            ["Graph", "Nodes", "Edges", "Unresolved call sites", "Edges added", "Edges removed"],
            rows,
            []
        );
    }

    private Table? Other()
    {
        var other = changes.Where(c => Sections.Of(c.Path) == Sections.Other).ToList();
        return other.Count == 0
            ? null
            : new Table(
                "Other",
                ["File", "Status", "+", "−"],
                other
                    .Select(c =>
                        (IReadOnlyList<string>)[c.Path, c.Status, $"+{c.Added}", $"−{c.Removed}"]
                    )
                    .ToList(),
                []
            );
    }

    private List<string> ChangedPaths(Func<string, bool> predicate) =>
        changes.Select(c => c.Path).Where(predicate).ToList();

    // Model names whose file directly under `prefix`, with any of `suffixes`, changed.
    private List<string> ChangedModels(string prefix, params string[] suffixes) =>
        changes
            .Select(c => c.Path)
            .Where(p =>
                p.StartsWith(prefix, StringComparison.Ordinal) && p.IndexOf('/', prefix.Length) < 0
            )
            .Select(p =>
                suffixes.FirstOrDefault(s => p.EndsWith(s, StringComparison.Ordinal)) is { } s
                    ? p[prefix.Length..^s.Length]
                    : null
            )
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>An API line naming a type alone — no member arrow, no parameter list.</summary>
    public static bool IsType(string line) =>
        !line.Contains(" -> ", StringComparison.Ordinal)
        && !line.Contains('(', StringComparison.Ordinal);

    /// <summary>"1357" when unchanged, "1357 → 1210 (−147)" when moved, "—" where a side lacks it.</summary>
    public static string Moved(string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return after ?? "—";
        }

        string moved = $"{before ?? "—"} → {after ?? "—"}";
        if (
            decimal.TryParse(
                before,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal b
            )
            && decimal.TryParse(
                after,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal a
            )
        )
        {
            decimal delta = a - b;
            moved +=
                delta > 0
                    ? $" (+{delta.ToString(CultureInfo.InvariantCulture)})"
                    : $" (−{(-delta).ToString(CultureInfo.InvariantCulture)})";
        }

        return moved;
    }
}

/// <summary>Readers for the line-oriented reports scripts/DerivedOutputs.cs writes.</summary>
internal static class Reports
{
    public static string[] Lines(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    /// <summary>A report's data lines, comments dropped, as a set.</summary>
    public static HashSet<string> Entries(string root, string relative) =>
        Lines(root, relative)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A report's data lines keyed by id, the text before " | ".</summary>
    public static Dictionary<string, string> Keyed(string root, string relative)
    {
        var keyed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (
            string line in Lines(root, relative).Where(l => l.Length > 0 && !l.StartsWith('#'))
        )
        {
            int bar = line.IndexOf(" | ", StringComparison.Ordinal);
            keyed[bar < 0 ? line : line[..bar]] = bar < 0 ? "" : line[(bar + 3)..];
        }

        return keyed;
    }

    /// <summary>The KEY=VALUE fields after " | " on the first line starting with <paramref name="kind"/>.</summary>
    public static Dictionary<string, string> Summary(string root, string relative, string kind)
    {
        string? line = Lines(root, relative)
            .FirstOrDefault(l => l.StartsWith(kind, StringComparison.Ordinal));
        int bar = line?.IndexOf(" | ", StringComparison.Ordinal) ?? -1;
        return bar < 0 ? new(StringComparer.Ordinal) : Pairs(line![(bar + 3)..]);
    }

    /// <summary>The key=value fields of a header comment such as "# stats: nodes=226 edges=367".</summary>
    public static Dictionary<string, string> Header(string root, string relative, string header)
    {
        string? line = Lines(root, relative)
            .FirstOrDefault(l => l.StartsWith(header, StringComparison.Ordinal));
        return line is null ? new(StringComparer.Ordinal) : Pairs(line[header.Length..]);
    }

    public static Dictionary<string, string> Pairs(string text)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                pairs[token[..eq]] = token[(eq + 1)..];
            }
        }

        return pairs;
    }
}
