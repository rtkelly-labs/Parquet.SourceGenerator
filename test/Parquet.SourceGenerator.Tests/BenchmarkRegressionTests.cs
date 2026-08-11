using System.Collections.Generic;
using System.Linq;
using Parquet.SourceGenerator.Tools;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Covers the benchmark regression gate.
/// </summary>
/// <remarks>
/// The gate itself cannot be verified by running benchmarks in CI — that is the whole reason the
/// benchmark workflow is manual. What can be verified is everything around the measurement: that
/// units are normalised before anything is compared, that the thresholds fire where they should and
/// stay quiet where they should not, and that a baseline survives a round trip. A gate with a bug in
/// any of those reports success regardless of what the numbers say.
/// </remarks>
public sealed class BenchmarkRegressionTests
{
    // ──────────────────────────────────────────────────────────
    //  UNIT NORMALISATION
    // ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1234 ns", 1234d)]
    [InlineData("1.5 us", 1_500d)]
    [InlineData("1.5 μs", 1_500d)]
    [InlineData("1,234.5 μs", 1_234_500d)]
    [InlineData("2 ms", 2_000_000d)]
    [InlineData("0.5 s", 500_000_000d)]
    public void DurationsNormaliseToNanoseconds(string value, double expected)
    {
        Assert.Equal(expected, RegressionCheck.ParseTimeToNanoseconds(value));
    }

    /// <summary>
    /// The failure this exists to prevent: BenchmarkDotNet picks whichever unit reads best, so a
    /// method that slows from 900 μs to 1.2 ms is printed with two different units. Comparing the
    /// printed numbers would read 1.2 as an improvement on 900.
    /// </summary>
    [Fact]
    public void ASlowdownThatChangesUnitsIsStillASlowdown()
    {
        double? before = RegressionCheck.ParseTimeToNanoseconds("900.0 μs");
        double? after = RegressionCheck.ParseTimeToNanoseconds("1.2 ms");

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(after > before, $"1.2 ms ({after} ns) must compare as slower than 900 μs ({before} ns)");
    }

    [Theory]
    [InlineData("512 B", 512L)]
    [InlineData("1.5 KB", 1536L)]
    [InlineData("2 MB", 2_097_152L)]
    [InlineData("1,024 KB", 1_048_576L)]
    public void AllocationsNormaliseToBytes(string value, long expected)
    {
        Assert.Equal(expected, RegressionCheck.ParseMemoryToBytes(value));
    }

    /// <summary>
    /// "-", "NA" and "?" mean "no measurement", not zero. Parsing them as zero would make a
    /// benchmark that failed to report allocations look like the best result in the suite, and
    /// would then bake that into the baseline.
    /// </summary>
    [Theory]
    [InlineData("-")]
    [InlineData("NA")]
    [InlineData("?")]
    [InlineData("")]
    public void AbsentMeasurementsDoNotParseAsZero(string value)
    {
        Assert.Null(RegressionCheck.ParseMemoryToBytes(value));
        Assert.Null(RegressionCheck.ParseTimeToNanoseconds(value));
    }

    [Fact]
    public void UnitInTheHeaderIsHonouredWhenTheValueHasNone()
    {
        string[] csv =
        {
            "Method,Count,Mean [ms],Allocated [KB]",
            "SourceGeneratorReadAsync,1000,2.5,1.5",
        };

        BenchmarkMeasurement measurement = Assert.Single(RegressionCheck.ParseCsv(csv));

        Assert.Equal(2_500_000d, measurement.MeanNanoseconds);
        Assert.Equal(1536L, measurement.AllocatedBytes);
    }

    [Fact]
    public void CsvRowsParseIntoMeasurements()
    {
        string[] csv =
        {
            "Method,Count,Mean,Error,Allocated",
            "SourceGeneratorReadAsync,100000,\"1,234.5 μs\",1.0 μs,\"2.5 MB\"",
            "ReflectionParquetSerializerV6Read,100000,\"3,000.0 μs\",2.0 μs,\"9.0 MB\"",
        };

        IReadOnlyList<BenchmarkMeasurement> measurements = RegressionCheck.ParseCsv(csv);

        Assert.Equal(2, measurements.Count);
        BenchmarkMeasurement generated = measurements.Single(m => m.Method == "SourceGeneratorReadAsync");
        Assert.Equal(100_000, generated.Count);
        Assert.Equal(1_234_500d, generated.MeanNanoseconds);
        Assert.Equal(2_621_440L, generated.AllocatedBytes);
    }

    // ──────────────────────────────────────────────────────────
    //  THRESHOLDS
    // ──────────────────────────────────────────────────────────

    private static BenchmarkMeasurement Measurement(double meanNs, long allocated, string method = "Read", int count = 1000) =>
        new(method, count, meanNs, allocated);

    [Fact]
    public void AllocationGrowthBeyondToleranceIsARegression()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1_000_000) },
            new[] { Measurement(1000, 1_500_000) });

        BenchmarkComparison result = Assert.Single(comparisons);
        Assert.Equal(RegressionKind.AllocationRegression, result.Kind);
        Assert.True(RegressionCheck.HasFailures(comparisons, failOnTime: false));
    }

    [Fact]
    public void AllocationGrowthWithinToleranceIsNotARegression()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1_000_000) },
            new[] { Measurement(1000, 1_020_000) });

        Assert.Equal(RegressionKind.Unchanged, Assert.Single(comparisons).Kind);
        Assert.False(RegressionCheck.HasFailures(comparisons, failOnTime: false));
    }

    /// <summary>
    /// Without an absolute floor, a benchmark allocating a few hundred bytes fails the percentage
    /// rule on one extra small object — a build stopped for nothing.
    /// </summary>
    [Fact]
    public void TinyAbsoluteGrowthIsNoiseEvenWhenThePercentageIsLarge()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 200) },
            new[] { Measurement(1000, 400) });

        Assert.Equal(RegressionKind.Unchanged, Assert.Single(comparisons).Kind);
    }

    /// <summary>
    /// Wall-clock is reported but must not fail the build by default: on a shared runner it moves
    /// tens of percent between runs of identical code, and a gate nobody can act on gets disabled.
    /// </summary>
    [Fact]
    public void TimeRegressionIsReportedButDoesNotFailByDefault()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1_000_000, 1000) },
            new[] { Measurement(3_000_000, 1000) });

        Assert.Equal(RegressionKind.TimeRegression, Assert.Single(comparisons).Kind);
        Assert.False(RegressionCheck.HasFailures(comparisons, failOnTime: false));
        Assert.True(RegressionCheck.HasFailures(comparisons, failOnTime: true));
    }

    /// <summary>
    /// An allocation regression outranks a simultaneous slowdown, because it is the actionable one.
    /// </summary>
    [Fact]
    public void AllocationRegressionWinsOverASimultaneousSlowdown()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1_000_000, 1_000_000) },
            new[] { Measurement(3_000_000, 2_000_000) });

        Assert.Equal(RegressionKind.AllocationRegression, Assert.Single(comparisons).Kind);
    }

    [Fact]
    public void AllocationDropIsReportedAsAnImprovement()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 2_000_000) },
            new[] { Measurement(1000, 1_000_000) });

        Assert.Equal(RegressionKind.Improved, Assert.Single(comparisons).Kind);
        Assert.False(RegressionCheck.HasFailures(comparisons, failOnTime: false));
    }

    /// <summary>
    /// A faster wall-clock alone is not an improvement worth recording. Baking a quiet runner's
    /// numbers into the baseline makes the next honest run look like a regression.
    /// </summary>
    [Fact]
    public void FasterWallClockAloneIsNotRecordedAsAnImprovement()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(3_000_000, 1_000_000) },
            new[] { Measurement(1_000_000, 1_000_000) });

        Assert.Equal(RegressionKind.Unchanged, Assert.Single(comparisons).Kind);
    }

    // ──────────────────────────────────────────────────────────
    //  COVERAGE OF THE SUITE ITSELF
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// A filter that quietly skips half the suite must not read as "everything passed".
    /// </summary>
    [Fact]
    public void BenchmarkMissingFromTheRunIsReportedRatherThanIgnored()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1000, "Read"), Measurement(1000, 1000, "Write") },
            new[] { Measurement(1000, 1000, "Read") });

        BenchmarkComparison missing = Assert.Single(comparisons, c => c.Kind == RegressionKind.NotRun);
        Assert.Equal("Write", missing.Method);
    }

    [Fact]
    public void BenchmarkAbsentFromTheBaselineIsReportedAsNewAndDoesNotFail()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1000, "Read") },
            new[] { Measurement(1000, 1000, "Read"), Measurement(1000, 1000, "ReadParallel") });

        BenchmarkComparison added = Assert.Single(comparisons, c => c.Kind == RegressionKind.New);
        Assert.Equal("ReadParallel", added.Method);
        Assert.False(RegressionCheck.HasFailures(comparisons, failOnTime: false));
    }

    /// <summary>
    /// The same method at a different <c>[Params]</c> scale is a different benchmark. Keying on the
    /// name alone would compare a 1,000-row run against a 100,000-row baseline.
    /// </summary>
    [Fact]
    public void MeasurementsAreKeyedByScaleAsWellAsName()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1_000_000, "Read", 1_000) },
            new[] { Measurement(1000, 1_000_000, "Read", 100_000) });

        Assert.Contains(comparisons, c => c.Kind == RegressionKind.New && c.Count == 100_000);
        Assert.Contains(comparisons, c => c.Kind == RegressionKind.NotRun && c.Count == 1_000);
    }

    // ──────────────────────────────────────────────────────────
    //  BASELINE FILE
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void BaselineSurvivesARoundTrip()
    {
        var original = new[]
        {
            Measurement(1_234_500, 2_621_440, "SourceGeneratorReadAsync", 100_000),
            Measurement(9_000, 512, "SourceGeneratorGuidWriteAsync", 1_000),
        };

        IReadOnlyList<BenchmarkMeasurement> restored =
            RegressionCheck.ParseBaseline(RegressionCheck.WriteBaseline(original));

        Assert.Equal(2, restored.Count);
        BenchmarkMeasurement read = restored.Single(m => m.Method == "SourceGeneratorReadAsync");
        Assert.Equal(100_000, read.Count);
        Assert.Equal(2_621_440L, read.AllocatedBytes);
        Assert.Equal(1_234_500d, read.MeanNanoseconds);
    }

    [Fact]
    public void ARoundTrippedBaselineComparesAsUnchangedAgainstItself()
    {
        // Serialisation rounds the mean to one decimal place. If that rounding were coarse enough
        // to matter, every run would regress against a baseline taken from the same numbers.
        var measurements = new[] { Measurement(1_234_567.89, 2_621_440, "Read", 100_000) };

        IReadOnlyList<BenchmarkMeasurement> restored =
            RegressionCheck.ParseBaseline(RegressionCheck.WriteBaseline(measurements));

        Assert.Equal(RegressionKind.Unchanged, Assert.Single(RegressionCheck.Compare(restored, measurements)).Kind);
    }

    [Fact]
    public void ReportNamesTheRegressedBenchmark()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1_000_000, "SourceGeneratorReadAsync") },
            new[] { Measurement(1000, 4_000_000, "SourceGeneratorReadAsync") });

        string report = RegressionCheck.BuildReport(comparisons);

        Assert.Contains("SourceGeneratorReadAsync", report);
        Assert.Contains("1 allocation regression(s)", report);
    }
}
