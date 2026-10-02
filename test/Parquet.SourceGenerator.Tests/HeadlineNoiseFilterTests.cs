using System;
using System.IO;
using Parquet.SourceGenerator.Tools;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The headline table is refreshed from a shared CI runner, where timing moves tens of percent
/// between identical runs. These tests pin what counts as a change worth a pull request (#568).
/// </summary>
public sealed class HeadlineNoiseFilterTests
{
    private const double AllocationThreshold = 0.10;
    private const double TimeThreshold = 0.30;

    private static string Table(
        string label = "File Serialization (Write)",
        string scale = "100,000 items",
        string baselineTime = "6.66 ms",
        string baselineMemory = "11.00 MB",
        string time = "2.57 ms",
        string memory = "5.74 MB"
    ) =>
        $"""
            ## Performance

            | Operation | Scale | Reflection Baseline | Source Generator | Speedup | Memory Reduction |
            |:--- |:---:|:---:|:---:|:---:|:---:|
            | **{label}** | {scale} | {baselineTime} ({baselineMemory}) | **{time}** (**{memory}**) | ⚡ **2.6x faster** | 📉 **48% less memory** |

            > note
            """;

    private static bool Significant(string committed, string fresh, out string reason) =>
        HeadlineNoiseFilter.IsSignificant(
            committed,
            fresh,
            AllocationThreshold,
            TimeThreshold,
            out reason
        );

    [Fact]
    public void AnIdenticalTableIsNotAChange()
    {
        Significant(Table(), Table(), out _).ShouldBeFalse();
    }

    /// <summary>
    /// The case this exists for: same code, a noisier runner. Time moved 20%, allocation 2%.
    /// </summary>
    [Fact]
    public void SmallTimingAndAllocationDriftIsNoise()
    {
        Significant(Table(), Table(time: "3.08 ms", memory: "5.85 MB"), out string reason)
            .ShouldBeFalse();

        reason.ShouldContain("No row moved");
    }

    [Fact]
    public void AnAllocationChangeBeyondTheThresholdIsSignal()
    {
        // 5.74 MB -> 6.60 MB is +15%.
        Significant(Table(), Table(memory: "6.60 MB"), out string reason).ShouldBeTrue();

        reason.ShouldContain("allocation");
    }

    [Fact]
    public void AnAllocationChangeJustInsideTheThresholdIsNoise()
    {
        // 5.74 MB -> 6.30 MB is +9.8%.
        Significant(Table(), Table(memory: "6.30 MB"), out _).ShouldBeFalse();
    }

    [Fact]
    public void ALargeTimingChangeIsSignal()
    {
        // 2.57 ms -> 4.00 ms is +56%.
        Significant(Table(), Table(time: "4.00 ms"), out string reason).ShouldBeTrue();

        reason.ShouldContain("time");
    }

    /// <summary>
    /// Units change as a number crosses a boundary; comparing the printed digits would read
    /// 900 μs -> 1.40 ms as an improvement.
    /// </summary>
    [Fact]
    public void ChangesAreComparedAcrossUnits()
    {
        Significant(Table(time: "900.0 μs"), Table(time: "1.40 ms"), out string reason)
            .ShouldBeTrue();

        reason.ShouldContain("+55");
    }

    [Fact]
    public void AnAddedOrRemovedRowIsAlwaysAChange()
    {
        Significant(Table(), Table(label: "Guid Serialization"), out string reason).ShouldBeTrue();

        reason.ShouldContain("Rows changed");
    }

    [Fact]
    public void AChangedScaleIsAlwaysAChange()
    {
        Significant(Table(), Table(scale: "10,000 items"), out string reason).ShouldBeTrue();

        reason.ShouldContain("scale");
    }

    /// <summary>
    /// The filter must fail open: a table it cannot read is rewritten, never silently kept.
    /// </summary>
    [Fact]
    public void ATableWithNothingReadableIsAChange()
    {
        Significant("", Table(), out _).ShouldBeTrue();
        Significant(Table(), "no table here", out _).ShouldBeTrue();
    }

    [Fact]
    public void ARowThatChangedShapeIsAChange()
    {
        // The new cell lost its memory figure.
        Significant(
                Table(),
                Table().Replace("(**5.74 MB**)", "", System.StringComparison.Ordinal),
                out _
            )
            .ShouldBeTrue();
    }

    /// <summary>
    /// The reader has to understand the table the generator really writes. If a format change made
    /// every row unreadable the filter would fail open, and a green run of the other tests would
    /// hide that the noise filter no longer filters anything.
    /// </summary>
    [Fact]
    public void TheCommittedReadmeTableIsReadableAndUnchangedAgainstItself()
    {
        string root = AppContext.BaseDirectory;
        while (
            !System.IO.File.Exists(Path.Combine(root, "README.md"))
            || !System.IO.File.Exists(Path.Combine(root, "Parquet.SourceGenerator.slnx"))
        )
        {
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("README.md");
        }

        string readme = System.IO.File.ReadAllText(Path.Combine(root, "README.md"));
        const string start = "<!-- BENCHMARK_TABLE_START -->";
        const string end = "<!-- BENCHMARK_TABLE_END -->";
        int from = readme.IndexOf(start, StringComparison.Ordinal) + start.Length;
        string table = readme[from..readme.IndexOf(end, StringComparison.Ordinal)];

        Significant(table, table, out string reason).ShouldBeFalse(reason);
    }
}
