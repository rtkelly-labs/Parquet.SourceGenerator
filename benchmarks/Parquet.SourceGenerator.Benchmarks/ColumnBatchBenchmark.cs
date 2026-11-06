using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Parquet.Serialization;

namespace Parquet.SourceGenerator.Benchmarks;

/// <summary>
/// The workload issue #147 exists for: an analytical aggregation over a couple of columns, where
/// the caller wants numbers out and never wants the row objects. Every benchmark here computes the
/// same two sums, so the only thing that varies is how the decoded column data reaches the loop.
/// </summary>
[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ColumnBatchAggregationBenchmark
{
    private byte[] _parquetBytes = null!;

    [Params(100_000, 1_000_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        List<ScaleEvent> data = Enumerable
            .Range(0, Count)
            .Select(i => new ScaleEvent
            {
                Id = i,
                ValA = i * 3.14159,
                ValB = i * 1000L,
                IsValid = i % 2 == 0,
            })
            .ToList();

        using var stream = new MemoryStream();
        data.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = 20_000 }
            )
            .GetAwaiter()
            .GetResult();
        _parquetBytes = stream.ToArray();
    }

    /// <summary>Reflection baseline: deserialize every row, then aggregate.</summary>
    [Benchmark(Baseline = true)]
    public async Task<double> ReflectionPocoAggregate()
    {
        using var stream = new MemoryStream(_parquetBytes);
        var result = await ParquetSerializer.DeserializeAsync<ScaleEvent>(stream);
        double sum = 0;
        long sumB = 0;
        foreach (ScaleEvent row in result.Data)
        {
            sum += row.ValA;
            sumB += row.ValB;
        }
        return sum + sumB;
    }

    /// <summary>Generated bulk reader: materialize the whole list, then aggregate.</summary>
    [Benchmark]
    public async Task<double> GeneratedPocoListAggregate()
    {
        using var stream = new MemoryStream(_parquetBytes);
        List<ScaleEvent> rows = await ScaleEventParquetExtensions.ReadParquetAsync(stream);
        double sum = 0;
        long sumB = 0;
        foreach (ScaleEvent row in rows)
        {
            sum += row.ValA;
            sumB += row.ValB;
        }
        return sum + sumB;
    }

    /// <summary>
    /// Generated streaming reader: one object at a time, so no list, but still one object per row.
    /// </summary>
    [Benchmark]
    public async Task<double> GeneratedPocoStreamAggregate()
    {
        using var stream = new MemoryStream(_parquetBytes);
        double sum = 0;
        long sumB = 0;
        await foreach (ScaleEvent row in ScaleEventParquetExtensions.ReadParquetStreamAsync(stream))
        {
            sum += row.ValA;
            sumB += row.ValB;
        }
        return sum + sumB;
    }

    /// <summary>Struct-of-arrays batches (#147): scalar loop over the column spans, no objects.</summary>
    [Benchmark]
    public async Task<double> ColumnBatchScalarAggregate()
    {
        using var stream = new MemoryStream(_parquetBytes);
        double sum = 0;
        long sumB = 0;
        await foreach (
            ScaleEventParquetExtensions.ColumnBatch batch in ScaleEventParquetExtensions.ReadParquetBatchesAsync(
                stream
            )
        )
        {
            ReadOnlySpan<double> a = batch.ValASpan;
            ReadOnlySpan<long> b = batch.ValBSpan;
            for (int i = 0; i < a.Length; i++)
            {
                sum += a[i];
                sumB += b[i];
            }
        }
        return sum + sumB;
    }

    /// <summary>
    /// The reason the spans are worth having: the same aggregation vectorized. This shape is simply
    /// unavailable to a row-at-a-time reader.
    /// </summary>
    [Benchmark]
    public async Task<double> ColumnBatchSimdAggregate()
    {
        using var stream = new MemoryStream(_parquetBytes);
        double sum = 0;
        long sumB = 0;
        await foreach (
            ScaleEventParquetExtensions.ColumnBatch batch in ScaleEventParquetExtensions.ReadParquetBatchesAsync(
                stream
            )
        )
        {
            sum += SumVectorized(batch.ValASpan);
            sumB += SumVectorized(batch.ValBSpan);
        }
        return sum + sumB;
    }

    private static double SumVectorized(ReadOnlySpan<double> values)
    {
        var acc = Vector<double>.Zero;
        int width = Vector<double>.Count;
        int i = 0;
        for (; i <= values.Length - width; i += width)
        {
            acc += new Vector<double>(values.Slice(i, width));
        }
        double total = Vector.Sum(acc);
        for (; i < values.Length; i++)
        {
            total += values[i];
        }
        return total;
    }

    private static long SumVectorized(ReadOnlySpan<long> values)
    {
        var acc = Vector<long>.Zero;
        int width = Vector<long>.Count;
        int i = 0;
        for (; i <= values.Length - width; i += width)
        {
            acc += new Vector<long>(values.Slice(i, width));
        }
        long total = Vector.Sum(acc);
        for (; i < values.Length; i++)
        {
            total += values[i];
        }
        return total;
    }
}
