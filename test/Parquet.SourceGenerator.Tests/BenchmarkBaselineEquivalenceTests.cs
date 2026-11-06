using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Serialization;
using Shouldly;
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
[Collection(AllocationMeasurementSuite.Name)]
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
        List<BenchmarkScaleModel> sgResult = await BenchmarkScaleModelParquet
            .From(sgStream)
            .ToListAsync();

        sgResult.Count.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            sgResult[i].Id.ShouldBe(original[i].Id);
            sgResult[i].ValA.ShouldBe(original[i].ValA, 0.000005);
            sgResult[i].ValB.ShouldBe(original[i].ValB);
            sgResult[i].IsValid.ShouldBe(original[i].IsValid);
        }

        // 2. Guard the reflection baseline: ensure ParquetSerializer does NOT silently skip columns
        using var baselineStream = new MemoryStream(bytes);
        DeserializationResult<BenchmarkScaleModel> baselineResult =
            await ParquetSerializer.DeserializeAsync<BenchmarkScaleModel>(baselineStream);

        baselineResult.Data.Count.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            baselineResult.Data[i].Id.ShouldBe(original[i].Id);
            baselineResult.Data[i].ValA.ShouldBe(original[i].ValA, 0.000005);
            baselineResult.Data[i].ValB.ShouldBe(original[i].ValB);
            baselineResult.Data[i].IsValid.ShouldBe(original[i].IsValid);
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
        List<BenchmarkGuidModel> sgResult = await BenchmarkGuidModelParquet
            .From(sgStream)
            .ToListAsync();

        // Reflection baseline read
        using var baselineStream = new MemoryStream(bytes);
        DeserializationResult<BenchmarkGuidModel> baselineResult =
            await ParquetSerializer.DeserializeAsync<BenchmarkGuidModel>(baselineStream);

        sgResult.Count.ShouldBe(count);
        baselineResult.Data.Count.ShouldBe(count);

        for (int i = 0; i < count; i++)
        {
            sgResult[i].Id.ShouldBe(original[i].Id);
            sgResult[i].CorrelationId.ShouldBe(original[i].CorrelationId);
            sgResult[i].Timestamp.ShouldBe(original[i].Timestamp);

            baselineResult.Data[i].Id.ShouldBe(original[i].Id);
            baselineResult.Data[i].CorrelationId.ShouldBe(original[i].CorrelationId);
            baselineResult.Data[i].Timestamp.ShouldBe(original[i].Timestamp);
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
            await BenchmarkScaleModelParquet.From(s).ToListAsync();
        }

        // Measure reflection baseline
        var baseline = await AllocationMeasurement.MeasureAsync(async () =>
        {
            using var s = new MemoryStream(bytes);
            return await ParquetSerializer.DeserializeAsync<BenchmarkScaleModel>(s);
        });
        var baselineResult = baseline.Result;
        baselineResult.Data.Count.ShouldBe(count);
        baselineResult.Data[count - 1].Id.ShouldBe(count - 1);
        long allocBaseline = baseline.AllocatedBytes;

        // Measure source generator array read
        AllocationMeasurementResult<BenchmarkScaleModel[]> generated =
            await AllocationMeasurement.MeasureAsync(async () =>
            {
                using var s = new MemoryStream(bytes);
                return await BenchmarkScaleModelParquet.From(s).ToArrayAsync();
            });
        BenchmarkScaleModel[] generatedResult = generated.Result;
        generatedResult.Length.ShouldBe(count);
        generatedResult[count - 1].Id.ShouldBe(count - 1);
        long allocSGArray = generated.AllocatedBytes;

        // Source generator array deserializer must allocate less than or equal to reflection baseline
        (allocSGArray <= allocBaseline).ShouldBeTrue(
            $"Source generator allocated {allocSGArray:N0} bytes which exceeds reflection baseline {allocBaseline:N0} bytes"
        );
    }
}
