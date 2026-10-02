using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parquet.SourceGenerator.Tools;
using Shouldly;
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
    //  READING BENCHMARKDOTNET JSON
    // ──────────────────────────────────────────────────────────

    private const string ExportWithOneFailedCase = """
        {
          "Title": "Parquet.SourceGenerator.Benchmarks.Demo-20261002-000000",
          "HostEnvironmentInfo": {
            "RuntimeVersion": ".NET 8.0.22 (8.0.2225.52707)",
            "Architecture": "Arm64",
            "ProcessorName": "Apple M1"
          },
          "Benchmarks": [
            {
              "Type": "ReadBench", "Method": "Read", "Parameters": "Count=1000",
              "Statistics": { "Mean": 1234567.5 },
              "Memory": { "BytesAllocatedPerOperation": 2621440 }
            },
            {
              "Type": "ReadBench", "Method": "Read", "Parameters": "Count=1000",
              "Statistics": null,
              "Memory": null
            },
            {
              "Type": "ReadBench", "Method": "NoDiagnoser", "Parameters": "Count=1000",
              "Statistics": { "Mean": 10 },
              "Memory": null
            }
          ]
        }
        """;

    /// <summary>
    /// A case that failed (null statistics) or was run without the memory diagnoser has no
    /// measurement. Reading it as zero would make it the best result in the suite.
    /// </summary>
    [Fact]
    public void CasesWithoutAMeasurementAreLeftOutRatherThanReadAsZero()
    {
        BenchmarkMeasurement measurement = RegressionCheck
            .ParseResults(ExportWithOneFailedCase)
            .ShouldHaveSingleItem();

        measurement.Type.ShouldBe("ReadBench");
        measurement.Method.ShouldBe("Read");
        measurement.Parameters.ShouldBe("Count=1000");
        measurement.MeanNanoseconds.ShouldBe(1_234_567.5d);
        measurement.AllocatedBytes.ShouldBe(2_621_440L);
    }

    [Fact]
    public void AnExportWithNoBenchmarksYieldsNothing()
    {
        RegressionCheck.ParseResults("""{ "Benchmarks": [] }""").ShouldBeEmpty();
        RegressionCheck.ParseResults("{}").ShouldBeEmpty();
    }

    [Fact]
    public void TheEnvironmentOfARunIsReadFromItsExport()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("bench-env-");
        try
        {
            System.IO.File.WriteAllText(
                Path.Combine(directory.FullName, "Demo-report-full-compressed.json"),
                ExportWithOneFailedCase
            );

            RegressionCheck
                .ReadEnvironment(directory.FullName)
                .ShouldBe(".NET 8.0.22 (8.0.2225.52707), Arm64, Apple M1");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ──────────────────────────────────────────────────────────
    //  THRESHOLDS
    // ──────────────────────────────────────────────────────────

    private static BenchmarkMeasurement Measurement(
        double meanNs,
        long allocated,
        string method = "Read",
        int count = 1000,
        string type = "ReadBench"
    ) => new(type, method, $"Count={count}", meanNs, allocated);

    [Fact]
    public void AllocationGrowthBeyondToleranceIsARegression()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1_000_000) },
            new[] { Measurement(1000, 1_500_000) }
        );

        BenchmarkComparison result = comparisons.ShouldHaveSingleItem();
        result.Kind.ShouldBe(RegressionKind.AllocationRegression);
        RegressionCheck.HasFailures(comparisons, failOnTime: false).ShouldBeTrue();
    }

    [Fact]
    public void AllocationGrowthWithinToleranceIsNotARegression()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1_000_000) },
            new[] { Measurement(1000, 1_020_000) }
        );

        comparisons.ShouldHaveSingleItem().Kind.ShouldBe(RegressionKind.Unchanged);
        RegressionCheck.HasFailures(comparisons, failOnTime: false).ShouldBeFalse();
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
            new[] { Measurement(1000, 400) }
        );

        comparisons.ShouldHaveSingleItem().Kind.ShouldBe(RegressionKind.Unchanged);
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
            new[] { Measurement(3_000_000, 1000) }
        );

        comparisons.ShouldHaveSingleItem().Kind.ShouldBe(RegressionKind.TimeRegression);
        RegressionCheck.HasFailures(comparisons, failOnTime: false).ShouldBeFalse();
        RegressionCheck.HasFailures(comparisons, failOnTime: true).ShouldBeTrue();
    }

    /// <summary>
    /// An allocation regression outranks a simultaneous slowdown, because it is the actionable one.
    /// </summary>
    [Fact]
    public void AllocationRegressionWinsOverASimultaneousSlowdown()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1_000_000, 1_000_000) },
            new[] { Measurement(3_000_000, 2_000_000) }
        );

        comparisons.ShouldHaveSingleItem().Kind.ShouldBe(RegressionKind.AllocationRegression);
    }

    [Fact]
    public void AllocationDropIsReportedAsAnImprovement()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 2_000_000) },
            new[] { Measurement(1000, 1_000_000) }
        );

        comparisons.ShouldHaveSingleItem().Kind.ShouldBe(RegressionKind.Improved);
        RegressionCheck.HasFailures(comparisons, failOnTime: false).ShouldBeFalse();
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
            new[] { Measurement(1_000_000, 1_000_000) }
        );

        comparisons.ShouldHaveSingleItem().Kind.ShouldBe(RegressionKind.Unchanged);
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
            new[] { Measurement(1000, 1000, "Read") }
        );

        BenchmarkComparison missing = comparisons
            .Where(c => c.Kind == RegressionKind.NotRun)
            .ShouldHaveSingleItem();
        missing.Key.ShouldBe("ReadBench.Write(Count=1000)");
    }

    [Fact]
    public void BenchmarkAbsentFromTheBaselineIsReportedAsNewAndDoesNotFail()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1000, "Read") },
            new[] { Measurement(1000, 1000, "Read"), Measurement(1000, 1000, "ReadParallel") }
        );

        BenchmarkComparison added = comparisons
            .Where(c => c.Kind == RegressionKind.New)
            .ShouldHaveSingleItem();
        added.Key.ShouldBe("ReadBench.ReadParallel(Count=1000)");
        RegressionCheck.HasFailures(comparisons, failOnTime: false).ShouldBeFalse();
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
            new[] { Measurement(1000, 1_000_000, "Read", 100_000) }
        );

        comparisons.ShouldContain(c =>
            c.Kind == RegressionKind.New
            && c.Key.EndsWith("(Count=100000)", StringComparison.Ordinal)
        );
        comparisons.ShouldContain(c =>
            c.Kind == RegressionKind.NotRun
            && c.Key.EndsWith("(Count=1000)", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// <c>WriteSnappyAsync</c> is declared in two benchmark classes. Keyed by method and count
    /// they were one benchmark, and whichever was read last silently replaced the other.
    /// </summary>
    [Fact]
    public void ASharedMethodNameInTwoClassesIsTwoBenchmarks()
    {
        BenchmarkMeasurement tpch = Measurement(1000, 1_000_000, "WriteSnappyAsync", type: "Tpch");
        BenchmarkMeasurement census = Measurement(
            1000,
            9_000_000,
            "WriteSnappyAsync",
            type: "Census"
        );

        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { tpch, census },
            new[] { tpch, census with { AllocatedBytes = 50_000_000 } }
        );

        comparisons.Count.ShouldBe(2);
        comparisons
            .Single(c => c.Kind == RegressionKind.AllocationRegression)
            .Key.ShouldStartWith("Census.");
        comparisons.Single(c => c.Kind == RegressionKind.Unchanged).Key.ShouldStartWith("Tpch.");
    }

    /// <summary>
    /// A benchmark parameterised by something other than <c>Count</c> is one benchmark per
    /// parameter set.
    /// </summary>
    [Fact]
    public void ParametersOtherThanCountAreAPartOfTheIdentity()
    {
        var sixtyFour = new BenchmarkMeasurement("Pruning", "Open", "RowGroups=64", 1, 322_257);
        var thousand = new BenchmarkMeasurement("Pruning", "Open", "RowGroups=1024", 1, 5_107_009);

        sixtyFour.Key.ShouldNotBe(thousand.Key);

        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { sixtyFour, thousand },
            new[] { sixtyFour, thousand }
        );

        comparisons.ShouldAllBe(c => c.Kind == RegressionKind.Unchanged);
        comparisons.Count.ShouldBe(2);
    }

    [Fact]
    public void TwoMeasurementsWithOneIdentityAreRefusedRatherThanOneDropped()
    {
        BenchmarkMeasurement one = Measurement(1, 100);

        Should.Throw<InvalidOperationException>(() =>
            RegressionCheck.Compare(new[] { one, one }, new[] { one })
        );
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

        IReadOnlyList<BenchmarkMeasurement> restored = RegressionCheck.ParseBaseline(
            RegressionCheck.WriteBaseline(original)
        );

        restored.Count.ShouldBe(2);
        BenchmarkMeasurement read = restored.Single(m => m.Method == "SourceGeneratorReadAsync");
        read.Type.ShouldBe("ReadBench");
        read.Parameters.ShouldBe("Count=100000");
        read.AllocatedBytes.ShouldBe(2_621_440L);
        read.MeanNanoseconds.ShouldBe(1_234_500d);
    }

    [Fact]
    public void ARoundTrippedBaselineComparesAsUnchangedAgainstItself()
    {
        // Serialisation rounds the mean to one decimal place. If that rounding were coarse enough
        // to matter, every run would regress against a baseline taken from the same numbers.
        var measurements = new[] { Measurement(1_234_567.89, 2_621_440, "Read", 100_000) };

        IReadOnlyList<BenchmarkMeasurement> restored = RegressionCheck.ParseBaseline(
            RegressionCheck.WriteBaseline(measurements)
        );

        RegressionCheck
            .Compare(restored, measurements)
            .ShouldHaveSingleItem()
            .Kind.ShouldBe(RegressionKind.Unchanged);
    }

    [Fact]
    public void ABaselineRecordsWhereItWasTaken()
    {
        string json = RegressionCheck.WriteBaseline(
            new[] { Measurement(1, 100) },
            ".NET 8.0.22, Arm64, Apple M1",
            "quiet laptop, load 1.2"
        );

        DirectoryInfo directory = Directory.CreateTempSubdirectory("bench-baseline-");
        try
        {
            string path = Path.Combine(directory.FullName, "baseline.json");
            System.IO.File.WriteAllText(path, json);

            RegressionCheck
                .ReadBaselineEnvironment(path)
                .ShouldBe(".NET 8.0.22, Arm64, Apple M1; quiet laptop, load 1.2");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A schema 1 baseline keyed measurements by method and count only. Comparing it against
    /// current results would mismatch silently, so it is refused until it is re-recorded.
    /// </summary>
    [Fact]
    public void ASchemaOneBaselineIsRefusedRatherThanMismatchedSilently()
    {
        const string schemaOne = """
            { "schema": 1, "measurements": [
              { "method": "Read", "count": 1000, "meanNanoseconds": 1.0, "allocatedBytes": 100 } ] }
            """;

        Should.Throw<BaselineFormatException>(() => RegressionCheck.ParseBaseline(schemaOne));
    }

    [Fact]
    public void ABaselineWithTwoMeasurementsOfOneIdentityIsRefused()
    {
        string json = RegressionCheck.WriteBaseline(
            new[] { Measurement(1, 100), Measurement(1, 5) }
        );

        Should.Throw<BaselineFormatException>(() => RegressionCheck.ParseBaseline(json));
    }

    [Fact]
    public void ReportNamesTheRegressedBenchmark()
    {
        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            new[] { Measurement(1000, 1_000_000, "SourceGeneratorReadAsync") },
            new[] { Measurement(1000, 4_000_000, "SourceGeneratorReadAsync") }
        );

        string report = RegressionCheck.BuildReport(comparisons);

        report.ShouldContain("SourceGeneratorReadAsync");
        report.ShouldContain("1 allocation regression(s)");
    }
}
