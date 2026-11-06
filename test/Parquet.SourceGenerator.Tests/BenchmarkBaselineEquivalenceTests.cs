using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Serialization;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record BenchmarkScaleModel
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
public partial record BenchmarkGuidModel
{
    [ParquetColumn("Id")]
    public int Id { get; init; }

    [ParquetColumn("CorrelationId")]
    public Guid CorrelationId { get; init; }

    [ParquetColumn("Timestamp")]
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Guards that benchmark models and baselines produce identical, fully-populated data across both
/// the reflection-based serializer (<see cref="ParquetSerializer"/>) and the source generator.
/// Prevents silent column omission or schema mismatches from corrupting performance baselines.
/// </summary>
public sealed class BenchmarkBaselineEquivalenceTests
{
    [Fact]
    public async Task BenchmarkScaleModelRoundTripMatchesBetweenGeneratorAndReflectionBaseline()
    {
        int count = 100;
        List<BenchmarkScaleModel> original = Enumerable
            .Range(0, count)
            .Select(i => new BenchmarkScaleModel
            {
                Id = i,
                ValA = i * 3.14159,
                ValB = i * 1000L,
                IsValid = i % 2 == 0,
            })
            .ToList();

        using var stream = new MemoryStream();
        await original.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 20 }
        );
        byte[] bytes = stream.ToArray();

        // 1. Verify Source Generator deserializes every single field correctly
        using var sgStream = new MemoryStream(bytes);
        List<BenchmarkScaleModel> sgResult =
            await BenchmarkScaleModelParquetExtensions.ReadParquetAsync(sgStream);

        Assert.Equal(count, sgResult.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(original[i].Id, sgResult[i].Id);
            Assert.Equal(original[i].ValA, sgResult[i].ValA, precision: 5);
            Assert.Equal(original[i].ValB, sgResult[i].ValB);
            Assert.Equal(original[i].IsValid, sgResult[i].IsValid);
        }

        // 2. Guard the reflection baseline: ensure ParquetSerializer does NOT silently skip columns
        using var baselineStream = new MemoryStream(bytes);
        DeserializationResult<BenchmarkScaleModel> baselineResult =
            await ParquetSerializer.DeserializeAsync<BenchmarkScaleModel>(baselineStream);

        Assert.Equal(count, baselineResult.Data.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(original[i].Id, baselineResult.Data[i].Id);
            Assert.Equal(original[i].ValA, baselineResult.Data[i].ValA, precision: 5);
            Assert.Equal(original[i].ValB, baselineResult.Data[i].ValB);
            Assert.Equal(original[i].IsValid, baselineResult.Data[i].IsValid);
        }
    }

    [Fact]
    public async Task BenchmarkGuidModelRoundTripMatchesBetweenGeneratorAndReflectionBaseline()
    {
        int count = 50;
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        List<BenchmarkGuidModel> original = Enumerable
            .Range(0, count)
            .Select(i => new BenchmarkGuidModel
            {
                Id = i,
                CorrelationId = Guid.NewGuid(),
                Timestamp = baseTime.AddMinutes(i),
            })
            .ToList();

        using var stream = new MemoryStream();
        await original.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 10 }
        );
        byte[] bytes = stream.ToArray();

        // Source generator read
        using var sgStream = new MemoryStream(bytes);
        List<BenchmarkGuidModel> sgResult =
            await BenchmarkGuidModelParquetExtensions.ReadParquetAsync(sgStream);

        // Reflection baseline read
        using var baselineStream = new MemoryStream(bytes);
        DeserializationResult<BenchmarkGuidModel> baselineResult =
            await ParquetSerializer.DeserializeAsync<BenchmarkGuidModel>(baselineStream);

        Assert.Equal(count, sgResult.Count);
        Assert.Equal(count, baselineResult.Data.Count);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(original[i].Id, sgResult[i].Id);
            Assert.Equal(original[i].CorrelationId, sgResult[i].CorrelationId);
            Assert.Equal(original[i].Timestamp, sgResult[i].Timestamp);

            Assert.Equal(original[i].Id, baselineResult.Data[i].Id);
            Assert.Equal(original[i].CorrelationId, baselineResult.Data[i].CorrelationId);
            Assert.Equal(original[i].Timestamp, baselineResult.Data[i].Timestamp);
        }
    }

    [Fact]
    public async Task SourceGeneratorDeserializesEqualOrLessMemoryThanReflectionBaseline()
    {
        int count = 10_000;
        List<BenchmarkScaleModel> data = Enumerable
            .Range(0, count)
            .Select(i => new BenchmarkScaleModel
            {
                Id = i,
                ValA = i * 3.14159,
                ValB = i * 1000L,
                IsValid = i % 2 == 0,
            })
            .ToList();

        using var ms = new MemoryStream();
        await data.WriteParquetBatchedAsync(
            ms,
            new ParquetSerializerOptions { RowGroupSize = 2_000 }
        );
        byte[] bytes = ms.ToArray();

        // Warmup
        using (var s = new MemoryStream(bytes))
        {
            await ParquetSerializer.DeserializeAsync<BenchmarkScaleModel>(s);
            await BenchmarkScaleModelParquetExtensions.ReadParquetAsync(s);
        }

        // Measure reflection baseline
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        using (var s = new MemoryStream(bytes))
        {
            var res = await ParquetSerializer.DeserializeAsync<BenchmarkScaleModel>(s);
            Assert.Equal(count, res.Data.Count);
            Assert.Equal(count - 1, res.Data[count - 1].Id);
        }
        long allocBaseline = GC.GetAllocatedBytesForCurrentThread() - b0;

        // Measure source generator array read
        long b1 = GC.GetAllocatedBytesForCurrentThread();
        using (var s = new MemoryStream(bytes))
        {
            var res = await BenchmarkScaleModelParquetExtensions.ReadParquetArrayAsync(s);
            Assert.Equal(count, res.Length);
            Assert.Equal(count - 1, res[count - 1].Id);
        }
        long allocSGArray = GC.GetAllocatedBytesForCurrentThread() - b1;

        // Source generator array deserializer must allocate less than or equal to reflection baseline
        Assert.True(
            allocSGArray <= allocBaseline,
            $"Source generator allocated {allocSGArray:N0} bytes which exceeds reflection baseline {allocBaseline:N0} bytes"
        );
    }
}
