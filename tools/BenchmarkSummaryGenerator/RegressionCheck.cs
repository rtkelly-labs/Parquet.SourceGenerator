using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Parquet.SourceGenerator.Tools;

/// <summary>
/// One benchmark result reduced to canonical units.
/// </summary>
/// <remarks>
/// Canonical units are the whole point of this type. BenchmarkDotNet writes whichever unit reads
/// best — "1,234.5 μs" in one run and "1.23 ms" in the next as a method gets slower — so comparing
/// the printed numbers directly would report a 1000x improvement for a genuine slowdown. Everything
/// is normalised to nanoseconds and bytes before any comparison happens.
/// </remarks>
public sealed record BenchmarkMeasurement(
    string Method,
    int Count,
    double MeanNanoseconds,
    long AllocatedBytes);

/// <summary>
/// How one benchmark compares against its recorded baseline.
/// </summary>
public sealed record BenchmarkComparison(
    string Method,
    int Count,
    BenchmarkMeasurement? Baseline,
    BenchmarkMeasurement? Current,
    RegressionKind Kind,
    string Detail);

/// <summary>
/// The verdict for a single benchmark.
/// </summary>
public enum RegressionKind
{
    /// <summary>Within tolerance of the baseline.</summary>
    Unchanged,

    /// <summary>Measurably better than the baseline — worth refreshing the baseline for.</summary>
    Improved,

    /// <summary>Allocates more than the baseline allows. Fails the check.</summary>
    AllocationRegression,

    /// <summary>Slower than the baseline allows. Reported, but does not fail the check by default.</summary>
    TimeRegression,

    /// <summary>Present in this run but not in the baseline.</summary>
    New,

    /// <summary>In the baseline but absent from this run — usually a benchmark filter.</summary>
    NotRun,
}

/// <summary>
/// Compares a BenchmarkDotNet run against a committed baseline.
/// </summary>
/// <remarks>
/// <para>
/// Allocated bytes is the gate; wall-clock time is reported but does not fail by default. That
/// split is deliberate rather than timid. Allocation counts on a fixed input are close to
/// deterministic — the same code allocates the same bytes on any machine — so a change in them is
/// a real change in what the code does. Wall-clock on a shared CI runner is not: neighbouring
/// tenants, frequency scaling and a cold cache move it by tens of percent between runs of
/// identical code. Gating merges on that produces failures nobody can act on, which is how a
/// performance gate ends up permanently disabled.
/// </para>
/// <para>
/// Use <c>--fail-on-time</c> when running on a quiet machine where the wall-clock number means
/// something.
/// </para>
/// </remarks>
public static class RegressionCheck
{
    /// <summary>Default allowance for an allocation increase before it counts as a regression.</summary>
    public const double DefaultAllocationTolerance = 0.05;

    /// <summary>Default allowance for a wall-clock increase before it is reported.</summary>
    public const double DefaultTimeTolerance = 0.50;

    /// <summary>A change smaller than this is treated as noise regardless of the percentage.</summary>
    /// <remarks>
    /// Without an absolute floor, a benchmark allocating 200 bytes fails the 5% rule on a single
    /// extra 16-byte object — a difference nobody wants a build to stop for.
    /// </remarks>
    public const long AllocationNoiseFloorBytes = 4096;

    /// <summary>
    /// Compares current measurements against a baseline.
    /// </summary>
    public static IReadOnlyList<BenchmarkComparison> Compare(
        IReadOnlyList<BenchmarkMeasurement> baseline,
        IReadOnlyList<BenchmarkMeasurement> current,
        double allocationTolerance = DefaultAllocationTolerance,
        double timeTolerance = DefaultTimeTolerance)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        Dictionary<(string, int), BenchmarkMeasurement> baselineByKey =
            baseline.ToDictionary(m => (m.Method, m.Count));
        Dictionary<(string, int), BenchmarkMeasurement> currentByKey =
            current.ToDictionary(m => (m.Method, m.Count));

        var results = new List<BenchmarkComparison>();

        foreach (BenchmarkMeasurement now in current.OrderBy(m => m.Method, StringComparer.Ordinal).ThenBy(m => m.Count))
        {
            if (!baselineByKey.TryGetValue((now.Method, now.Count), out BenchmarkMeasurement? was))
            {
                results.Add(new BenchmarkComparison(
                    now.Method, now.Count, null, now, RegressionKind.New,
                    "Not in the baseline. Refresh the baseline to start tracking it."));
                continue;
            }

            results.Add(CompareOne(was, now, allocationTolerance, timeTolerance));
        }

        // A benchmark that vanished is reported rather than ignored. Silence here would let a
        // filter that skips half the suite read as "everything passed".
        foreach (BenchmarkMeasurement was in baseline.OrderBy(m => m.Method, StringComparer.Ordinal).ThenBy(m => m.Count))
        {
            if (!currentByKey.ContainsKey((was.Method, was.Count)))
            {
                results.Add(new BenchmarkComparison(
                    was.Method, was.Count, was, null, RegressionKind.NotRun,
                    "In the baseline but not in this run — check the benchmark filter."));
            }
        }

        return results;
    }

    private static BenchmarkComparison CompareOne(
        BenchmarkMeasurement was,
        BenchmarkMeasurement now,
        double allocationTolerance,
        double timeTolerance)
    {
        long allocationDelta = now.AllocatedBytes - was.AllocatedBytes;
        long allocationBudget = (long)Math.Round(was.AllocatedBytes * allocationTolerance);

        if (allocationDelta > AllocationNoiseFloorBytes && allocationDelta > allocationBudget)
        {
            double percent = was.AllocatedBytes > 0
                ? allocationDelta * 100.0 / was.AllocatedBytes
                : double.PositiveInfinity;

            return new BenchmarkComparison(
                now.Method, now.Count, was, now, RegressionKind.AllocationRegression,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Allocated {FormatBytes(was.AllocatedBytes)} -> {FormatBytes(now.AllocatedBytes)} (+{percent:F1}%)"));
        }

        double timeBudget = was.MeanNanoseconds * (1.0 + timeTolerance);
        if (now.MeanNanoseconds > timeBudget && was.MeanNanoseconds > 0)
        {
            double percent = (now.MeanNanoseconds - was.MeanNanoseconds) * 100.0 / was.MeanNanoseconds;
            return new BenchmarkComparison(
                now.Method, now.Count, was, now, RegressionKind.TimeRegression,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Mean {FormatNanoseconds(was.MeanNanoseconds)} -> {FormatNanoseconds(now.MeanNanoseconds)} (+{percent:F1}%)"));
        }

        // Only allocations count as an improvement. A wall-clock drop on a shared runner is as
        // likely to be a quiet neighbour as a better algorithm, and baking that into the baseline
        // would make the next honest run look like a regression.
        if (was.AllocatedBytes - now.AllocatedBytes > AllocationNoiseFloorBytes &&
            was.AllocatedBytes - now.AllocatedBytes > allocationBudget)
        {
            double percent = (was.AllocatedBytes - now.AllocatedBytes) * 100.0 / was.AllocatedBytes;
            return new BenchmarkComparison(
                now.Method, now.Count, was, now, RegressionKind.Improved,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Allocated {FormatBytes(was.AllocatedBytes)} -> {FormatBytes(now.AllocatedBytes)} (-{percent:F1}%)"));
        }

        return new BenchmarkComparison(now.Method, now.Count, was, now, RegressionKind.Unchanged, "Within tolerance.");
    }

    // ──────────────────────────────────────────────────────────
    //  PARSING BENCHMARKDOTNET OUTPUT
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every <c>*-report.csv</c> in a BenchmarkDotNet results directory.
    /// </summary>
    public static IReadOnlyList<BenchmarkMeasurement> ReadResults(string resultsDirectory)
    {
        if (!Directory.Exists(resultsDirectory)) return Array.Empty<BenchmarkMeasurement>();

        var measurements = new Dictionary<(string, int), BenchmarkMeasurement>();

        foreach (string csvFile in Directory.GetFiles(resultsDirectory, "*-report.csv"))
        {
            foreach (BenchmarkMeasurement measurement in ParseCsv(File.ReadAllLines(csvFile)))
            {
                measurements[(measurement.Method, measurement.Count)] = measurement;
            }
        }

        return measurements.Values
            .OrderBy(m => m.Method, StringComparer.Ordinal)
            .ThenBy(m => m.Count)
            .ToList();
    }

    /// <summary>
    /// Parses the rows of one BenchmarkDotNet CSV report.
    /// </summary>
    public static IReadOnlyList<BenchmarkMeasurement> ParseCsv(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count <= 1) return Array.Empty<BenchmarkMeasurement>();

        string[] headers = Program.ParseCsvLine(lines[0]);
        int methodIndex = IndexOfHeader(headers, "Method");
        int countIndex = IndexOfHeader(headers, "Count");
        int meanIndex = IndexOfHeader(headers, "Mean");
        int allocatedIndex = IndexOfHeader(headers, "Allocated");

        if (methodIndex < 0 || meanIndex < 0) return Array.Empty<BenchmarkMeasurement>();

        // BenchmarkDotNet puts the unit either in the value ("1,234.5 μs") or in the header
        // ("Mean [ns]"), depending on the exporter's configuration. Both have to be understood, or
        // the numbers silently become unitless and comparisons are nonsense.
        string? meanHeaderUnit = UnitFromHeader(headers[meanIndex]);
        string? allocatedHeaderUnit = allocatedIndex >= 0 ? UnitFromHeader(headers[allocatedIndex]) : null;

        var results = new List<BenchmarkMeasurement>();

        for (int i = 1; i < lines.Count; i++)
        {
            string[] parts = Program.ParseCsvLine(lines[i]);
            if (parts.Length <= methodIndex || parts.Length <= meanIndex) continue;

            string method = parts[methodIndex].Trim();
            if (string.IsNullOrEmpty(method)) continue;

            double? mean = ParseTimeToNanoseconds(parts[meanIndex], meanHeaderUnit);
            if (mean is null) continue;

            long allocated = 0;
            if (allocatedIndex >= 0 && allocatedIndex < parts.Length)
            {
                allocated = ParseMemoryToBytes(parts[allocatedIndex], allocatedHeaderUnit) ?? 0;
            }

            int count = 0;
            if (countIndex >= 0 && countIndex < parts.Length)
            {
                _ = int.TryParse(
                    parts[countIndex].Replace(",", "", StringComparison.Ordinal).Trim(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out count);
            }

            results.Add(new BenchmarkMeasurement(method, count, mean.Value, allocated));
        }

        return results;
    }

    /// <summary>
    /// Converts a BenchmarkDotNet duration to nanoseconds.
    /// </summary>
    public static double? ParseTimeToNanoseconds(string value, string? headerUnit = null)
    {
        string cleaned = Clean(value);
        if (cleaned.Length == 0) return null;

        string unit = ExtractUnit(ref cleaned) ?? headerUnit ?? "ns";
        if (!double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out double number)) return null;

        // "us" and "μs" both appear depending on the console encoding the run happened under.
        return unit switch
        {
            "ns" => number,
            "us" or "μs" or "µs" => number * 1_000d,
            "ms" => number * 1_000_000d,
            "s" => number * 1_000_000_000d,
            _ => null,
        };
    }

    /// <summary>
    /// Converts a BenchmarkDotNet allocation figure to bytes.
    /// </summary>
    public static long? ParseMemoryToBytes(string value, string? headerUnit = null)
    {
        string cleaned = Clean(value);
        if (cleaned.Length == 0) return null;

        string unit = ExtractUnit(ref cleaned) ?? headerUnit ?? "B";
        if (!double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out double number)) return null;

        double bytes = unit.ToUpperInvariant() switch
        {
            "B" => number,
            "KB" => number * 1024d,
            "MB" => number * 1024d * 1024d,
            "GB" => number * 1024d * 1024d * 1024d,
            _ => double.NaN,
        };

        return double.IsNaN(bytes) ? null : (long)Math.Round(bytes);
    }

    private static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string cleaned = value.Replace(",", "", StringComparison.Ordinal).Trim();

        // BenchmarkDotNet writes "-" for a column that does not apply and "NA"/"?" where a run
        // produced nothing. None of them are zero, so they must not parse as zero.
        return cleaned is "-" or "NA" or "?" or "N/A" ? string.Empty : cleaned;
    }

    private static string? ExtractUnit(ref string cleaned)
    {
        int split = cleaned.Length;
        while (split > 0 && !char.IsDigit(cleaned[split - 1]) && cleaned[split - 1] != '.')
        {
            split--;
        }

        if (split == cleaned.Length) return null;

        string unit = cleaned.Substring(split).Trim();
        cleaned = cleaned.Substring(0, split).Trim();
        return unit.Length == 0 ? null : unit;
    }

    private static string? UnitFromHeader(string header)
    {
        int open = header.IndexOf('[', StringComparison.Ordinal);
        int close = header.IndexOf(']', StringComparison.Ordinal);
        return open >= 0 && close > open ? header.Substring(open + 1, close - open - 1).Trim() : null;
    }

    private static int IndexOfHeader(string[] headers, string name)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            string header = headers[i].Trim();
            int bracket = header.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0) header = header.Substring(0, bracket).Trim();

            if (string.Equals(header, name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    // ──────────────────────────────────────────────────────────
    //  BASELINE FILE
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a baseline file, returning an empty list when it does not exist yet.
    /// </summary>
    public static IReadOnlyList<BenchmarkMeasurement> ReadBaseline(string path)
    {
        if (!File.Exists(path)) return Array.Empty<BenchmarkMeasurement>();

        return ParseBaseline(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses baseline JSON.
    /// </summary>
    public static IReadOnlyList<BenchmarkMeasurement> ParseBaseline(string json)
    {
        // JsonDocument rather than JsonSerializer: no reflection, so nothing here trips the trim
        // and AOT analyzers, and the shape is flat enough that a mapper would not earn its keep.
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("measurements", out JsonElement measurements))
        {
            return Array.Empty<BenchmarkMeasurement>();
        }

        var results = new List<BenchmarkMeasurement>();
        foreach (JsonElement element in measurements.EnumerateArray())
        {
            results.Add(new BenchmarkMeasurement(
                element.GetProperty("method").GetString() ?? string.Empty,
                element.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 0,
                element.GetProperty("meanNanoseconds").GetDouble(),
                element.GetProperty("allocatedBytes").GetInt64()));
        }

        return results;
    }

    /// <summary>
    /// Renders a baseline file.
    /// </summary>
    public static string WriteBaseline(IReadOnlyList<BenchmarkMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"schema\": 1,");
        builder.AppendLine("  \"note\": \"Regenerate with the benchmarks workflow's update_baseline input. Times are indicative; allocations are the gate.\",");
        builder.AppendLine("  \"measurements\": [");

        List<BenchmarkMeasurement> ordered = measurements
            .OrderBy(m => m.Method, StringComparer.Ordinal)
            .ThenBy(m => m.Count)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            BenchmarkMeasurement m = ordered[i];
            string comma = i < ordered.Count - 1 ? "," : string.Empty;
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {{ \"method\": \"{m.Method}\", \"count\": {m.Count}, \"meanNanoseconds\": {m.MeanNanoseconds:F1}, \"allocatedBytes\": {m.AllocatedBytes} }}{comma}"));
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    // ──────────────────────────────────────────────────────────
    //  REPORTING
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the comparison as a Markdown report suitable for a step summary.
    /// </summary>
    public static string BuildReport(IReadOnlyList<BenchmarkComparison> comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);

        var builder = new StringBuilder();
        builder.AppendLine("## 📈 Benchmark Regression Check");
        builder.AppendLine();

        int regressions = comparisons.Count(c => c.Kind == RegressionKind.AllocationRegression);
        int slowdowns = comparisons.Count(c => c.Kind == RegressionKind.TimeRegression);
        int improvements = comparisons.Count(c => c.Kind == RegressionKind.Improved);

        builder.AppendLine(regressions > 0
            ? $"**{regressions} allocation regression(s).**"
            : "No allocation regressions.");

        if (slowdowns > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $" {slowdowns} benchmark(s) slower than tolerance — informational, see the note below.");
        }

        if (improvements > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $" {improvements} improvement(s) — refresh the baseline to lock them in.");
        }

        builder.AppendLine();
        builder.AppendLine("| Benchmark | Count | Verdict | Detail |");
        builder.AppendLine("|:--- |---:|:---:|:--- |");

        foreach (BenchmarkComparison c in comparisons.Where(c => c.Kind != RegressionKind.Unchanged))
        {
            string icon = c.Kind switch
            {
                RegressionKind.AllocationRegression => "🔴 alloc",
                RegressionKind.TimeRegression => "🟡 slower",
                RegressionKind.Improved => "🟢 better",
                RegressionKind.New => "🆕 new",
                RegressionKind.NotRun => "⚪ not run",
                _ => "",
            };

            builder.AppendLine(CultureInfo.InvariantCulture, $"| `{c.Method}` | {c.Count} | {icon} | {c.Detail} |");
        }

        if (comparisons.All(c => c.Kind == RegressionKind.Unchanged))
        {
            builder.AppendLine("| _all benchmarks_ | | ✅ | Within tolerance of the baseline. |");
        }

        builder.AppendLine();
        builder.AppendLine("> Allocated bytes is the gate; wall-clock is reported but does not fail the build. Allocation");
        builder.AppendLine("> counts on a fixed input are near-deterministic, so a change in them is a real change in what");
        builder.AppendLine("> the code does. Wall-clock on a shared runner moves tens of percent between runs of identical");
        builder.AppendLine("> code, and a gate nobody can act on is a gate that gets switched off.");

        return builder.ToString();
    }

    /// <summary>
    /// Whether the comparison should fail the build.
    /// </summary>
    public static bool HasFailures(IReadOnlyList<BenchmarkComparison> comparisons, bool failOnTime)
    {
        ArgumentNullException.ThrowIfNull(comparisons);

        return comparisons.Any(c =>
            c.Kind == RegressionKind.AllocationRegression ||
            (failOnTime && c.Kind == RegressionKind.TimeRegression));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):F2} MB");
        if (bytes >= 1024L) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:F1} KB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
    }

    private static string FormatNanoseconds(double nanoseconds)
    {
        if (nanoseconds >= 1_000_000_000d) return string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000_000_000d:F2} s");
        if (nanoseconds >= 1_000_000d) return string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000_000d:F2} ms");
        if (nanoseconds >= 1_000d) return string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000d:F1} μs");
        return string.Create(CultureInfo.InvariantCulture, $"{nanoseconds:F0} ns");
    }
}
