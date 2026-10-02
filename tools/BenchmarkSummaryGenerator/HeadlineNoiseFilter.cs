using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Parquet.SourceGenerator.Tools;

/// <summary>
/// Decides whether a freshly measured headline table differs from the committed one by more than
/// run-to-run noise.
/// </summary>
/// <remarks>
/// <para>
/// The headline table is rewritten by a scheduled run on a shared CI runner. Wall-clock there moves
/// tens of percent between runs of identical code, so without a filter every Sunday opens a pull
/// request that rewrites the table from noise (#568). Allocation moves very little, so a modest
/// allocation change is signal and a modest timing change is not.
/// </para>
/// <para>
/// The comparison reads the committed table back from its own text. Both tables are the same
/// shape: a row per scenario, each with a label, a scale, and "time (memory)" cells. A row that
/// appears or disappears, a changed scale, or a cell this reader cannot parse counts as a change,
/// so the filter fails open: a table it cannot understand is rewritten, never silently kept.
/// </para>
/// </remarks>
public static partial class HeadlineNoiseFilter
{
    /// <summary>Default relative allocation change that counts as signal.</summary>
    public const double DefaultAllocationThreshold = 0.10;

    /// <summary>Default relative wall-clock change that counts as signal.</summary>
    public const double DefaultTimeThreshold = 0.30;

    [GeneratedRegex(
        @"(?<value>\d[\d,]*(?:\.\d+)?)\s*(?<unit>ns|μs|µs|us|ms|s)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 2000
    )]
    private static partial Regex TimePattern();

    [GeneratedRegex(
        @"(?<value>\d[\d,]*(?:\.\d+)?)\s*(?<unit>GB|MB|KB|B)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 2000
    )]
    private static partial Regex MemoryPattern();

    /// <summary>
    /// Whether <paramref name="fresh"/> differs from <paramref name="committed"/> by more than the
    /// thresholds allow.
    /// </summary>
    /// <param name="committed">The table text currently in the README, between the markers.</param>
    /// <param name="fresh">The newly built table text.</param>
    /// <param name="allocationThreshold">Relative allocation change that counts, such as 0.10.</param>
    /// <param name="timeThreshold">Relative wall-clock change that counts, such as 0.30.</param>
    /// <param name="reason">What made it significant, or why it was not.</param>
    public static bool IsSignificant(
        string committed,
        string fresh,
        double allocationThreshold,
        double timeThreshold,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(fresh);

        Dictionary<string, Row>? before = ParseRows(committed);
        Dictionary<string, Row>? after = ParseRows(fresh);

        if (before is null || before.Count == 0)
        {
            reason = "There is no committed table to compare against.";
            return true;
        }

        if (after is null || after.Count == 0)
        {
            reason = "The new table has no rows this reader understands.";
            return true;
        }

        string[] added = after.Keys.Except(before.Keys, StringComparer.Ordinal).ToArray();
        string[] removed = before.Keys.Except(after.Keys, StringComparer.Ordinal).ToArray();
        if (added.Length > 0 || removed.Length > 0)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"Rows changed: {added.Length} added, {removed.Length} removed."
            );
            return true;
        }

        foreach ((string label, Row was) in before)
        {
            Row now = after[label];

            if (!string.Equals(was.Scale, now.Scale, StringComparison.Ordinal))
            {
                reason = $"'{label}' changed scale: {was.Scale} -> {now.Scale}.";
                return true;
            }

            if (
                was.Times.Count != now.Times.Count
                || was.Allocations.Count != now.Allocations.Count
            )
            {
                reason = $"'{label}' changed shape.";
                return true;
            }

            for (int i = 0; i < was.Allocations.Count; i++)
            {
                if (Exceeds(was.Allocations[i], now.Allocations[i], allocationThreshold))
                {
                    reason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{label}' allocation moved {Percent(was.Allocations[i], now.Allocations[i])} (threshold {allocationThreshold:P0})."
                    );
                    return true;
                }
            }

            for (int i = 0; i < was.Times.Count; i++)
            {
                if (Exceeds(was.Times[i], now.Times[i], timeThreshold))
                {
                    reason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{label}' time moved {Percent(was.Times[i], now.Times[i])} (threshold {timeThreshold:P0})."
                    );
                    return true;
                }
            }
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"No row moved more than {allocationThreshold:P0} in allocation or {timeThreshold:P0} in time."
        );
        return false;
    }

    private static bool Exceeds(double was, double now, double threshold)
    {
        if (was <= 0 || now <= 0)
            return Math.Abs(now - was) > 0;

        return Math.Abs(now - was) / was > threshold;
    }

    private static string Percent(double was, double now) =>
        was > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(now - was) / was:+0.0%;-0.0%}")
            : "from zero";

    /// <summary>
    /// Reads the scenario rows of a headline table, keyed by label. Returns null when a row has
    /// no readable label.
    /// </summary>
    private static Dictionary<string, Row>? ParseRows(string table)
    {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);

        foreach (string line in table.Split('\n'))
        {
            string trimmed = line.Trim();

            // Data rows start with a bold label. Header, separator and note lines do not.
            if (!trimmed.StartsWith("| **", StringComparison.Ordinal))
                continue;

            string[] cells = trimmed.Trim('|').Split('|');
            if (cells.Length < 3)
                return null;

            string label = Strip(cells[0]);
            string scale = Strip(cells[1]);
            if (label.Length == 0 || !rows.TryAdd(label, new Row(scale)))
                return null;

            Row row = rows[label];
            foreach (string cell in cells.Skip(2))
            {
                string text = Strip(cell);
                foreach (Match time in TimePattern().Matches(text))
                {
                    if (!TryNanoseconds(time, out double nanoseconds))
                        return null;

                    row.Times.Add(nanoseconds);
                }

                foreach (Match memory in MemoryPattern().Matches(text))
                {
                    if (!TryBytes(memory, out double bytes))
                        return null;

                    row.Allocations.Add(bytes);
                }
            }
        }

        return rows;
    }

    private static string Strip(string cell) =>
        cell.Replace("*", string.Empty, StringComparison.Ordinal).Trim();

    private static bool TryNanoseconds(Match match, out double nanoseconds)
    {
        if (!TryNumber(match, out double value))
        {
            nanoseconds = 0;
            return false;
        }

        nanoseconds = match.Groups["unit"].Value switch
        {
            "ns" => value,
            "ms" => value * 1e6,
            "s" => value * 1e9,
            _ => value * 1e3,
        };
        return true;
    }

    private static bool TryBytes(Match match, out double bytes)
    {
        if (!TryNumber(match, out double value))
        {
            bytes = 0;
            return false;
        }

        bytes = match.Groups["unit"].Value switch
        {
            "KB" => value * 1024d,
            "MB" => value * 1024d * 1024d,
            "GB" => value * 1024d * 1024d * 1024d,
            _ => value,
        };
        return true;
    }

    private static bool TryNumber(Match match, out double value) =>
        double.TryParse(
            match.Groups["value"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value
        );

    private sealed class Row(string scale)
    {
        public string Scale { get; } = scale;

        public List<double> Times { get; } = [];

        public List<double> Allocations { get; } = [];
    }
}
