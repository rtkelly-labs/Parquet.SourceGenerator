using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SampleDomain.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.AbiMatrix;

/// <summary>
/// ABI Execution verification for frozen v1.0 generated code (issue #289, docs/27 §2.1).
///
/// Verifies that historical, pre-generated OrderEventParquetExtensions compiled into a consumer
/// project continues to execute cleanly against the runtime Parquet.Net package without
/// MissingMethodException, TypeLoadException, or ABI signature drift.
/// </summary>
public sealed class OrderEventAbiExecutionTests
{
    private static List<OrderEvent> CreateSampleEvents(int count)
    {
        var baseDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        return Enumerable
            .Range(1, count)
            .Select(i => new OrderEvent
            {
                Id = i,
                Name = i % 2 == 0 ? null : $"Order_{i}",
                Score = i * 1.5,
                Price = i * 19.99m,
                CreatedAt = baseDate.AddHours(i),
                Duration = TimeSpan.FromMinutes(i * 5),
                CorrelationId = Guid.NewGuid(),
                OptionalGuid = i % 3 == 0 ? null : Guid.NewGuid(),
                Payload = i % 4 == 0 ? null : new byte[] { (byte)i, 0xFE, 0xED },
            })
            .ToList();
    }

    [Fact]
    public async Task FrozenOrderEventCanRoundtripSequentially()
    {
        var expected = CreateSampleEvents(25);
        using var ms = new MemoryStream();
        await OrderEventParquetExtensions.WriteParquetAsync(expected, ms);
        ms.Position = 0;

        List<OrderEvent> actual = await OrderEventParquetExtensions.ReadParquetAsync(ms);
        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Id.ShouldBe(expected[i].Id);
            actual[i].Name.ShouldBe(expected[i].Name);
            actual[i].Score.ShouldBe(expected[i].Score);
            actual[i].Price.ShouldBe(expected[i].Price);
            actual[i].CreatedAt.ShouldBe(expected[i].CreatedAt);
            actual[i].Duration.ShouldBe(expected[i].Duration);
            actual[i].CorrelationId.ShouldBe(expected[i].CorrelationId);
            actual[i].OptionalGuid.ShouldBe(expected[i].OptionalGuid);
            actual[i].Payload.ShouldBe(expected[i].Payload);
        }
    }

    [Fact]
    public async Task FrozenOrderEventCanRoundtripInParallel()
    {
        var expected = CreateSampleEvents(40);
        using var ms = new MemoryStream();
        await expected.WriteParquetBatchedAsync(
            ms,
            new Parquet.SourceGenerator.ParquetSerializerOptions { RowGroupSize = 10 }
        );
        ms.Position = 0;

        OrderEvent[] actual = await OrderEventParquetExtensions.ReadParquetParallelArrayAsync(ms);
        actual.Length.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Id.ShouldBe(expected[i].Id);
            actual[i].CorrelationId.ShouldBe(expected[i].CorrelationId);
        }
    }

    [Fact]
    public async Task FrozenOrderEventCanStream()
    {
        var expected = CreateSampleEvents(15);
        using var ms = new MemoryStream();
        await OrderEventParquetExtensions.WriteParquetAsync(expected, ms);
        ms.Position = 0;

        var actual = new List<OrderEvent>();
        await foreach (OrderEvent evt in OrderEventParquetExtensions.ReadParquetStreamAsync(ms))
        {
            actual.Add(evt);
        }

        actual.Count.ShouldBe(expected.Count);
        actual[0].Id.ShouldBe(expected[0].Id);
        actual[14].Id.ShouldBe(expected[14].Id);
    }

    [Fact]
    public async Task FrozenOrderEventCanReadFromMemory()
    {
        var expected = CreateSampleEvents(10);
        using var ms = new MemoryStream();
        await OrderEventParquetExtensions.WriteParquetAsync(expected, ms);
        byte[] bytes = ms.ToArray();

        List<OrderEvent> actual = await OrderEventParquetExtensions.ReadParquetAsync(bytes);
        actual.Count.ShouldBe(expected.Count);
        actual[0].Name.ShouldBe(expected[0].Name);
    }
}
