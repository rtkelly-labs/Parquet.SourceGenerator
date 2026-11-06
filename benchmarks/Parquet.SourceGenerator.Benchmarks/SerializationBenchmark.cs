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
    [ParquetColumn("Id")]
    public int Id { get; init; }

    [ParquetColumn("ValA")]
    public double ValA { get; init; }

    [ParquetColumn("ValB")]
    public long ValB { get; init; }

    [ParquetColumn("IsValid")]
    public bool IsValid { get; init; }
}

[ParquetSerializable]
public partial record GuidEvent
{
    [ParquetColumn("Id")]
    public int Id { get; init; }

    [ParquetColumn("CorrelationId")]
    public Guid CorrelationId { get; init; }

    [ParquetColumn("Timestamp")]
    public DateTime Timestamp { get; init; }
}

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ScalingSerializationBenchmark
{
    private List<ScaleEvent> _data = null!;

    [ParamsSource(nameof(Counts))]
    public int Count { get; set; }

    public static IEnumerable<int> Counts =>
        BenchmarkParameterSource.GetCounts(1_000, 10_000, 100_000, 1_000_000);

    [GlobalSetup]
    public void Setup()
    {
        _data = Enumerable
            .Range(0, Count)
            .Select(i => new ScaleEvent
            {
                Id = i,
                ValA = i * 3.14159,
                ValB = i * 1000L,
                IsValid = i % 2 == 0,
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
        await _data.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 20_000 }
        );
    }
}

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ScalingDeserializationBenchmark
{
    private byte[] _parquetBytes = null!;

    [ParamsSource(nameof(Counts))]
    public int Count { get; set; }

    public static IEnumerable<int> Counts =>
        BenchmarkParameterSource.GetCounts(1_000, 10_000, 100_000, 1_000_000);

    [GlobalSetup]
    public void Setup()
    {
        var data = Enumerable
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

        // Guard: ensure the reflection baseline genuinely deserializes column data rather than skipping
        using var verifyStream = new MemoryStream(_parquetBytes);
        var baselineCheck = ParquetSerializer
            .DeserializeAsync<ScaleEvent>(verifyStream)
            .GetAwaiter()
            .GetResult();
        if (
            baselineCheck.Data.Count != Count
            || baselineCheck.Data[0].Id != 0
            || baselineCheck.Data[Count - 1].Id != Count - 1
        )
        {
            throw new InvalidOperationException(
                "Reflection baseline failed to deserialize column values."
            );
        }
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
        return await ScaleEventParquet.From(stream).ToListAsync();
    }

    /// <summary>
    /// Source generator sequential deserializer over a byte buffer — no stream wrapper allocation.
    /// </summary>
    [Benchmark]
    public async Task<List<ScaleEvent>> SourceGeneratorReadBufferAsync()
    {
        return await ScaleEventParquet.From(new ReadOnlyMemory<byte>(_parquetBytes)).ToListAsync();
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
        return await ScaleEventParquet
            .From(new ReadOnlyMemory<byte>(_parquetBytes))
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
            .Parallel()
            .ToListAsync();
    }

    /// <summary>
    /// Source generator sequential array deserializer over a stream — zero-copy native array.
    /// </summary>
    [Benchmark]
    public async Task<ScaleEvent[]> SourceGeneratorReadArrayAsync()
    {
        using var stream = new MemoryStream(_parquetBytes);
        return await ScaleEventParquet.From(stream).ToArrayAsync();
    }

    /// <summary>
    /// Source generator sequential array deserializer over a byte buffer — zero-copy native array.
    /// </summary>
    [Benchmark]
    public async Task<ScaleEvent[]> SourceGeneratorReadBufferArrayAsync()
    {
        return await ScaleEventParquet.From(new ReadOnlyMemory<byte>(_parquetBytes)).ToArrayAsync();
    }

    /// <summary>
    /// Source generator parallel array deserializer over a byte buffer — zero-copy native array.
    /// </summary>
    [Benchmark]
    public async Task<ScaleEvent[]> SourceGeneratorReadParallelBufferArrayAsync()
    {
        return await ScaleEventParquet
            .From(new ReadOnlyMemory<byte>(_parquetBytes))
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
            .Parallel()
            .ToArrayAsync();
    }

    /// <summary>
    /// Source generator streaming deserializer — IAsyncEnumerable O(1) memory footprint.
    /// </summary>
    [Benchmark]
    public async Task<int> SourceGeneratorReadStreamAsync()
    {
        using var stream = new MemoryStream(_parquetBytes);
        int count = 0;
        await foreach (var item in ScaleEventParquet.From(stream).AsAsyncEnumerable())
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

    [ParamsSource(nameof(Counts))]
    public int Count { get; set; }

    public static IEnumerable<int> Counts =>
        BenchmarkParameterSource.GetCounts(1_000, 10_000, 100_000);

    [GlobalSetup]
    public void Setup()
    {
        _guidData = Enumerable
            .Range(0, Count)
            .Select(i => new GuidEvent
            {
                Id = i,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
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
        List<string> benchmarkArgs = new(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--params", StringComparison.Ordinal))
            {
                if (++i >= args.Length)
                {
                    throw new ArgumentException(
                        "--params requires a value such as Count=1000,10000.",
                        nameof(args)
                    );
                }

                string parameter = args[i];
                const string countPrefix = "Count=";
                if (!parameter.StartsWith(countPrefix, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Only the Count benchmark parameter can be overridden.",
                        nameof(args)
                    );
                }

                Environment.SetEnvironmentVariable(
                    "PSG_BENCHMARK_COUNTS",
                    parameter[countPrefix.Length..]
                );
                continue;
            }

            benchmarkArgs.Add(args[i]);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(benchmarkArgs.ToArray());
    }
}
