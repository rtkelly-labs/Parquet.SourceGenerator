using System;
using System.IO;
using Parquet.SourceGenerator.Tools;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Drives the <c>--baseline</c> entry point of the benchmark summary tool, the exact command the
/// benchmarks workflow runs as its regression gate.
/// </summary>
/// <remarks>
/// <see cref="BenchmarkRegressionTests"/> covers the comparison logic. What it cannot see is the
/// entry point, where a guard placed above the baseline dispatch once turned a missing results
/// directory into a green exit (#408) and a missing baseline bootstrapped itself (#412). Every
/// case here is one of those ways for the gate to pass having examined nothing, plus the positive
/// controls that show it still fails on a real regression.
/// </remarks>
public sealed class BenchmarkGateEntryPointTests : IDisposable
{
    private const string ReportHeader = "Method,Count,Mean,Allocated";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bench-gate-");

    private string ResultsDirectory => Path.Combine(_root.FullName, "results");

    private string BaselinePath => Path.Combine(_root.FullName, "baseline.json");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void AMissingResultsDirectoryFailsTheGate()
    {
        WriteBaseline(Measured("Read", 1_000_000));

        // The results directory is never created: a renamed artifacts path, a rejected filter or
        // a crash before export. This used to exit 0.
        Run().ShouldBe(1);
    }

    [Fact]
    public void AnEmptyResultsDirectoryFailsTheGate()
    {
        WriteBaseline(Measured("Read", 1_000_000));
        Directory.CreateDirectory(ResultsDirectory);

        Run().ShouldBe(1);
    }

    [Fact]
    public void AMissingBaselineFailsWithoutWritingOne()
    {
        WriteResults("Read", 1000, 1_000_000);

        Run().ShouldBe(1);

        System.IO.File.Exists(BaselinePath).ShouldBeFalse();
    }

    [Fact]
    public void ABootstrapRunRecordsTheBaselineOnlyWhenAskedTo()
    {
        WriteResults("Read", 1000, 1_000_000);

        Run("--bootstrap").ShouldBe(0);

        RegressionCheck.ReadBaseline(BaselinePath).ShouldHaveSingleItem().Method.ShouldBe("Read");
    }

    [Fact]
    public void AnEmptyBaselineFailsWithoutBeingRewritten()
    {
        WriteResults("Read", 1000, 1_000_000);
        System.IO.File.WriteAllText(BaselinePath, RegressionCheck.WriteBaseline([]));
        string before = System.IO.File.ReadAllText(BaselinePath);

        Run().ShouldBe(1);

        System.IO.File.ReadAllText(BaselinePath).ShouldBe(before);
    }

    [Fact]
    public void ARunSharingNothingWithTheBaselineFailsTheGate()
    {
        // Every method renamed, or a filter that skipped them all: each row is New or NotRun and
        // neither fails on its own, so without an examined-N check this is a green run that
        // compared nothing.
        WriteBaseline(Measured("Read", 1_000_000));
        WriteResults("Renamed", 1000, 1_000_000);

        Run().ShouldBe(1);
    }

    [Fact]
    public void AnAllocationRegressionFailsTheGate()
    {
        // The positive control: the gate is only worth anything if it goes red when it should.
        WriteBaseline(Measured("Read", 1_000_000));
        WriteResults("Read", 1000, 4_000_000);

        Run().ShouldBe(1);
    }

    [Fact]
    public void AnUnchangedRunPassesTheGate()
    {
        WriteBaseline(Measured("Read", 1_000_000));
        WriteResults("Read", 1000, 1_000_000);

        Run().ShouldBe(0);
    }

    [Fact]
    public void ABenchmarkMissingFromTheRunFailsOnlyWhenRequired()
    {
        WriteBaseline(Measured("Read", 1_000_000), Measured("Write", 1_000_000));
        WriteResults("Read", 1000, 1_000_000);

        Run().ShouldBe(0);
        Run("--fail-on-not-run").ShouldBe(1);
    }

    [Fact]
    public void ACheckRunNeverModifiesTheBaseline()
    {
        WriteBaseline(Measured("Read", 1_000_000));
        WriteResults("Read", 1000, 4_000_000);
        string before = System.IO.File.ReadAllText(BaselinePath);

        _ = Run();

        System.IO.File.ReadAllText(BaselinePath).ShouldBe(before);
    }

    private int Run(params string[] extra)
    {
        string[] args = [ResultsDirectory, "--baseline", BaselinePath, .. extra];
        return Program.Main(args);
    }

    private void WriteBaseline(params BenchmarkMeasurement[] measurements) =>
        System.IO.File.WriteAllText(BaselinePath, RegressionCheck.WriteBaseline(measurements));

    private static BenchmarkMeasurement Measured(string method, long allocated) =>
        new(method, 1000, 1000d, allocated);

    private void WriteResults(string method, int count, long allocated)
    {
        Directory.CreateDirectory(ResultsDirectory);
        System.IO.File.WriteAllText(
            Path.Combine(ResultsDirectory, "Gate-report.csv"),
            $"{ReportHeader}\n{method},{count},1000 ns,{allocated} B\n"
        );
    }
}
