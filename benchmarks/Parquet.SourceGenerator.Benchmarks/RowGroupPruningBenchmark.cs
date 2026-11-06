using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Parquet.SourceGenerator.Benchmarks;

[ParquetSerializable]
public partial record PrunableOrder
{
    [ParquetColumn("order_key")]
    public long OrderKey { get; init; }

    [ParquetColumn("region")]
    public string Region { get; init; } = string.Empty;

    [ParquetColumn("quantity")]
    public int Quantity { get; init; }

    [ParquetColumn("amount")]
    public double Amount { get; init; }
}

/// <summary>
/// Row-group predicate pushdown (issue #149) against the status quo: read every row group, then
/// filter the materialized objects with LINQ.
/// </summary>
/// <remarks>
/// <para>
/// The file is a million rows in 100 row groups of 10,000, with <c>OrderKey</c> ascending, so each
/// group carries a disjoint zone map and a range predicate prunes an exactly known number of groups.
/// <see cref="Selectivity"/> is the percentage of row groups the predicate admits.
/// </para>
/// <para>
/// Both methods return the same rows. The baseline pays for decompressing and materializing every
/// row group; the pushdown variant pays only for the groups the footer statistics could not rule out.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[InProcess]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class RowGroupPruningBenchmark
{
    private const int RowGroupSize = 10_000;
    private const int RowGroupCount = 100;
    private const int TotalRows = RowGroupSize * RowGroupCount;

    private byte[] _parquet = null!;
    private long _threshold;

    /// <summary>Percentage of the file's row groups the predicate admits.</summary>
    [Params(1, 10, 50)]
    public int Selectivity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        List<PrunableOrder> rows = Enumerable
            .Range(0, TotalRows)
            .Select(i => new PrunableOrder
            {
                OrderKey = i,
                Region = $"region_{i % 16}",
                Quantity = i % 97,
                Amount = i * 1.5,
            })
            .ToList();

        using var stream = new MemoryStream();
        rows.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = RowGroupSize }
            )
            .GetAwaiter()
            .GetResult();
        _parquet = stream.ToArray();

        // Keep the tail `Selectivity`% of row groups; everything before it is prunable.
        _threshold = (long)TotalRows - ((long)TotalRows * Selectivity / 100);
    }

    /// <summary>Status quo: every row group is read and materialized, then LINQ throws most of it away.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> ReadEverythingThenFilter()
    {
        List<PrunableOrder> all = await PrunableOrderParquetExtensions.ReadParquetAsync(
            new MemoryStream(_parquet, writable: false)
        );
        return all.Count(o => o.OrderKey >= _threshold);
    }

    /// <summary>Pushdown: row groups whose zone map cannot match are never opened.</summary>
    [Benchmark]
    public async Task<int> PruneRowGroupsThenFilter()
    {
        long threshold = _threshold;
        List<PrunableOrder> candidates = await PrunableOrderParquetExtensions.ReadParquetAsync(
            new MemoryStream(_parquet, writable: false),
            predicate: meta => meta.OrderKey.MayContainAtLeast(threshold)
        );
        return candidates.Count(o => o.OrderKey >= threshold);
    }
}
