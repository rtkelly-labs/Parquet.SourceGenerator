using System.Text;

namespace Parquet.SourceGenerator.Tools.DerivedReport;

/// <summary>
/// The sticky PR comment: a summary of the whole PR, never diff text. Everything above the footer
/// is a function of the two commits and the two trees; only the footer's links name the run.
/// </summary>
internal static class Markdown
{
    public const string Marker = "<!-- derived-review-diff -->";

    public static string Comment(DiffReport report, string driftStatus, Links links)
    {
        var body = new StringBuilder();
        body.Append("## PR summary vs `").Append(report.BaseLabel).Append("`\n\n");
        if (report.Summary.Count > 0)
        {
            body.Append("### Changes\n\n");
            AppendTables(body, report.Summary);
        }

        body.Append("### Derived output drift\n\n");
        if (driftStatus.Length > 0)
        {
            body.Append(driftStatus).Append("\n\n");
        }
        else if (!report.HasChanges)
        {
            body.Append("**No derived output changed.**\n\n");
        }
        else
        {
            body.Append("| What changed | Files | + | − |\n|:--|--:|--:|--:|\n");
            foreach (var section in report.Sections)
            {
                body.Append(
                    $"| {section.Title} | {Html.N(section.Files.Count)} | +{Html.N(section.Added)} | −{Html.N(section.Removed)} |\n"
                );
            }

            body.Append('\n');
            AppendTables(body, report.Drift);
        }

        body.Append("---\n\n").Append(links.Footer(report.BaseSource));
        return Marker + "\n" + body.ToString().TrimEnd('\n') + "\n";
    }

    private static void AppendTables(StringBuilder body, IReadOnlyList<Table> tables)
    {
        foreach (var table in tables)
        {
            body.Append("#### ").Append(table.Title).Append("\n\n");
            if (table.Columns.Count > 0 && table.Rows.Count > 0)
            {
                body.Append("| ").AppendJoin(" | ", table.Columns.Select(Cell)).Append(" |\n|");
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    body.Append(i == 0 ? ":--|" : "--:|");
                }

                body.Append('\n');
                foreach (var row in table.Rows)
                {
                    body.Append("| ").AppendJoin(" | ", row.Select(Cell)).Append(" |\n");
                }

                body.Append('\n');
            }

            foreach (var note in table.Notes)
            {
                body.Append(note.Text);
                if (note.Items.Count > 0)
                {
                    body.Append(": ").AppendJoin(", ", note.Items.Select(i => $"`{i}`"));
                }

                body.Append("\n\n");
            }
        }
    }

    // A cell cannot contain a raw pipe or newline in a Markdown table.
    private static string Cell(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
}

/// <summary>Where this run's full outputs are; the only run-specific part of the comment.</summary>
internal sealed record Links(string? DiffHtml, string? StateHtml, string? Artifacts)
{
    public string Footer(string? baseSource)
    {
        var parts = new List<string>();
        if (DiffHtml is not null)
        {
            parts.Add($"**[Full diff (HTML)]({DiffHtml})**");
        }

        if (StateHtml is not null)
        {
            parts.Add($"[Current state (HTML)]({StateHtml})");
        }

        parts.Add(
            Artifacts is null
                ? "the `derived-outputs` artifact of this run"
                : $"[`derived-outputs` artifact]({Artifacts})"
        );
        var footer = new StringBuilder("Full detail: ").AppendJoin(" · ", parts).Append('\n');
        if (baseSource is not null)
        {
            footer.Append("\n<sub>Merge-base outputs: ").Append(baseSource).Append(".</sub>\n");
        }

        return footer.ToString();
    }
}
