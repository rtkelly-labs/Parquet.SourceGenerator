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
/// <para>
/// Canonical units are the whole point of this type. BenchmarkDotNet writes whichever unit reads
/// best in its tables, so comparing printed numbers directly would report a 1000x improvement for a
/// genuine slowdown. Everything is nanoseconds and bytes, read from the JSON export where both are
/// exact numbers.
/// </para>
/// <para>
/// A measurement is identified by the declaring class, the method and the full parameter set
/// (<see cref="Key"/>). Keying on method and <c>Count</c> alone collided where one method name is
/// declared in two classes (<c>WriteSnappyAsync</c>) and where a benchmark is parameterised by
/// something other than <c>Count</c>.
/// </para>
/// </remarks>
/// <param name="Type">The benchmark class, without its namespace.</param>
/// <param name="Method">The benchmark method.</param>
/// <param name="Parameters">Every <c>[Params]</c> value of the case, as BenchmarkDotNet prints them (<c>Count=1000</c>); empty when unparameterised.</param>
/// <param name="MeanNanoseconds">Mean wall-clock per operation. Indicative only.</param>
/// <param name="AllocatedBytes">Managed bytes allocated per operation.</param>
public sealed record BenchmarkMeasurement(
    string Type,
    string Method,
    string Parameters,
    double MeanNanoseconds,
    long AllocatedBytes
)
{
    /// <summary>The identity two measurements must share to be compared.</summary>
    public string Key =>
        string.IsNullOrEmpty(Parameters) ? $"{Type}.{Method}" : $"{Type}.{Method}({Parameters})";
}

/// <summary>
/// How one benchmark compares against its recorded baseline.
/// </summary>
public sealed record BenchmarkComparison(
    string Key,
    BenchmarkMeasurement? Baseline,
    BenchmarkMeasurement? Current,
    RegressionKind Kind,
    string Detail
);

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
/// A baseline file this tool cannot trust: wrong schema, or a measurement without an identity.
/// </summary>
public sealed class BaselineFormatException : Exception
{
    public BaselineFormatException() { }

    public BaselineFormatException(string message)
        : base(message) { }

    public BaselineFormatException(string message, Exception innerException)
        : base(message, innerException) { }
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
/// something, and <c>--no-time</c> when the baseline was recorded on different hardware than the
/// run, where a time comparison means nothing at all.
/// </para>
/// </remarks>
public static class RegressionCheck
{
    /// <summary>The baseline file schema this tool reads and writes.</summary>
    public const int BaselineSchema = 2;

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
        double timeTolerance = DefaultTimeTolerance
    )
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        Dictionary<string, BenchmarkMeasurement> baselineByKey = ByKey(baseline);
        Dictionary<string, BenchmarkMeasurement> currentByKey = ByKey(current);

        var results = new List<BenchmarkComparison>();

        foreach (BenchmarkMeasurement now in current.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!baselineByKey.TryGetValue(now.Key, out BenchmarkMeasurement? was))
            {
                results.Add(
                    new BenchmarkComparison(
                        now.Key,
                        null,
                        now,
                        RegressionKind.New,
                        "Not in the baseline. Refresh the baseline to start tracking it."
                    )
                );
                continue;
            }

            results.Add(CompareOne(was, now, allocationTolerance, timeTolerance));
        }

        // A benchmark that vanished is reported rather than ignored. Silence here would let a
        // filter that skips half the suite read as "everything passed".
        foreach (BenchmarkMeasurement was in baseline.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!currentByKey.ContainsKey(was.Key))
            {
                results.Add(
                    new BenchmarkComparison(
                        was.Key,
                        was,
                        null,
                        RegressionKind.NotRun,
                        "In the baseline but not in this run — check the benchmark filter."
                    )
                );
            }
        }

        return results;
    }

    private static Dictionary<string, BenchmarkMeasurement> ByKey(
        IReadOnlyList<BenchmarkMeasurement> measurements
    )
    {
        var byKey = new Dictionary<string, BenchmarkMeasurement>(StringComparer.Ordinal);
        foreach (BenchmarkMeasurement measurement in measurements)
        {
            // Two entries with one identity would make one of them unreachable: whichever was
            // read second would silently replace the first.
            if (!byKey.TryAdd(measurement.Key, measurement))
            {
                throw new InvalidOperationException(
                    $"Two measurements share the identity '{measurement.Key}'."
                );
            }
        }

        return byKey;
    }

    private static BenchmarkComparison CompareOne(
        BenchmarkMeasurement was,
        BenchmarkMeasurement now,
        double allocationTolerance,
        double timeTolerance
    )
    {
        long allocationDelta = now.AllocatedBytes - was.AllocatedBytes;
        long allocationBudget = (long)Math.Round(was.AllocatedBytes * allocationTolerance);

        if (allocationDelta > AllocationNoiseFloorBytes && allocationDelta > allocationBudget)
        {
            double percent =
                was.AllocatedBytes > 0
                    ? allocationDelta * 100.0 / was.AllocatedBytes
                    : double.PositiveInfinity;

            return new BenchmarkComparison(
                now.Key,
                was,
                now,
                RegressionKind.AllocationRegression,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Allocated {FormatBytes(was.AllocatedBytes)} -> {FormatBytes(now.AllocatedBytes)} (+{percent:F1}%)"
                )
            );
        }

        double timeBudget = was.MeanNanoseconds * (1.0 + timeTolerance);
        if (now.MeanNanoseconds > timeBudget && was.MeanNanoseconds > 0)
        {
            double percent =
                (now.MeanNanoseconds - was.MeanNanoseconds) * 100.0 / was.MeanNanoseconds;
            return new BenchmarkComparison(
                now.Key,
                was,
                now,
                RegressionKind.TimeRegression,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Mean {FormatNanoseconds(was.MeanNanoseconds)} -> {FormatNanoseconds(now.MeanNanoseconds)} (+{percent:F1}%)"
                )
            );
        }

        // Only allocations count as an improvement. A wall-clock drop on a shared runner is as
        // likely to be a quiet neighbour as a better algorithm, and baking that into the baseline
        // would make the next honest run look like a regression.
        if (
            was.AllocatedBytes - now.AllocatedBytes > AllocationNoiseFloorBytes
            && was.AllocatedBytes - now.AllocatedBytes > allocationBudget
        )
        {
            double percent = (was.AllocatedBytes - now.AllocatedBytes) * 100.0 / was.AllocatedBytes;
            return new BenchmarkComparison(
                now.Key,
                was,
                now,
                RegressionKind.Improved,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Allocated {FormatBytes(was.AllocatedBytes)} -> {FormatBytes(now.AllocatedBytes)} (-{percent:F1}%)"
                )
            );
        }

        return new BenchmarkComparison(
            now.Key,
            was,
            now,
            RegressionKind.Unchanged,
            "Within tolerance."
        );
    }

    // ──────────────────────────────────────────────────────────
    //  READING BENCHMARKDOTNET OUTPUT
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every <c>*-report-full-compressed.json</c> in a BenchmarkDotNet results directory.
    /// </summary>
    /// <remarks>
    /// Cases that did not produce a measurement (a failed job reports a null statistics block) and
    /// cases run without the memory diagnoser are left out rather than read as zero: a benchmark
    /// that failed to report must not look like the best result in the suite, and must surface as
    /// "not run" when a baseline expects it.
    /// </remarks>
    public static IReadOnlyList<BenchmarkMeasurement> ReadResults(string resultsDirectory)
    {
        if (!Directory.Exists(resultsDirectory))
            return Array.Empty<BenchmarkMeasurement>();

        var measurements = new List<BenchmarkMeasurement>();
        foreach (
            string file in Directory
                .GetFiles(resultsDirectory, "*-report-full-compressed.json")
                .OrderBy(f => f, StringComparer.Ordinal)
        )
        {
            measurements.AddRange(ParseResults(File.ReadAllText(file)));
        }

        _ = ByKey(measurements);
        return measurements.OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Parses the content of one BenchmarkDotNet JSON export.
    /// </summary>
    public static IReadOnlyList<BenchmarkMeasurement> ParseResults(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var results = new List<BenchmarkMeasurement>();

        if (
            !document.RootElement.TryGetProperty("Benchmarks", out JsonElement benchmarks)
            || benchmarks.ValueKind != JsonValueKind.Array
        )
        {
            return results;
        }

        foreach (JsonElement benchmark in benchmarks.EnumerateArray())
        {
            if (
                !benchmark.TryGetProperty("Statistics", out JsonElement statistics)
                || statistics.ValueKind != JsonValueKind.Object
                || !statistics.TryGetProperty("Mean", out JsonElement mean)
                || !benchmark.TryGetProperty("Memory", out JsonElement memory)
                || memory.ValueKind != JsonValueKind.Object
                || !memory.TryGetProperty("BytesAllocatedPerOperation", out JsonElement bytes)
                || bytes.GetInt64() < 0
            )
            {
                continue;
            }

            string type = StringProperty(benchmark, "Type");
            string method = StringProperty(benchmark, "Method");
            if (type.Length == 0 || method.Length == 0)
                continue;

            results.Add(
                new BenchmarkMeasurement(
                    type,
                    method,
                    StringProperty(benchmark, "Parameters"),
                    mean.GetDouble(),
                    bytes.GetInt64()
                )
            );
        }

        return results;
    }

    /// <summary>
    /// Describes the runtime and hardware a results directory was produced on.
    /// </summary>
    /// <returns>For example <c>.NET 8.0.22, Arm64, Apple M1</c>, or an empty string when unknown.</returns>
    public static string ReadEnvironment(string resultsDirectory)
    {
        if (!Directory.Exists(resultsDirectory))
            return string.Empty;

        foreach (
            string file in Directory
                .GetFiles(resultsDirectory, "*-report-full-compressed.json")
                .OrderBy(f => f, StringComparer.Ordinal)
        )
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (
                document.RootElement.TryGetProperty("HostEnvironmentInfo", out JsonElement host)
                && host.ValueKind == JsonValueKind.Object
            )
            {
                return string.Join(
                    ", ",
                    new[]
                    {
                        StringProperty(host, "RuntimeVersion"),
                        StringProperty(host, "Architecture"),
                        StringProperty(host, "ProcessorName"),
                    }.Where(part => part.Length > 0)
                );
            }
        }

        return string.Empty;
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // ──────────────────────────────────────────────────────────
    //  BASELINE FILE
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a baseline file, returning an empty list when it does not exist yet.
    /// </summary>
    public static IReadOnlyList<BenchmarkMeasurement> ReadBaseline(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<BenchmarkMeasurement>();

        return ParseBaseline(File.ReadAllText(path));
    }

    /// <summary>
    /// Reads the free-text description of where a baseline was recorded, or an empty string.
    /// </summary>
    public static string ReadBaselineEnvironment(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        string environment = StringProperty(document.RootElement, "environment");
        string recordedOn = StringProperty(document.RootElement, "recordedOn");
        return string.Join("; ", new[] { environment, recordedOn }.Where(s => s.Length > 0));
    }

    /// <summary>
    /// Parses baseline JSON.
    /// </summary>
    /// <exception cref="BaselineFormatException">
    /// The file is a schema this tool does not read. A schema 1 baseline keyed measurements by
    /// method and count only, and comparing one against current results would silently mismatch.
    /// </exception>
    public static IReadOnlyList<BenchmarkMeasurement> ParseBaseline(string json)
    {
        // JsonDocument rather than JsonSerializer: no reflection, so nothing here trips the trim
        // and AOT analyzers, and the shape is flat enough that a mapper would not earn its keep.
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("measurements", out JsonElement measurements))
        {
            return Array.Empty<BenchmarkMeasurement>();
        }

        int schema =
            document.RootElement.TryGetProperty("schema", out JsonElement schemaElement)
            && schemaElement.ValueKind == JsonValueKind.Number
                ? schemaElement.GetInt32()
                : 0;
        if (schema != BaselineSchema)
        {
            throw new BaselineFormatException(
                $"Baseline schema {schema} is not readable; this tool reads schema {BaselineSchema}. Re-record it with --update-baseline."
            );
        }

        var results = new List<BenchmarkMeasurement>();
        foreach (JsonElement element in measurements.EnumerateArray())
        {
            string type = StringProperty(element, "type");
            string method = StringProperty(element, "method");
            if (type.Length == 0 || method.Length == 0)
            {
                throw new BaselineFormatException(
                    "A baseline measurement has no type or method; it cannot be identified."
                );
            }

            results.Add(
                new BenchmarkMeasurement(
                    type,
                    method,
                    StringProperty(element, "parameters"),
                    element.GetProperty("meanNanoseconds").GetDouble(),
                    element.GetProperty("allocatedBytes").GetInt64()
                )
            );
        }

        try
        {
            _ = ByKey(results);
        }
        catch (InvalidOperationException ex)
        {
            throw new BaselineFormatException(ex.Message, ex);
        }

        return results;
    }

    /// <summary>
    /// Renders a baseline file.
    /// </summary>
    /// <param name="measurements">What to record.</param>
    /// <param name="environment">The runtime and hardware of the run, from <see cref="ReadEnvironment"/>.</param>
    /// <param name="recordedOn">Free text about the machine and conditions, supplied by whoever records it.</param>
    public static string WriteBaseline(
        IReadOnlyList<BenchmarkMeasurement> measurements,
        string environment = "",
        string recordedOn = ""
    )
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine(
            string.Create(CultureInfo.InvariantCulture, $"  \"schema\": {BaselineSchema},")
        );
        builder.AppendLine(
            "  \"note\": \"Regenerate with the benchmarks workflow's update_baseline input or the command in docs/BENCHMARKS.md. Allocated bytes are the gate; times are indicative and are not compared.\","
        );
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"  \"environment\": {JsonString(environment)},"
        );
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"  \"recordedOn\": {JsonString(recordedOn)},"
        );
        builder.AppendLine("  \"measurements\": [");

        List<BenchmarkMeasurement> ordered = measurements
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            BenchmarkMeasurement m = ordered[i];
            string comma = i < ordered.Count - 1 ? "," : string.Empty;
            builder.AppendLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"    {{ \"type\": {JsonString(m.Type)}, \"method\": {JsonString(m.Method)}, \"parameters\": {JsonString(m.Parameters)}, \"meanNanoseconds\": {m.MeanNanoseconds:F1}, \"allocatedBytes\": {m.AllocatedBytes} }}{comma}"
                )
            );
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static readonly JsonSerializerOptions StringOptions = new()
    {
        // Keep apostrophes and non-ASCII readable in a file people review by eye.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string JsonString(string value) =>
        JsonSerializer.Serialize(value, StringOptions);

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

        builder.AppendLine(
            regressions > 0
                ? $"**{regressions} allocation regression(s).**"
                : "No allocation regressions."
        );

        if (slowdowns > 0)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $" {slowdowns} benchmark(s) slower than tolerance — informational, see the note below."
            );
        }

        if (improvements > 0)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $" {improvements} improvement(s) — refresh the baseline to lock them in."
            );
        }

        builder.AppendLine();
        builder.AppendLine("| Benchmark | Verdict | Detail |");
        builder.AppendLine("|:--- |:---:|:--- |");

        foreach (
            BenchmarkComparison c in comparisons.Where(c => c.Kind != RegressionKind.Unchanged)
        )
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

            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| `{c.Key}` | {icon} | {c.Detail} |"
            );
        }

        if (comparisons.All(c => c.Kind == RegressionKind.Unchanged))
        {
            builder.AppendLine("| _all benchmarks_ | ✅ | Within tolerance of the baseline. |");
        }

        builder.AppendLine();
        builder.AppendLine(
            "> Allocated bytes is the gate; wall-clock is reported but does not fail the build. Allocation"
        );
        builder.AppendLine(
            "> counts on a fixed input are near-deterministic, so a change in them is a real change in what"
        );
        builder.AppendLine(
            "> the code does. Wall-clock on a shared runner moves tens of percent between runs of identical"
        );
        builder.AppendLine(
            "> code, and a gate nobody can act on is a gate that gets switched off."
        );

        return builder.ToString();
    }

    /// <summary>
    /// Whether the comparison should fail the build.
    /// </summary>
    public static bool HasFailures(IReadOnlyList<BenchmarkComparison> comparisons, bool failOnTime)
    {
        ArgumentNullException.ThrowIfNull(comparisons);

        return comparisons.Any(c =>
            c.Kind == RegressionKind.AllocationRegression
            || (failOnTime && c.Kind == RegressionKind.TimeRegression)
        );
    }

    /// <summary>
    /// How many benchmarks were present in both the baseline and the run.
    /// </summary>
    public static int CountCompared(IReadOnlyList<BenchmarkComparison> comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);

        return comparisons.Count(c => c.Baseline is not null && c.Current is not null);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):F2} MB");
        if (bytes >= 1024L)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:F1} KB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
    }

    private static string FormatNanoseconds(double nanoseconds)
    {
        if (nanoseconds >= 1_000_000_000d)
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{nanoseconds / 1_000_000_000d:F2} s"
            );
        if (nanoseconds >= 1_000_000d)
            return string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000_000d:F2} ms");
        if (nanoseconds >= 1_000d)
            return string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000d:F1} μs");
        return string.Create(CultureInfo.InvariantCulture, $"{nanoseconds:F0} ns");
    }
}
