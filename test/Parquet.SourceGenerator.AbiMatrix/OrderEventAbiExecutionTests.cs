using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SampleDomain.Models;
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
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.Equal(expected[i].Score, actual[i].Score);
            Assert.Equal(expected[i].Price, actual[i].Price);
            Assert.Equal(expected[i].CreatedAt, actual[i].CreatedAt);
            Assert.Equal(expected[i].Duration, actual[i].Duration);
            Assert.Equal(expected[i].CorrelationId, actual[i].CorrelationId);
            Assert.Equal(expected[i].OptionalGuid, actual[i].OptionalGuid);
            Assert.Equal(expected[i].Payload, actual[i].Payload);
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
        Assert.Equal(expected.Count, actual.Length);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.Equal(expected[i].CorrelationId, actual[i].CorrelationId);
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

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected[0].Id, actual[0].Id);
        Assert.Equal(expected[14].Id, actual[14].Id);
    }

    [Fact]
    public async Task FrozenOrderEventCanReadFromMemory()
    {
        var expected = CreateSampleEvents(10);
        using var ms = new MemoryStream();
        await OrderEventParquetExtensions.WriteParquetAsync(expected, ms);
        byte[] bytes = ms.ToArray();

        List<OrderEvent> actual = await OrderEventParquetExtensions.ReadParquetAsync(bytes);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected[0].Name, actual[0].Name);
    }
}
