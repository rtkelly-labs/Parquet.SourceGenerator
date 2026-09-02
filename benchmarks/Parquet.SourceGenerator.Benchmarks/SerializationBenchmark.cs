using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Parquet.Serialization;

namespace Parquet.SourceGenerator.Benchmarks;

[ParquetSerializable]
public partial record ScaleEvent
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("val_a")]
    public double ValA { get; init; }

    [ParquetColumn("val_b")]
    public long ValB { get; init; }

    [ParquetColumn("is_valid")]
    public bool IsValid { get; init; }
}

[ParquetSerializable]
public partial record GuidEvent
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("correlation_id")]
    public Guid CorrelationId { get; init; }

    [ParquetColumn("timestamp")]
    public DateTime Timestamp { get; init; }
}

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ScalingSerializationBenchmark
{
    private List<ScaleEvent> _data = null!;

    [Params(1_000, 10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = Enumerable.Range(0, Count)
            .Select(i => new ScaleEvent
            {
                Id = i,
                ValA = i * 3.14159,
                ValB = i * 1000L,
                IsValid = i % 2 == 0
            })
            .ToList();
    }

    /// <summary>
    /// v6 ParquetSerializer baseline — uses Dremel shredding + compiled Expression trees.
    /// NOT Native AOT compatible.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task ReflectionParquetSerializerV6Write()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(_data, stream);
    }

    /// <summary>
    /// Source generator — Native AOT compatible, zero reflection, ArrayPool buffers.
    /// </summary>
    [Benchmark]
    public async Task SourceGeneratorWriteAsync()
    {
        using var stream = new MemoryStream();
        await _data.WriteParquetAsync(stream);
    }

    /// <summary>
    /// Source generator batched streaming — 100M+ scale.
    /// </summary>
    [Benchmark]
    public async Task SourceGeneratorWriteBatchedAsync()
    {
        using var stream = new MemoryStream();
        await _data.WriteParquetBatchedAsync(stream, rowGroupSize: 20_000);
    }
}

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ScalingDeserializationBenchmark
{
    private byte[] _parquetBytes = null!;

    [Params(1_000, 10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var data = Enumerable.Range(0, Count)
            .Select(i => new ScaleEvent
            {
                Id = i,
                ValA = i * 3.14159,
                ValB = i * 1000L,
                IsValid = i % 2 == 0
            })
            .ToList();

        using var stream = new MemoryStream();
        data.WriteParquetBatchedAsync(stream, rowGroupSize: 20_000).GetAwaiter().GetResult();
        _parquetBytes = stream.ToArray();
    }

    /// <summary>
    /// v6 ParquetSerializer baseline deserializer — compiled Expression trees.
    /// NOT Native AOT compatible.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task<IList<ScaleEvent>> ReflectionParquetSerializerV6Read()
    {
        using var stream = new MemoryStream(_parquetBytes);
        var result = await ParquetSerializer.DeserializeAsync<ScaleEvent>(stream);
        return result.Data;
    }

    /// <summary>
    /// Source generator sequential deserializer — Native AOT compatible, ArrayPool buffers.
    /// </summary>
    [Benchmark]
    public async Task<List<ScaleEvent>> SourceGeneratorReadAsync()
    {
        using var stream = new MemoryStream(_parquetBytes);
        return await ScaleEventParquetExtensions.ReadParquetAsync(stream);
    }

    /// <summary>
    /// Source generator array-backed deserializer over a <c>Stream</c>. Reads row groups
    /// sequentially — a stream cannot be shared between readers — and materialises into a pre-sized
    /// array rather than a growing list.
    /// </summary>
    [Benchmark]
    public async Task<List<ScaleEvent>> SourceGeneratorReadParallelAsync()
    {
        using var stream = new MemoryStream(_parquetBytes);
        return await ScaleEventParquetExtensions.ReadParquetParallelAsync(stream);
    }

    /// <summary>
    /// Source generator sequential deserializer over a byte buffer — no stream wrapper allocation.
    /// </summary>
    [Benchmark]
    public async Task<List<ScaleEvent>> SourceGeneratorReadBufferAsync()
    {
        return await ScaleEventParquetExtensions.ReadParquetAsync(new ReadOnlyMemory<byte>(_parquetBytes));
    }

    /// <summary>
    /// Source generator genuinely parallel deserializer: one reader and one stream per worker over
    /// the same buffer, so decode parallelises rather than just materialisation.
    /// </summary>
    /// <remarks>
    /// This is the benchmark the regression gate exists for. Each worker builds its own stream over
    /// the buffer, so any change that makes that construction copy — rather than view — the bytes
    /// multiplies the file size by the worker count. That is invisible in a correctness test and
    /// obvious in the allocation column here.
    /// </remarks>
    [Benchmark]
    public async Task<List<ScaleEvent>> SourceGeneratorReadParallelBufferAsync()
    {
        return await ScaleEventParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(_parquetBytes),
            maxDegreeOfParallelism: 4);
    }

    /// <summary>
    /// Source generator streaming deserializer — IAsyncEnumerable O(1) memory footprint.
    /// </summary>
    [Benchmark]
    public async Task<int> SourceGeneratorReadStreamAsync()
    {
        using var stream = new MemoryStream(_parquetBytes);
        int count = 0;
        await foreach (var item in ScaleEventParquetExtensions.ReadParquetStreamAsync(stream))
        {
            count++;
        }
        return count;
    }
}

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class GuidInterchangeBenchmark
{
    private List<GuidEvent> _guidData = null!;

    [Params(1_000, 10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _guidData = Enumerable.Range(0, Count)
            .Select(i => new GuidEvent
            {
                Id = i,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow
            })
            .ToList();
    }

    [Benchmark(Baseline = true)]
    public async Task ReflectionParquetSerializerGuidWrite()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(_guidData, stream);
    }

    [Benchmark]
    public async Task SourceGeneratorGuidWriteAsync()
    {
        using var stream = new MemoryStream();
        await _guidData.WriteParquetAsync(stream);
    }
}

internal static class Program
{
    private static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
