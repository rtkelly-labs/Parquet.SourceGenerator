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
/// Issue #151: point lookups and range slices prune row groups using the footer
/// <c>[Min, Max]</c> statistics.
/// <para>
/// The properties worth pinning are correctness first — a pruned read must return exactly
/// what a full scan returns, including on unsorted data where pruning has to switch itself
/// off — and only then the observable fact that row groups were skipped. The skip count is
/// asserted through <see cref="ParquetPruneStatistics"/> rather than wall-clock time, which
/// would make the suite flaky on a loaded runner.
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
        var pruning = new ParquetPruneStatistics();

        List<SortedEvent> found =
            await SortedEventParquetExtensions.ReadParquetBySequenceNumberAsync(
                Open(bytes),
                7_531,
                pruning
            );

        found.Count.ShouldBe(1);
        found[0].SequenceNumber.ShouldBe(7_531);
        pruning.SortedColumnDetected.ShouldBeTrue();
        pruning.StrictlyMonotonic.ShouldBeTrue();
        pruning.RowGroupCount.ShouldBe(100);
        pruning.RowGroupsScanned.ShouldBe(1);
        pruning.RowGroupsPruned.ShouldBe(99);
        pruning.FirstRowGroupRead.ShouldBe(75);
    }

    /// <summary>
    /// #264's combined surface, and the correctness floor for converging the two mechanisms
    /// onto one footer read: <c>SortedEvent</c> carries BOTH — three sort keys emit the
    /// binary-search path, the non-key eligible columns emit the predicate zone-map path —
    /// so one footer is currently read by two independent code paths. Before any merge, all
    /// three access paths (full scan, pushdown predicate, sorted lookup) must agree on the
    /// SAME bytes. The <c>SortedShipmentParquetExtensions</c> golden is this test's
    /// emitted-API twin; together they are what a convergence must keep green.
    /// </summary>
    [Fact]
    public async Task PredicatePushdownAndSortedLookupCoexistOnOneModel()
    {
        byte[] bytes = await WriteAsync(SortedRows(10_000), rowGroupSize: 100);

        List<SortedEvent> all = await SortedEventParquetExtensions.ReadParquetAsync(Open(bytes));
        all.Count.ShouldBe(10_000);

        // Predicate path (AcceptRowGroup / RowGroupMetadata): the pushdown result must equal
        // the client-side filter over the full scan — pruning may only remove provably-empty
        // groups, never a matching row.
        List<SortedEvent> viaPredicate = await SortedEventParquetExtensions.ReadParquetAsync(
            Open(bytes),
            predicate: meta => meta.SequenceNumber.MayContainAtLeast(9_500)
        );
        viaPredicate
            .Select(e => e.SequenceNumber)
            .ShouldBe(all.Where(e => e.SequenceNumber >= 9_500).Select(e => e.SequenceNumber));

        // Sorted-lookup path (TryPruneSortedRowGroups / ReadPrunedRangeAsync) on the same
        // key column and file.
        var pruning = new ParquetPruneStatistics();
        List<SortedEvent> viaLookup =
            await SortedEventParquetExtensions.ReadParquetBySequenceNumberAsync(
                Open(bytes),
                9_999,
                pruning
            );
        viaLookup.Count.ShouldBe(1);
        viaLookup[0].SequenceNumber.ShouldBe(9_999);

        // The two mechanisms must not interfere: the co-located predicate machinery cannot
        // silently disable sorted binary search, and vice versa.
        pruning.SortedColumnDetected.ShouldBeTrue();
        pruning.RowGroupsScanned.ShouldBe(1);

        // And the lookup's row is inside the predicate's surviving set — one key column, one
        // file, two pruning mechanisms, one consistent answer.
        viaPredicate.ShouldContain(viaLookup[0]);
    }

    [Fact]
    public async Task PointLookupOnAMissingKeyReadsNothingAndReturnsEmpty()
    {
        byte[] bytes = await WriteAsync(SortedRows(1_000), rowGroupSize: 100);
        var pruning = new ParquetPruneStatistics();

        List<SortedEvent> found =
            await SortedEventParquetExtensions.ReadParquetBySequenceNumberAsync(
                Open(bytes),
                50_000,
                pruning
            );

        found.ShouldBeEmpty();
        pruning.SortedColumnDetected.ShouldBeTrue();
        pruning.RowGroupsScanned.ShouldBe(0);
        pruning.FirstRowGroupRead.ShouldBe(-1);
    }

    [Fact]
    public async Task RangeSliceReadsOnlyTheOverlappingRowGroups()
    {
        byte[] bytes = await WriteAsync(SortedRows(10_000), rowGroupSize: 100);
        var pruning = new ParquetPruneStatistics();

        List<SortedEvent> slice =
            await SortedEventParquetExtensions.ReadParquetSequenceNumberRangeAsync(
                Open(bytes),
                2_050,
                2_349,
                pruning
            );

        slice.Count.ShouldBe(300);
        slice[0].SequenceNumber.ShouldBe(2_050);
        slice[^1].SequenceNumber.ShouldBe(2_349);
        pruning.SortedColumnDetected.ShouldBeTrue();
        // Groups 20..23 overlap [2050, 2349]; the other 96 never open.
        pruning.RowGroupsScanned.ShouldBe(4);
        pruning.FirstRowGroupRead.ShouldBe(20);
        pruning.LastRowGroupRead.ShouldBe(23);
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

        List<SortedEvent> actual =
            await SortedEventParquetExtensions.ReadParquetSequenceNumberRangeAsync(
                Open(bytes),
                1_234,
                3_210
            );

        actual.Count.ShouldBe(expected.Count);
        actual.Select(r => r.SequenceNumber).ShouldBe(expected.Select(r => r.SequenceNumber));
        actual.Select(r => r.Payload).ShouldBe(expected.Select(r => r.Payload));
    }

    [Fact]
    public async Task DateTimeKeyPrunesTheSameWayAsAnIntegerKey()
    {
        byte[] bytes = await WriteAsync(SortedRows(2_000), rowGroupSize: 100);
        var pruning = new ParquetPruneStatistics();

        List<SortedEvent> slice = await SortedEventParquetExtensions.ReadParquetTimestampRangeAsync(
            Open(bytes),
            Epoch.AddSeconds(1_500),
            Epoch.AddSeconds(1_599),
            pruning
        );

        slice.Count.ShouldBe(100);
        pruning.SortedColumnDetected.ShouldBeTrue();
        pruning.RowGroupsScanned.ShouldBe(1);
    }

    [Fact]
    public async Task UnsortedKeyFallsBackToAFullScanAndStillAnswersCorrectly()
    {
        // Bucket cycles 0..6 inside every row group, so every row group's [Min, Max] is [0, 6]:
        // maximally overlapping, and nothing can be pruned.
        List<SortedEvent> rows = SortedRows(1_000);
        byte[] bytes = await WriteAsync(rows, rowGroupSize: 100);
        var pruning = new ParquetPruneStatistics();

        List<SortedEvent> found = await SortedEventParquetExtensions.ReadParquetByBucketAsync(
            Open(bytes),
            3,
            pruning
        );

        pruning.SortedColumnDetected.ShouldBeFalse();
        pruning.RowGroupCount.ShouldBe(10);
        pruning.RowGroupsScanned.ShouldBe(10);
        found.Count.ShouldBe(rows.Count(r => r.Bucket == 3));
        found.ShouldAllBe(r => r.Bucket == 3);
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
        var pruning = new ParquetPruneStatistics();

        List<SortedEvent> found =
            await SortedEventParquetExtensions.ReadParquetBySequenceNumberAsync(
                Open(bytes),
                42,
                pruning
            );

        found.Count.ShouldBe(200);
        pruning.SortedColumnDetected.ShouldBeTrue();
        pruning.StrictlyMonotonic.ShouldBeFalse();
        pruning.RowGroupsScanned.ShouldBe(2);
    }

    [Fact]
    public async Task SingleRowGroupFileIsHandledWithoutPruningAnything()
    {
        byte[] bytes = await WriteAsync(SortedRows(50), rowGroupSize: 1_000);
        var pruning = new ParquetPruneStatistics();

        List<SortedEvent> found =
            await SortedEventParquetExtensions.ReadParquetBySequenceNumberAsync(
                Open(bytes),
                17,
                pruning
            );

        found.Count.ShouldBe(1);
        pruning.RowGroupCount.ShouldBe(1);
        pruning.RowGroupsScanned.ShouldBe(1);
    }

    [Fact]
    public async Task InvertedRangeReturnsEmptyWithoutOpeningTheFile()
    {
        byte[] bytes = await WriteAsync(SortedRows(500), rowGroupSize: 100);

        List<SortedEvent> found =
            await SortedEventParquetExtensions.ReadParquetSequenceNumberRangeAsync(
                Open(bytes),
                400,
                100
            );

        found.ShouldBeEmpty();
    }

    [Fact]
    public async Task NullStreamThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            SortedEventParquetExtensions.ReadParquetBySequenceNumberAsync((Stream)null!, 1)
        );
    }

    /// <summary>
    /// The opt-in guarantee, asserted against the real generator output rather than the
    /// emitter: a model with no <c>[ParquetSortKey]</c> gains no public API at all — not the
    /// lookups, not the range overloads, and not the shared pruning core they forward into.
    /// </summary>
    [Fact]
    public void UnmarkedModelGetsNoneOfTheLookupApi()
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

        // The ordinary read API is untouched, so the absence above is opt-in and not a
        // generator that simply failed to run for this model.
        emitted.ShouldContain("ReadParquetAsync");
    }

    /// <summary>
    /// The per-column half of the same guarantee: <c>Payload</c> shares a model with three
    /// marked columns, and still gets nothing.
    /// </summary>
    [Fact]
    public void UnmarkedColumnGetsNoLookupOverloads()
    {
        string[] emitted = typeof(SortedEventParquetExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.Name)
            .ToArray();

        emitted.ShouldNotContain("ReadParquetByPayloadAsync");
        emitted.ShouldNotContain("ReadParquetPayloadRangeAsync");
        emitted.ShouldContain("ReadParquetBySequenceNumberAsync");
    }
}
