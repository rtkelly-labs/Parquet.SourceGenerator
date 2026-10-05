using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// A time-series shaped row: monotonically increasing <c>SequenceNumber</c> and
/// <c>Timestamp</c>, an unsorted <c>Bucket</c>, and a payload column so a row group is
/// worth skipping.
/// <para>
/// Three columns carry <c>[ParquetSortKey]</c> and so get lookup overloads; <c>Payload</c>
/// does not, and is the control for
/// <see cref="SortedRowGroupPruningTests.UnmarkedColumnGetsNoLookupOverloads"/>. <c>Bucket</c>
/// is marked but is not actually written in sorted order — the marker asks for the lookup, it
/// does not promise the data is sorted, and the read has to notice that at runtime.
/// </para>
/// </summary>
[ParquetSerializable]
public partial record SortedEvent
{
    [ParquetColumn("sequence_number")]
    [ParquetSortKey]
    public long SequenceNumber { get; init; }

    [ParquetColumn("timestamp")]
    [ParquetSortKey]
    public DateTime Timestamp { get; init; }

    [ParquetColumn("bucket")]
    [ParquetSortKey]
    public int Bucket { get; init; }

    [ParquetColumn("payload")]
    public string Payload { get; init; } = string.Empty;
}

/// <summary>
/// The same shape with no <c>[ParquetSortKey]</c> anywhere: the opt-out case, whose generated
/// class must carry none of the pruning API.
/// </summary>
[ParquetSerializable]
public partial record UnmarkedEvent
{
    [ParquetColumn("sequence_number")]
    public long SequenceNumber { get; init; }

    [ParquetColumn("payload")]
    public string Payload { get; init; } = string.Empty;
}

/// <summary>
/// Point lookups and range slices prune row groups using the footer <c>[Min, Max]</c> statistics
/// via the modern <c>.Where(...)</c> reader API (issue #584, #151).
/// <para>
/// The properties worth pinning are correctness first — a pruned read must return exactly
/// what a full scan returns, including on unsorted data where pruning has to switch itself
/// off — and only then the observable fact that row groups were skipped.
/// </para>
/// </summary>
public sealed class SortedRowGroupPruningTests
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<SortedEvent> SortedRows(int count) =>
        Enumerable
            .Range(0, count)
            .Select(i => new SortedEvent
            {
                SequenceNumber = i,
                Timestamp = Epoch.AddSeconds(i),
                Bucket = i % 7,
                Payload = $"payload-{i}",
            })
            .ToList();

    private static async Task<byte[]> WriteAsync(List<SortedEvent> rows, int rowGroupSize)
    {
        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
        );
        return stream.ToArray();
    }

    private static MemoryStream Open(byte[] bytes) => new(bytes, writable: false);

    [Fact]
    public async Task PointLookupOnSortedKeyReadsExactlyOneRowGroup()
    {
        byte[] bytes = await WriteAsync(SortedRows(10_000), rowGroupSize: 100);
        var scanned = new List<int>();

        SortedEvent[] found = await SortedEventParquet
            .From(Open(bytes))
            .Where(m =>
            {
                bool match = m.SequenceNumber.MayContain(7_531);
                if (match)
                {
                    scanned.Add(m.RowGroupIndex);
                }
                return match;
            })
            .ToArrayAsync();

        scanned.ShouldBe([75]);
        found.Length.ShouldBe(100);
        SortedEvent record = found.Single(r => r.SequenceNumber == 7_531);
        record.SequenceNumber.ShouldBe(7_531);
    }

    /// <summary>
    /// Combined surface: pushdown predicates and point lookups through <c>Where</c> agree on the
    /// same file. The pushdown result matches client-side filtering over a full scan, and point
    /// lookup rows sit inside the surviving range.
    /// </summary>
    [Fact]
    public async Task PredicatePushdownAndPointLookupCoexistOnOneModel()
    {
        byte[] bytes = await WriteAsync(SortedRows(10_000), rowGroupSize: 100);

        SortedEvent[] all = await SortedEventParquet.From(Open(bytes)).ToArrayAsync();
        all.Length.ShouldBe(10_000);

        // Predicate path (AcceptRowGroup / RowGroupMetadata): the pushdown result must equal
        // the client-side filter over the full scan — pruning may only remove provably-empty
        // groups, never a matching row.
        SortedEvent[] viaPredicate = await SortedEventParquet
            .From(Open(bytes))
            .Where(meta => meta.SequenceNumber.MayContainAtLeast(9_500))
            .ToArrayAsync();
        viaPredicate
            .Select(e => e.SequenceNumber)
            .ShouldBe(all.Where(e => e.SequenceNumber >= 9_500).Select(e => e.SequenceNumber));

        // Point lookup via Where on the same model.
        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(meta => meta.SequenceNumber.MayContain(9_999))
            .ToArrayAsync();
        viaWhere.Length.ShouldBe(100);
        SortedEvent target = viaWhere.Single(e => e.SequenceNumber == 9_999);
        target.SequenceNumber.ShouldBe(9_999);

        // And the point lookup's row is inside the range predicate's surviving set.
        viaPredicate.ShouldContain(target);
    }

    [Fact]
    public async Task PointLookupOnAMissingKeyReadsNothingAndReturnsEmpty()
    {
        byte[] bytes = await WriteAsync(SortedRows(1_000), rowGroupSize: 100);
        var scanned = new List<int>();

        SortedEvent[] found = await SortedEventParquet
            .From(Open(bytes))
            .Where(m =>
            {
                bool match = m.SequenceNumber.MayContain(50_000);
                if (match)
                {
                    scanned.Add(m.RowGroupIndex);
                }
                return match;
            })
            .ToArrayAsync();

        found.ShouldBeEmpty();
        scanned.ShouldBeEmpty();
    }

    [Fact]
    public async Task RangeSliceReadsOnlyTheOverlappingRowGroups()
    {
        byte[] bytes = await WriteAsync(SortedRows(10_000), rowGroupSize: 100);
        var scanned = new List<int>();

        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(m =>
            {
                bool match = m.SequenceNumber.MayContainBetween(2_050, 2_349);
                if (match)
                {
                    scanned.Add(m.RowGroupIndex);
                }
                return match;
            })
            .ToArrayAsync();

        // Groups 20..23 overlap [2050, 2349]; the other 96 never open.
        scanned.ShouldBe([20, 21, 22, 23]);
        List<SortedEvent> inRange = viaWhere
            .Where(r => r.SequenceNumber >= 2_050 && r.SequenceNumber <= 2_349)
            .ToList();
        inRange.Count.ShouldBe(300);
        inRange[0].SequenceNumber.ShouldBe(2_050);
        inRange[^1].SequenceNumber.ShouldBe(2_349);
    }

    [Fact]
    public async Task RangeSliceMatchesAFullScanFilterExactly()
    {
        List<SortedEvent> rows = SortedRows(5_000);
        byte[] bytes = await WriteAsync(rows, rowGroupSize: 250);

        List<SortedEvent> expected = rows.Where(r =>
                r.SequenceNumber >= 1_234 && r.SequenceNumber <= 3_210
            )
            .ToList();

        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(m => m.SequenceNumber.MayContainBetween(1_234, 3_210))
            .ToArrayAsync();

        List<SortedEvent> filteredWhere = viaWhere
            .Where(r => r.SequenceNumber >= 1_234 && r.SequenceNumber <= 3_210)
            .ToList();
        filteredWhere.Count.ShouldBe(expected.Count);
        filteredWhere
            .Select(r => r.SequenceNumber)
            .ShouldBe(expected.Select(r => r.SequenceNumber));
        filteredWhere.Select(r => r.Payload).ShouldBe(expected.Select(r => r.Payload));
    }

    [Fact]
    public async Task OpenEndedRangePrunesPrecedingRowGroups()
    {
        byte[] bytes = await WriteAsync(SortedRows(2_000), rowGroupSize: 100);
        var scanned = new List<int>();

        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(m =>
            {
                bool match = m.SequenceNumber.MayContainAtLeast(1_500);
                if (match)
                {
                    scanned.Add(m.RowGroupIndex);
                }
                return match;
            })
            .ToArrayAsync();

        // 2,000 rows across 20 row groups (0..19). Groups 0..14 hold [0..1499] and are pruned.
        scanned.ShouldBe([15, 16, 17, 18, 19]);
        viaWhere.Length.ShouldBe(500);
        viaWhere.All(r => r.SequenceNumber >= 1_500).ShouldBeTrue();
    }

    [Fact]
    public async Task UnsortedKeyFallsBackToAFullScanAndStillAnswersCorrectly()
    {
        // Bucket cycles 0..6 inside every row group, so every row group's [Min, Max] is [0, 6]:
        // maximally overlapping, and nothing can be pruned.
        List<SortedEvent> rows = SortedRows(1_000);
        byte[] bytes = await WriteAsync(rows, rowGroupSize: 100);

        var scanned = new List<int>();
        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(m =>
            {
                bool match = m.Bucket.MayContain(3);
                if (match)
                {
                    scanned.Add(m.RowGroupIndex);
                }
                return match;
            })
            .ToArrayAsync();

        scanned.Count.ShouldBe(10);
        viaWhere.Length.ShouldBe(1_000);
        viaWhere.Count(r => r.Bucket == 3).ShouldBe(rows.Count(r => r.Bucket == 3));
    }

    [Fact]
    public async Task DuplicateKeysStraddlingARowGroupBoundaryAreAllReturned()
    {
        // Two row groups of 100 rows each carry the same key 42, so [Min, Max] touch at the
        // boundary: sorted but not strictly monotonic, and both groups must be read.
        var rows = new List<SortedEvent>();
        for (int i = 0; i < 200; i++)
        {
            rows.Add(
                new SortedEvent
                {
                    SequenceNumber = 42,
                    Timestamp = Epoch,
                    Bucket = 0,
                    Payload = $"dup-{i}",
                }
            );
        }
        for (int i = 0; i < 100; i++)
        {
            rows.Add(
                new SortedEvent
                {
                    SequenceNumber = 100 + i,
                    Timestamp = Epoch.AddSeconds(1 + i),
                    Bucket = 0,
                    Payload = $"tail-{i}",
                }
            );
        }

        byte[] bytes = await WriteAsync(rows, rowGroupSize: 100);

        var scanned = new List<int>();
        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(m =>
            {
                bool match = m.SequenceNumber.MayContain(42);
                if (match)
                {
                    scanned.Add(m.RowGroupIndex);
                }
                return match;
            })
            .ToArrayAsync();

        scanned.ShouldBe([0, 1]);
        viaWhere.Count(r => r.SequenceNumber == 42).ShouldBe(200);
    }

    [Fact]
    public async Task SingleRowGroupFileIsHandledWithoutPruningAnything()
    {
        byte[] bytes = await WriteAsync(SortedRows(50), rowGroupSize: 1_000);

        var scanned = new List<int>();
        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(m =>
            {
                bool match = m.SequenceNumber.MayContain(17);
                if (match)
                {
                    scanned.Add(m.RowGroupIndex);
                }
                return match;
            })
            .ToArrayAsync();

        scanned.ShouldBe([0]);
        viaWhere.Count(r => r.SequenceNumber == 17).ShouldBe(1);
    }

    [Fact]
    public async Task InvertedRangeReturnsEmptyWithoutOpeningTheFile()
    {
        byte[] bytes = await WriteAsync(SortedRows(500), rowGroupSize: 100);

        SortedEvent[] viaWhere = await SortedEventParquet
            .From(Open(bytes))
            .Where(m => m.SequenceNumber.MayContainBetween(400, 100))
            .ToArrayAsync();

        viaWhere.ShouldBeEmpty();
    }

    [Fact]
    public void NullStreamThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => SortedEventParquet.From((Stream)null!));
    }

    /// <summary>
    /// The opt-in guarantee: unmarked model gains no lookup overloads.
    /// </summary>
    [Fact]
    public void UnmarkedModelGetsNoneOfTheLegacyLookupApi()
    {
        string[] emitted = typeof(UnmarkedEventParquetExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.Name)
            .ToArray();

        emitted.ShouldNotContain("ReadParquetBySequenceNumberAsync");
        emitted.ShouldNotContain("ReadParquetSequenceNumberRangeAsync");
        emitted.ShouldNotContain("ReadPrunedRangeAsync");
        emitted.ShouldNotContain("TryPruneSortedRowGroups");
        emitted.ShouldNotContain("TryCompareStatistics");
        emitted.ShouldNotContain("TryCompareStatisticToKey");

        // The ordinary read API is untouched.
        emitted.ShouldContain("ReadArrayCoreAsync");
    }

    /// <summary>
    /// Models with <c>[ParquetSortKey]</c> no longer emit legacy flat reads (#584).
    /// </summary>
    [Fact]
    public void SortedModelEmitsNoLookupOverloads()
    {
        string[] emitted = typeof(SortedEventParquetExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.Name)
            .ToArray();

        emitted.ShouldNotContain("ReadParquetByPayloadAsync");
        emitted.ShouldNotContain("ReadParquetPayloadRangeAsync");
        emitted.ShouldNotContain("ReadParquetBySequenceNumberAsync");
        emitted.ShouldNotContain("ReadParquetSequenceNumberRangeAsync");
        emitted.ShouldNotContain("ReadPrunedRangeAsync");
        emitted.ShouldNotContain("TryPruneSortedRowGroups");
    }
}
