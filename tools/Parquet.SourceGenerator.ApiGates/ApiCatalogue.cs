using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>
/// Reads a catalogue file — an emitted-API <c>.api.txt</c> baseline or <c>src/api/seams.txt</c> —
/// into the set of signatures it declares.
/// </summary>
/// <remarks>
/// Both catalogues use the grammar documented in <c>docs/17-GENERATED-API-BASELINES.md</c>: one
/// signature per line, self-contained, ordinal-sorted. Blank lines and lines beginning with
/// <c>#</c> carry no signature — that covers both the <c>#nullable enable</c> header the baselines
/// inherit from <c>PublicAPI.Shipped.txt</c> and the explanatory comments at the top of
/// <c>seams.txt</c>, so there is one skip rule rather than two.
/// </remarks>
public static class ApiCatalogue
{
    /// <summary>Parses the significant signature lines out of a catalogue's text.</summary>
    public static ImmutableHashSet<string> Read(SourceText? text)
    {
        if (text is null)
        {
            return ImmutableHashSet<string>.Empty;
        }

        ImmutableHashSet<string>.Builder builder = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.Ordinal
        );

        foreach (TextLine line in text.Lines)
        {
            string trimmed = line.ToString().Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            builder.Add(trimmed);
        }

        return builder.ToImmutable();
    }

    /// <summary>Parses the significant signature lines out of an additional file.</summary>
    public static ImmutableHashSet<string> Read(AdditionalText file, CancellationToken token) =>
        Read(file.GetText(token));

    /// <summary>
    /// Extracts the simple member name from a catalogue line, so a ledger escape-hatch entry can
    /// name a member without transcribing its whole signature. Returns the empty string for a bare
    /// type line, which has no member to name.
    /// </summary>
    public static string SimpleName(string line)
    {
        // Drop the canonical modifier prefix ("static readonly ...") and the return type.
        int arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        string head = arrow >= 0 ? line.Substring(0, arrow) : line;

        int cut = head.Length;
        foreach (char delimiter in new[] { '(', '[', '<' })
        {
            int index = head.IndexOf(delimiter);
            if (index >= 0 && index < cut)
            {
                cut = index;
            }
        }

        int equals = head.IndexOf(" = ", StringComparison.Ordinal);
        if (equals >= 0 && equals < cut)
        {
            cut = equals;
        }

        head = head.Substring(0, cut);

        int lastSpace = head.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            head = head.Substring(lastSpace + 1);
        }

        int lastDot = head.LastIndexOf('.');
        return lastDot >= 0 ? head.Substring(lastDot + 1) : head;
    }
}
