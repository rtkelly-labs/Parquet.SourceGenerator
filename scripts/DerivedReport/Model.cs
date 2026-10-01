using System.Globalization;
using System.Text.RegularExpressions;

namespace Parquet.SourceGenerator.Tools.DerivedReport;

// The one model both renderings read: the Markdown comment and the HTML page. Every value in it is
// a function of the two commits and the two derived-output trees, never of the run, so the same
// inputs always render the same bytes.

/// <summary>The diff view: what a PR changes and what that did to the derived outputs.</summary>
public sealed record DiffReport(
    string BaseLabel,
    string? BaseSource,
    IReadOnlyList<Table> Summary,
    IReadOnlyList<Table> Drift,
    IReadOnlyList<Section> Sections
)
{
    public bool HasChanges => Sections.Count > 0;
}

/// <summary>A titled table with optional notes under it. Cells are plain text.</summary>
public sealed record Table(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<Note> Notes
);

/// <summary>A sentence, optionally followed by a list of code-formatted items.</summary>
public sealed record Note(string Text, IReadOnlyList<string> Items);

/// <summary>The changed files of one output kind, in the review's reading order.</summary>
public sealed record Section(string Title, bool Expanded, IReadOnlyList<FileChange> Files)
{
    public int Added => Files.Sum(f => f.Added);

    public int Removed => Files.Sum(f => f.Removed);
}

/// <summary>One changed derived file and its unified diff.</summary>
public sealed record FileChange(string Path, string Status, int Added, int Removed, string Patch)
{
    /// <summary>The diff's hunks as numbered lines, header dropped.</summary>
    public IReadOnlyList<DiffLine> Lines => DiffLine.Parse(Patch);
}

public enum DiffLineKind
{
    Hunk,
    Context,
    Added,
    Removed,
    Meta,
}

/// <summary>One line of a hunk, with its line number on each side where it has one.</summary>
public sealed partial record DiffLine(DiffLineKind Kind, int? Old, int? New, string Text)
{
    public static IReadOnlyList<DiffLine> Parse(string patch)
    {
        var lines = new List<DiffLine>();
        int oldLine = 0;
        int newLine = 0;
        bool inHunks = false;
        foreach (string line in patch.Split('\n'))
        {
            Match header = HunkHeader().Match(line);
            if (header.Success)
            {
                inHunks = true;
                oldLine = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                newLine = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new DiffLine(DiffLineKind.Hunk, null, null, line));
                continue;
            }

            // Everything before the first hunk is the file header, already named elsewhere.
            if (!inHunks || line.Length == 0)
            {
                continue;
            }

            lines.Add(
                line[0] switch
                {
                    '+' => new DiffLine(DiffLineKind.Added, null, newLine++, line),
                    '-' => new DiffLine(DiffLineKind.Removed, oldLine++, null, line),
                    '\\' => new DiffLine(DiffLineKind.Meta, null, null, line),
                    _ => new DiffLine(DiffLineKind.Context, oldLine++, newLine++, line),
                }
            );
        }

        return lines;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.None, 1000)]
    private static partial Regex HunkHeader();
}
