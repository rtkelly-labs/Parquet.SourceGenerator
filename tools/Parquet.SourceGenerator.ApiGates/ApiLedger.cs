using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>
/// The escape hatch. A <c>docs/api/LEDGER.md</c> entry marked <c>**Unapproved-by-design:**</c>
/// suppresses <c>PARQAPI001</c> / <c>PARQAPI002</c> for the signature it names.
/// </summary>
/// <remarks>
/// <para>The strict gate exists so that nothing enters a governed surface unreviewed. That is the
/// right default and the wrong constraint for a spike: an experiment branch that has to refresh
/// five catalogues and write five rationales before it compiles is an experiment that does not get
/// run. The hatch is therefore deliberate rather than a loophole — it is written down, it costs one
/// ledger entry, and CI rejects the marker on <c>main</c>, so a pull request carrying one cannot
/// merge.</para>
///
/// <para><b>Grammar.</b> An entry is a level-3 heading and the lines beneath it up to the next
/// heading. When any of those lines contains the literal <c>**Unapproved-by-design:**</c>, every
/// backtick-quoted token in the heading and in that line becomes an exemption token.</para>
///
/// <para><b>Matching.</b> A token exempts a violation when it equals the catalogue line verbatim,
/// equals the member's simple name, or begins with the member's simple name followed by
/// <c>(</c> — the last of which is the shape the ledger's own headings use,
/// <c>ReadParquetBatchesAsync(Stream, ...)</c>. Nothing looser: a token that matched on substring
/// would let one hatch entry silence an unrelated member.</para>
/// </remarks>
public sealed class ApiLedger
{
    /// <summary>The literal that marks an entry as an escape-hatch entry.</summary>
    public const string UnapprovedMarker = "**Unapproved-by-design:**";

    /// <summary>A ledger with no entries, used when the file is not supplied to the compilation.</summary>
    public static readonly ApiLedger Empty = new(ImmutableHashSet<string>.Empty);

    private readonly ImmutableHashSet<string> _exemptions;

    private ApiLedger(ImmutableHashSet<string> exemptions) => _exemptions = exemptions;

    /// <summary>True when no signature is exempted, which is the state <c>main</c> must be in.</summary>
    public bool IsEmpty => _exemptions.Count == 0;

    /// <summary>Parses the ledger's escape-hatch entries out of <c>LEDGER.md</c>.</summary>
    public static ApiLedger Read(SourceText? text)
    {
        if (text is null)
        {
            return Empty;
        }

        ImmutableHashSet<string>.Builder exemptions = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.Ordinal
        );

        var pending = new List<string>();
        string heading = string.Empty;
        bool unapproved = false;

        void Flush()
        {
            if (unapproved)
            {
                CollectQuoted(heading, exemptions);
                foreach (string line in pending)
                {
                    CollectQuoted(line, exemptions);
                }
            }

            pending.Clear();
            unapproved = false;
        }

        foreach (TextLine textLine in text.Lines)
        {
            string line = textLine.ToString();
            if (line.Length > 0 && line[0] == '#')
            {
                Flush();
                heading = line;
                continue;
            }

            if (ContainsOrdinal(line, UnapprovedMarker))
            {
                unapproved = true;
                pending.Add(line);
            }
        }

        Flush();
        return new ApiLedger(exemptions.ToImmutable());
    }

    /// <summary>Parses the ledger out of an additional file.</summary>
    public static ApiLedger Read(AdditionalText? file, CancellationToken token) =>
        file is null ? Empty : Read(file.GetText(token));

    /// <summary>True when an escape-hatch entry covers <paramref name="signature"/>.</summary>
    public bool IsExempt(string signature)
    {
        if (_exemptions.Count == 0)
        {
            return false;
        }

        if (_exemptions.Contains(signature))
        {
            return true;
        }

        string simpleName = ApiCatalogue.SimpleName(signature);
        if (simpleName.Length == 0)
        {
            return false;
        }

        if (_exemptions.Contains(simpleName))
        {
            return true;
        }

        foreach (string exemption in _exemptions)
        {
            if (
                exemption.Length > simpleName.Length
                && exemption.StartsWith(simpleName, StringComparison.Ordinal)
                && exemption[simpleName.Length] == '('
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ordinal substring test. This file is compiled into both the netstandard2.0 analyzer and the
    /// net8.0 test assembly, and <c>string.Contains(string, StringComparison)</c> exists only in
    /// the latter — where CA2249 also insists on it.
    /// </summary>
    private static bool ContainsOrdinal(string text, string value) =>
#if NETSTANDARD2_0
        text.IndexOf(value, StringComparison.Ordinal) >= 0;
#else
        text.Contains(value, StringComparison.Ordinal);
#endif

    private static void CollectQuoted(string line, ImmutableHashSet<string>.Builder exemptions)
    {
        int index = 0;
        while (true)
        {
            int open = line.IndexOf('`', index);
            if (open < 0)
            {
                return;
            }

            int close = line.IndexOf('`', open + 1);
            if (close < 0)
            {
                return;
            }

            string token = line.Substring(open + 1, close - open - 1).Trim();
            if (token.Length > 0)
            {
                exemptions.Add(token);
            }

            index = close + 1;
        }
    }
}
