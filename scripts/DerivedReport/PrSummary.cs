using System.Globalization;
using System.Text.RegularExpressions;

namespace Parquet.SourceGenerator.Tools.DerivedReport;

/// <summary>
/// What the PR changes, read from git between the merge base and the head commit: files and lines
/// by area, the API catalogues and changelog, test methods, and whether CI or tooling is touched.
/// Two commits fix every number here.
/// </summary>
internal static partial class PrSummary
{
    // Catalogue files named individually in the summary, in this order.
    private static readonly (string Label, Func<string, bool> Matches)[] Catalogues =
    [
        (
            "Shipped API (`PublicAPI.Unshipped.txt`)",
            p => p.EndsWith("/PublicAPI.Unshipped.txt", StringComparison.Ordinal)
        ),
        (
            "Shipped API (`PublicAPI.Shipped.txt`)",
            p => p.EndsWith("/PublicAPI.Shipped.txt", StringComparison.Ordinal)
        ),
        ("Internal seams (`src/api/seams.txt`)", p => p == "src/api/seams.txt"),
        ("API ledger (`docs/api/LEDGER.md`)", p => p == "docs/api/LEDGER.md"),
        ("Changelog (`CHANGELOG.md`)", p => p == "CHANGELOG.md"),
    ];

    public static IReadOnlyList<Table> Build(string repo, string baseCommit, string headCommit)
    {
        var files = NumStat(repo, baseCommit, headCommit);
        if (files.Count == 0)
        {
            return [new Table("Changes", [], [], [new Note("No files changed.", [])])];
        }

        var tables = new List<Table> { Areas(files) };

        var catalogueRows = new List<IReadOnlyList<string>>();
        foreach (var (label, matches) in Catalogues)
        {
            foreach (var file in files.Where(f => matches(f.Path)))
            {
                string name = label.StartsWith("Shipped API", StringComparison.Ordinal)
                    ? $"{label} — {Area(file.Path)}"
                    : label;
                catalogueRows.Add([name, $"+{file.Added}", $"−{file.Removed}"]);
            }
        }

        var notes = new List<Note>();
        var (testsAdded, testsRemoved) = TestMethods(repo, baseCommit, headCommit);
        if (testsAdded + testsRemoved > 0)
        {
            notes.Add(
                new Note(
                    $"Test methods: +{testsAdded} −{testsRemoved} (counted by [Fact] and [Theory] attributes).",
                    []
                )
            );
        }

        var protectedPaths = ProtectedPaths.Load(repo);
        var tooling = files.Select(f => f.Path).Where(protectedPaths.Matches).ToList();
        if (tooling.Count > 0)
        {
            notes.Add(
                new Note(
                    $"CI and tooling touched ({tooling.Count}, listed in `{ProtectedPaths.File}`)",
                    tooling
                )
            );
        }

        if (catalogueRows.Count > 0 || notes.Count > 0)
        {
            tables.Add(
                new Table(
                    "API catalogues, changelog and tooling",
                    ["File", "+", "−"],
                    catalogueRows,
                    notes
                )
            );
        }

        return tables;
    }

    private static Table Areas(IReadOnlyList<(string Path, int Added, int Removed)> files)
    {
        var rows = files
            .GroupBy(f => Area(f.Path), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
                (IReadOnlyList<string>)
                    [
                        g.Key,
                        g.Count().ToString(CultureInfo.InvariantCulture),
                        $"+{g.Sum(f => f.Added)}",
                        $"−{g.Sum(f => f.Removed)}",
                    ]
            )
            .ToList();
        rows.Add([
            "Total",
            files.Count.ToString(CultureInfo.InvariantCulture),
            $"+{files.Sum(f => f.Added)}",
            $"−{files.Sum(f => f.Removed)}",
        ]);
        return new Table("Changes by area", ["Area", "Files", "+", "−"], rows, []);
    }

    // src/, test/, tools/, samples/ and benchmarks/ group by project; everything else by top folder.
    private static string Area(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length == 1)
        {
            return "(repository root)";
        }

        return
            parts.Length > 2 && parts[0] is "src" or "test" or "tools" or "samples" or "benchmarks"
            ? $"{parts[0]}/{parts[1]}"
            : parts[0];
    }

    private static List<(string Path, int Added, int Removed)> NumStat(
        string repo,
        string baseCommit,
        string headCommit
    )
    {
        var files = new List<(string, int, int)>();
        string output = Git.Run(
            repo,
            0,
            "diff",
            "--numstat",
            "--no-renames",
            baseCommit,
            headCommit
        );
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t', 3);
            // Binary files report "-" for both counts.
            files.Add((fields[2], Count(fields[0]), Count(fields[1])));
        }

        return files.OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();

        static int Count(string field) =>
            int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    private static (int Added, int Removed) TestMethods(
        string repo,
        string baseCommit,
        string headCommit
    )
    {
        string output = Git.Run(
            repo,
            0,
            "diff",
            "--unified=0",
            "--no-renames",
            baseCommit,
            headCommit,
            "--",
            "test"
        );
        int added = 0;
        int removed = 0;
        foreach (string line in output.Split('\n'))
        {
            if (TestAttribute().IsMatch(line))
            {
                if (line[0] == '+')
                {
                    added++;
                }
                else
                {
                    removed++;
                }
            }
        }

        return (added, removed);
    }

    [GeneratedRegex(@"^[+-]\s*\[(Fact|Theory)\b", RegexOptions.None, 1000)]
    private static partial Regex TestAttribute();
}
