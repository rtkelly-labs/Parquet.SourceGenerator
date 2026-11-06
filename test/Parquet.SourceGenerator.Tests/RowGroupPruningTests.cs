using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record PrunedOrder
{
    [ParquetColumn("order_key")]
    public long OrderKey { get; init; }

    [ParquetColumn("region")]
    public string Region { get; init; } = string.Empty;

    [ParquetColumn("quantity")]
    public int Quantity { get; init; }
}

/// <summary>
/// Row-group predicate pushdown (issue #149): the generated readers take an optional predicate over
/// footer statistics and never open the column pages of a row group the zone map rules out.
/// </summary>
/// <remarks>
/// Two things are worth pinning down, and they are different claims. Correctness — the surviving
/// rows are exactly the rows of the surviving row groups, and pruning never drops a row a predicate
/// admits. And the actual saving — a counting stream proves the skipped groups cost no bytes read,
/// which is the whole point of the feature and the part a plain LINQ <c>Where</c> cannot do.
/// </remarks>
public sealed class RowGroupPruningTests
{
    private const int RowsPerGroup = 500;
    private const int GroupCount = 10;

    /// <summary>
    /// Ten row groups of 500 rows, OrderKey running 0..4999 so group <c>g</c> owns exactly
    /// <c>[g*500, g*500+499]</c> — a clean zone map per group.
    /// </summary>
    private static async Task<byte[]> WriteAsync()
    {
        List<PrunedOrder> rows = Enumerable
            .Range(0, RowsPerGroup * GroupCount)
            .Select(i => new PrunedOrder
            {
                OrderKey = i,
                Region = $"region_{i / RowsPerGroup}",
                Quantity = i % 7,
            })
            .ToList();

        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = RowsPerGroup }
        );
        return stream.ToArray();
    }

    /// <summary>Counts the bytes a reader actually pulls, so a skipped row group is visible as I/O never done.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly MemoryStream _inner;

        public CountingStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            int read = _inner.Read(buffer.Span);
            BytesRead += read;
            return new ValueTask<int>(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task InequalityPredicateKeepsOnlyTheRowGroupsWhoseMaxReachesTheThreshold()
    {
        byte[] bytes = await WriteAsync();

        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta => meta.OrderKey.MayContainAtLeast(4_000))
            .ToListAsync();

        // Groups 8 and 9 hold keys 4000..4999; every earlier group's max is below the threshold.
        pruned.Count.ShouldBe(2 * RowsPerGroup);
        pruned[0].OrderKey.ShouldBe(4_000);
        pruned[^1].OrderKey.ShouldBe(4_999);
    }

    [Fact]
    public async Task RawMinMaxComparisonsReadTheSameWayTheIssueSpellsThem()
    {
        byte[] bytes = await WriteAsync();

        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta => meta.OrderKey.Max >= 1_000)
            .ToListAsync();

        // Group 1's max is 999, so the first group the raw comparison admits is group 2.
        pruned.Count.ShouldBe(8 * RowsPerGroup);
        pruned[0].OrderKey.ShouldBe(1_000);
    }

    [Fact]
    public async Task UpperBoundPredicatePrunesGroupsWhoseMinIsAlreadyPastIt()
    {
        byte[] bytes = await WriteAsync();

        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta => meta.OrderKey.MayContainAtMost(999))
            .ToListAsync();

        pruned.Count.ShouldBe(2 * RowsPerGroup);
        pruned[0].OrderKey.ShouldBe(0);
        pruned[^1].OrderKey.ShouldBe(999);
    }

    [Fact]
    public async Task EqualityPredicateKeepsTheSingleGroupWhoseRangeStraddlesTheValue()
    {
        byte[] bytes = await WriteAsync();

        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta => meta.OrderKey.MayContain(2_222))
            .ToListAsync();

        pruned.Count.ShouldBe(RowsPerGroup);
        pruned.ShouldContain(o => o.OrderKey == 2_222);
        pruned.ShouldAllBe(o => o.OrderKey >= 2_000 && o.OrderKey <= 2_499);
    }

    [Fact]
    public async Task ConjunctiveMultiColumnPredicateFailsTheGroupOnTheFirstColumnThatCannotMatch()
    {
        byte[] bytes = await WriteAsync();

        // The key range admits groups 4..9; the region equality admits only group 4.
        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta =>
                meta.OrderKey.MayContainAtLeast(2_000) && meta.Region.MayContain("region_4")
            )
            .ToListAsync();

        pruned.Count.ShouldBe(RowsPerGroup);
        pruned.ShouldAllBe(o => o.Region == "region_4");
    }

    [Fact]
    public async Task APredicateNoGroupCanSatisfyReturnsNothing()
    {
        byte[] bytes = await WriteAsync();

        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta => meta.OrderKey.MayContainAtLeast(1_000_000))
            .ToListAsync();

        pruned.ShouldBeEmpty();
    }

    [Fact]
    public async Task PruningNeverDropsARowTheEquivalentLinqFilterKeeps()
    {
        byte[] bytes = await WriteAsync();

        List<PrunedOrder> all = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .ToListAsync();
        List<PrunedOrder> expected = all.Where(o => o.OrderKey >= 3_100).ToList();

        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta => meta.OrderKey.MayContainAtLeast(3_100))
            .ToListAsync();

        // Pruning is a superset filter: whole groups only, so the surviving set still contains
        // every row the row-level predicate would keep.
        pruned.Count.ShouldBe(RowsPerGroup * 4);
        expected.Select(o => o.OrderKey).Except(pruned.Select(o => o.OrderKey)).ShouldBeEmpty();
        pruned.Where(o => o.OrderKey >= 3_100).ShouldAllBe(o => expected.Contains(o));
    }

    [Fact]
    public async Task SkippedRowGroupsCostNoBytesRead()
    {
        byte[] bytes = await WriteAsync();

        using var fullStream = new CountingStream(bytes);
        List<PrunedOrder> all = await PrunedOrderParquet.From(fullStream).ToListAsync();

        using var prunedStream = new CountingStream(bytes);
        List<PrunedOrder> pruned = await PrunedOrderParquet
            .From(prunedStream)
            .Where(meta => meta.OrderKey.MayContainAtLeast(4_500))
            .ToListAsync();

        all.Count.ShouldBe(RowsPerGroup * GroupCount);
        pruned.Count.ShouldBe(RowsPerGroup);

        // One group of ten survives, so the data pages read should collapse by roughly an order of
        // magnitude. The footer is read either way, so this is deliberately a loose bound.
        (prunedStream.BytesRead * 4 < fullStream.BytesRead).ShouldBeTrue(
            $"pruned read {prunedStream.BytesRead} bytes, full read {fullStream.BytesRead}"
        );
    }

    [Fact]
    public async Task ArrayOverloadPrunesTheSameWay()
    {
        byte[] bytes = await WriteAsync();

        PrunedOrder[] pruned = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta => meta.OrderKey.MayContainAtLeast(4_500))
            .ToArrayAsync();

        pruned.Length.ShouldBe(RowsPerGroup);
        pruned[0].OrderKey.ShouldBe(4_500);
    }

    [Fact]
    public async Task StreamingOverloadPrunesTheSameWay()
    {
        byte[] bytes = await WriteAsync();

        var streamed = new List<PrunedOrder>();
        await foreach (
            PrunedOrder item in PrunedOrderParquet
                .From(new MemoryStream(bytes))
                .Where(meta => meta.OrderKey.MayContainAtLeast(4_500))
                .AsAsyncEnumerable()
        )
        {
            streamed.Add(item);
        }

        streamed.Count.ShouldBe(RowsPerGroup);
        streamed[0].OrderKey.ShouldBe(4_500);
    }

    [Fact]
    public async Task BufferStreamingOverloadPrunesTheSameWay()
    {
        byte[] bytes = await WriteAsync();

        var streamed = new List<PrunedOrder>();
        await foreach (
            PrunedOrder item in PrunedOrderParquet
                .From(new ReadOnlyMemory<byte>(bytes))
                .Where(meta => meta.OrderKey.MayContainAtLeast(4_500))
                .AsAsyncEnumerable()
        )
        {
            streamed.Add(item);
        }

        streamed.Count.ShouldBe(RowsPerGroup);
    }

    [Fact]
    public async Task NoPredicateReadsEveryRowExactlyAsBefore()
    {
        byte[] bytes = await WriteAsync();

        List<PrunedOrder> all = await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .ToListAsync();

        all.Count.ShouldBe(RowsPerGroup * GroupCount);
        all.Select(o => o.OrderKey)
            .ShouldBe(Enumerable.Range(0, RowsPerGroup * GroupCount).Select(i => (long)i));
    }

    [Fact]
    public async Task MetadataCarriesRowGroupIdentityAndNullCounts()
    {
        byte[] bytes = await WriteAsync();

        var seen = new List<(int Index, long RowCount, long? Min, long? Max, long? Nulls)>();
        await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta =>
            {
                seen.Add(
                    (
                        meta.RowGroupIndex,
                        meta.RowCount,
                        meta.OrderKey.Min,
                        meta.OrderKey.Max,
                        meta.OrderKey.NullCount
                    )
                );
                return false;
            })
            .ToListAsync();

        seen.Count.ShouldBe(GroupCount);
        seen.Select(s => s.Index).ShouldBe(Enumerable.Range(0, GroupCount));
        seen.ShouldAllBe(s => s.RowCount == RowsPerGroup);
        seen.ShouldAllBe(s => s.Nulls == 0);
        seen[0].Min.ShouldBe(0);
        seen[0].Max.ShouldBe(499);
        seen[^1].Min.ShouldBe(4_500);
        seen[^1].Max.ShouldBe(4_999);
        seen.ShouldAllBe(s => s.Max - s.Min == RowsPerGroup - 1);
    }

    [Fact]
    public async Task ThePredicateRunsOncePerRowGroupNotOncePerRow()
    {
        byte[] bytes = await WriteAsync();

        int calls = 0;
        await PrunedOrderParquet
            .From(new MemoryStream(bytes))
            .Where(meta =>
            {
                calls++;
                return meta.RowGroupIndex == 0;
            })
            .ToListAsync();

        calls.ShouldBe(GroupCount);
    }
}

/// <summary>
/// The projection layer under the generated predicate: what a zone map answers when statistics are
/// present, absent, or recorded in a physical type that cannot be mapped back losslessly.
/// </summary>
public sealed class ParquetColumnStatisticsTests
{
    [Fact]
    public void RangeHelpersAnswerTheInclusionQuestionsAPredicateAsks()
    {
        ParquetColumnStatistics<int> stats = ParquetColumnStatistics.FromRaw<int>(10, 20, 0, null);

        stats.HasMinMax.ShouldBeTrue();
        stats.Min.ShouldBe(10);
        stats.Max.ShouldBe(20);
        stats.IsKnownNonNull.ShouldBeTrue();

        stats.MayContain(15).ShouldBeTrue();
        stats.MayContain(9).ShouldBeFalse();
        stats.MayContain(21).ShouldBeFalse();

        stats.MayContainAtLeast(20).ShouldBeTrue();
        stats.MayContainAtLeast(21).ShouldBeFalse();
        stats.MayContainGreaterThan(20).ShouldBeFalse();

        stats.MayContainAtMost(10).ShouldBeTrue();
        stats.MayContainAtMost(9).ShouldBeFalse();
        stats.MayContainLessThan(10).ShouldBeFalse();

        stats.MayContainBetween(5, 12).ShouldBeTrue();
        stats.MayContainBetween(0, 9).ShouldBeFalse();
        stats.MayContainBetween(21, 30).ShouldBeFalse();

        stats.MayContainAny(1, 15).ShouldBeTrue();
        stats.MayContainAny(1, 2).ShouldBeFalse();
        stats.MayContainAny().ShouldBeFalse();
    }

    [Fact]
    public void MissingStatisticsMakeEveryHelperAnswerYes()
    {
        ParquetColumnStatistics<int> stats = ParquetColumnStatistics.FromRaw<int>(
            null,
            null,
            null,
            null
        );

        stats.HasMinMax.ShouldBeFalse();
        stats.MayContain(15).ShouldBeTrue();
        stats.MayContainAtLeast(int.MaxValue).ShouldBeTrue();
        stats.MayContainAtMost(int.MinValue).ShouldBeTrue();
        stats.MayContainBetween(1, 2).ShouldBeTrue();
        stats.MayContainAny(7).ShouldBeTrue();
    }

    [Fact]
    public void PhysicalInt32StatisticsProjectOntoNarrowerClrTypes()
    {
        // Parquet stores a short column as INT32; the projection has to widen back down.
        ParquetColumnStatistics<short> stats = ParquetColumnStatistics.FromRaw<short>(
            -5,
            1_000,
            0,
            null
        );

        stats.HasMinMax.ShouldBeTrue();
        stats.Min.ShouldBe((short)-5);
        stats.Max.ShouldBe((short)1_000);
    }

    [Fact]
    public void AValueThatCannotBeProjectedDisablesPruningRatherThanGuessing()
    {
        // An INT32 statistic that overflows the property's own type must not silently truncate.
        ParquetColumnStatistics<short> overflowed = ParquetColumnStatistics.FromRaw<short>(
            0,
            int.MaxValue,
            0,
            null
        );

        overflowed.HasMinMax.ShouldBeFalse();
        overflowed.MayContainAtLeast(short.MaxValue).ShouldBeTrue();

        ParquetColumnStatistics<int> nonNumeric = ParquetColumnStatistics.FromRaw<int>(
            new object(),
            new object(),
            null,
            null
        );

        nonNumeric.HasMinMax.ShouldBeFalse();
        nonNumeric.MayContain(0).ShouldBeTrue();
    }

    [Fact]
    public void StringStatisticsCompareOrdinally()
    {
        ParquetColumnStatistics<string> stats = ParquetColumnStatistics.FromRaw<string>(
            "alpha",
            "omega",
            0,
            null
        );

        stats.MayContain("delta").ShouldBeTrue();
        stats.MayContain("zulu").ShouldBeFalse();
        stats.MayContain("Alpha").ShouldBeFalse();
    }

    [Fact]
    public void ZoneMapsCompareByValue()
    {
        ParquetColumnStatistics<int> a = ParquetColumnStatistics.FromRaw<int>(1, 2, 0, null);
        ParquetColumnStatistics<int> b = ParquetColumnStatistics.FromRaw<int>(1, 2, 0, null);
        ParquetColumnStatistics<int> c = ParquetColumnStatistics.FromRaw<int>(1, 3, 0, null);

        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        (a != c).ShouldBeTrue();
    }
}
