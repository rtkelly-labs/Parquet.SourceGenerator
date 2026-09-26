namespace Parquet.SourceGenerator.Tools.DerivedReport;

/// <summary>
/// How derived files group into sections, ordered so the root change reads first: the emitted
/// public API, then the emitted code, then the numbers that follow from it. Both views use it.
/// </summary>
internal static class Sections
{
    public const string Other = "Other";

    private static readonly (string Title, bool Expanded, Func<string, bool> Owns)[] Rules =
    [
        (
            "Emitted public API",
            true,
            p =>
                p.StartsWith("golden/", StringComparison.Ordinal)
                && (
                    p.EndsWith(".api.txt", StringComparison.Ordinal)
                    || p.EndsWith(".api.shape.txt", StringComparison.Ordinal)
                )
        ),
        (
            "Emitted code",
            false,
            p =>
                p.StartsWith("golden/", StringComparison.Ordinal)
                && p.EndsWith(".g.cs", StringComparison.Ordinal)
        ),
        (
            "Generated-code metrics",
            false,
            p => p.StartsWith("metrics/generated/", StringComparison.Ordinal)
        ),
        (
            "Duplication",
            false,
            p => string.Equals(p, "metrics/duplication.txt", StringComparison.Ordinal)
        ),
        (
            "Hand-written code metrics (src/)",
            false,
            p => p.StartsWith("metrics/", StringComparison.Ordinal)
        ),
        ("Call graph", false, p => p.StartsWith("callgraph/", StringComparison.Ordinal)),
        // Last, and matches everything: a new kind of output shows up instead of failing the render
        // before a section is written for it.
        (Other, false, _ => true),
    ];

    public static IEnumerable<(string Title, bool Expanded)> All =>
        Rules.Select(r => (r.Title, r.Expanded));

    public static string Of(string relative) => Rules.First(r => r.Owns(relative)).Title;

    /// <summary>
    /// The derived files of a tree, ordinal-sorted. Top-level files are step-summary fragments,
    /// views of the files listed here rather than outputs of their own.
    /// </summary>
    public static IEnumerable<string> Files(string root) =>
        Directory.Exists(root)
            ? Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(p =>
                    p.Contains('/', StringComparison.Ordinal)
                    && !p.EndsWith("-summary.md", StringComparison.Ordinal)
                )
                .Order(StringComparer.Ordinal)
            : [];
}

/// <summary>Diffs two trees written by scripts/DerivedOutputs.cs, file by file.</summary>
internal static class TreeDiff
{
    public static IReadOnlyList<FileChange> Changes(string baseDir, string headDir)
    {
        var changes = new List<FileChange>();
        foreach (
            string relative in Sections
                .Files(baseDir)
                .Concat(Sections.Files(headDir))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        )
        {
            string before = Path.Combine(baseDir, relative);
            string after = Path.Combine(headDir, relative);
            bool inBase = File.Exists(before);
            bool inHead = File.Exists(after);
            if (
                inBase
                && inHead
                && File.ReadAllBytes(before).AsSpan().SequenceEqual(File.ReadAllBytes(after))
            )
            {
                continue;
            }

            string patch = Git.DiffFiles(
                inBase ? before : "/dev/null",
                inHead ? after : "/dev/null",
                relative
            );
            var lines = DiffLine.Parse(patch);
            string status =
                !inBase ? "added"
                : !inHead ? "removed"
                : "modified";
            changes.Add(
                new FileChange(
                    relative,
                    status,
                    lines.Count(l => l.Kind == DiffLineKind.Added),
                    lines.Count(l => l.Kind == DiffLineKind.Removed),
                    patch
                )
            );
        }

        return changes;
    }

    public static IReadOnlyList<Section> Group(IReadOnlyList<FileChange> changes) =>
        Sections
            .All.Select(rule => new Section(
                rule.Title,
                rule.Expanded,
                changes.Where(c => Sections.Of(c.Path) == rule.Title).ToList()
            ))
            .Where(s => s.Files.Count > 0)
            .ToList();
}
