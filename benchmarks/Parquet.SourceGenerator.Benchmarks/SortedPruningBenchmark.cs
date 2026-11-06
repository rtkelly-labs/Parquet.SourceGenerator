using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Parquet.SourceGenerator.Benchmarks;

/// <summary>
/// An append-only event row sorted by <c>SequenceNumber</c>, the shape issue #151 targets.
/// </summary>
[ParquetSerializable]
public partial record SortedSeriesEvent
{
    [ParquetColumn("sequence_number")]
    [ParquetSortKey]
    public long SequenceNumber { get; init; }

    [ParquetColumn("timestamp")]
    [ParquetSortKey]
    public DateTime Timestamp { get; init; }

    [ParquetColumn("value")]
    public double Value { get; init; }

    [ParquetColumn("payload")]
    public string Payload { get; init; } = string.Empty;
}

/// <summary>
/// Issue #151: what row-group pruning is actually worth on a monotonically sorted key.
/// <para>
/// Every benchmark answers the same question against the same in-memory file, so the
/// comparison is like for like: locate one record, or one contiguous slice, by key. The
/// baseline reads the whole file and filters in LINQ, which is what a caller has to do
/// today; the pruned variants binary search the footer statistics first.
/// </para>
/// </summary>
[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "BenchmarkDotNet requires instance methods."
)]
public class SortedPruningBenchmark
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private byte[] _file = null!;

    /// <summary>Row groups in the file; row group size is fixed at 1,000 rows.</summary>
    [Params(64, 256, 1_024)]
    public int RowGroups { get; set; }

    private long _pointKey;
    private long _rangeStart;
    private long _rangeEnd;

    [GlobalSetup]
    public void Setup()
    {
        const int RowGroupSize = 1_000;
        int rows = RowGroups * RowGroupSize;

        List<SortedSeriesEvent> data = Enumerable
            .Range(0, rows)
            .Select(i => new SortedSeriesEvent
            {
                SequenceNumber = i,
                Timestamp = Epoch.AddSeconds(i),
                Value = i * 1.5,
                Payload =
                    "payload-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .ToList();

        using var stream = new MemoryStream();
        data.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = RowGroupSize }
            )
            .GetAwaiter()
            .GetResult();
        _file = stream.ToArray();

        // Deliberately near the end of the file: a linear scan pays for everything before it.
        _pointKey = rows - (RowGroupSize / 2);
        _rangeStart = _pointKey - 500;
        _rangeEnd = _pointKey + 499;
    }

    private MemoryStream Open() => new(_file, writable: false);

    /// <summary>
    /// What a caller has to write today: read every row group, then filter.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task<int> FullScanPointLookup()
    {
        List<SortedSeriesEvent> all = await SortedSeriesEventParquetExtensions.ReadParquetAsync(
            Open()
        );
        return all.Count(r => r.SequenceNumber == _pointKey);
    }

    [Benchmark]
    public async Task<int> PrunedPointLookup()
    {
        List<SortedSeriesEvent> found =
            await SortedSeriesEventParquetExtensions.ReadParquetBySequenceNumberAsync(
                Open(),
                _pointKey
            );
        return found.Count;
    }

    [Benchmark]
    public async Task<int> FullScanRangeSlice()
    {
        List<SortedSeriesEvent> all = await SortedSeriesEventParquetExtensions.ReadParquetAsync(
            Open()
        );
        return all.Count(r => r.SequenceNumber >= _rangeStart && r.SequenceNumber <= _rangeEnd);
    }

    [Benchmark]
    public async Task<int> PrunedRangeSlice()
    {
        List<SortedSeriesEvent> slice =
            await SortedSeriesEventParquetExtensions.ReadParquetSequenceNumberRangeAsync(
                Open(),
                _rangeStart,
                _rangeEnd
            );
        return slice.Count;
    }

    /// <summary>
    /// Isolates the metadata half of the work: how long the sortedness certification plus the
    /// two binary searches take, with no column data decompressed at all. This is the figure
    /// the "sub-millisecond record location" acceptance criterion is about.
    /// </summary>
    [Benchmark]
    public async Task<int> PruneMetadataOnly()
    {
        var pruning = new ParquetPruneStatistics();
        // An out-of-range key: pruning selects zero row groups, so nothing is decompressed and
        // the measurement is the footer search on its own.
        await SortedSeriesEventParquetExtensions.ReadParquetBySequenceNumberAsync(
            Open(),
            long.MaxValue,
            pruning
        );
        return pruning.RowGroupsPruned;
    }

    /// <summary>
    /// The floor every read pays: open the file and parse its footer, doing nothing else.
    /// Subtracting this from <see cref="PruneMetadataOnly"/> isolates the certification scan
    /// plus the two binary searches.
    /// </summary>
    [Benchmark]
    public async Task<int> OpenReaderOnly()
    {
        await using var reader = await Parquet.ParquetReader.CreateAsync(Open());
        return reader.RowGroupCount;
    }
}
