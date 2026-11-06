using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record EagerBufferModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("nullable_int")]
    public int? NullableInt { get; init; }

    [ParquetColumn("name")]
    public string? Name { get; init; }

    [ParquetColumn("price")]
    [ParquetDecimal(18, 4)]
    public decimal Price { get; init; }

    [ParquetColumn("nullable_double")]
    public double? NullableDouble { get; init; }

    [ParquetColumn("guid")]
    public Guid CorrelationId { get; init; }

    [ParquetColumn("payload")]
    public byte[]? Payload { get; init; }
}

public class EagerBufferReturnTests
{
    [Fact]
    public async Task EagerReturnRoundTripsAccurately()
    {
        var items = new List<EagerBufferModel>
        {
            new()
            {
                Id = 1,
                NullableInt = 100,
                Name = "Item 1",
                Price = 19.99m,
                NullableDouble = 1.234,
                CorrelationId = Guid.NewGuid(),
                Payload = new byte[] { 1, 2, 3 },
            },
            new()
            {
                Id = 2,
                NullableInt = null,
                Name = null,
                Price = 45.00m,
                NullableDouble = null,
                CorrelationId = Guid.NewGuid(),
                Payload = null,
            },
            new()
            {
                Id = 3,
                NullableInt = 300,
                Name = "Item 3",
                Price = 99.95m,
                NullableDouble = 5.678,
                CorrelationId = Guid.NewGuid(),
                Payload = new byte[] { 4, 5 },
            },
        };

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        ms.Position = 0;
        var readBack = await EagerBufferModelParquet.From(ms).ToListAsync();

        readBack.Count.ShouldBe(3);
        readBack[0].Id.ShouldBe(items[0].Id);
        readBack[0].NullableInt.ShouldBe(items[0].NullableInt);
        readBack[0].Name.ShouldBe(items[0].Name);
        readBack[0].Price.ShouldBe(items[0].Price);
        readBack[0].NullableDouble.ShouldBe(items[0].NullableDouble);
        readBack[0].CorrelationId.ShouldBe(items[0].CorrelationId);
        readBack[0].Payload.ShouldBe(items[0].Payload);

        readBack[1].NullableInt.ShouldBeNull();
        readBack[1].Name.ShouldBeNull();
        readBack[1].NullableDouble.ShouldBeNull();
        readBack[1].Payload.ShouldBeNull();
    }

    [Fact]
    public async Task CancellationDuringWriteHandlesCleanly()
    {
        var items = Enumerable
            .Range(0, 500)
            .Select(i => new EagerBufferModel
            {
                Id = i,
                NullableInt = i % 2 == 0 ? i : null,
                Name = $"Name_{i}",
                Price = (decimal)i * 1.5m,
                NullableDouble = i * 0.1,
                CorrelationId = Guid.NewGuid(),
                Payload = new byte[] { (byte)(i % 256) },
            })
            .ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancelled immediately

        using var ms = new MemoryStream();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await items.WriteParquetAsync(ms, cancellationToken: cts.Token);
        });
    }
}
