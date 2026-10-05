using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Schema;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The emitted writers return each column's pooled buffer to <see cref="ArrayPool{T}"/> as soon as
/// that column's <c>WriteAsync</c> / <c>WriteAllPartsAsync</c> completes, while the row-group
/// writer is still open (docs/architecture/memory-and-performance.md). That is correct only if Parquet.Net has fully consumed the
/// caller's memory by the time the task completes and keeps no reference to it until the row group
/// is disposed. Nothing in Parquet.Net documents that, so these tests pin it: a Parquet.Net upgrade
/// that deferred encoding would fail here instead of corrupting files under load (#399).
/// </summary>
public sealed class WriteCompletionContractTests
{
    private static readonly int[] Ids = [11, 22, 33, 44, 55];
    private static readonly int[] PackedValues = [7, 9, 13];
    private static readonly int[] Levels = [1, 0, 1, 0, 1];
    private static readonly int?[] ExpectedNullable = [7, null, 9, null, 13];

    private static readonly DataField<int> IdField = new("id");
    private static readonly DataField<int?> NullableField = new("maybe");

    [Fact]
    public async Task ColumnBytesAreOnTheStreamBeforeTheWriteTaskCompletesAsync()
    {
        using var stream = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(
            new ParquetSchema(IdField),
            stream
        );
        using ParquetRowGroupWriter group = writer.CreateRowGroup();
        int[] ids = (int[])Ids.Clone();
        long before = stream.Length;

        await group.WriteAsync<int>(IdField, new ReadOnlyMemory<int>(ids));

        // The chunk is encoded and handed to the stream, not parked until Dispose.
        stream.Length.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task OverwritingTheBufferAfterWriteAsyncCompletesDoesNotChangeTheFileAsync()
    {
        using var stream = new MemoryStream();
        ParquetSchema schema = new(IdField, NullableField);
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter group = writer.CreateRowGroup();
            int[] ids = (int[])Ids.Clone();
            int[] packed = (int[])PackedValues.Clone();

            await group.WriteAsync<int>(IdField, new ReadOnlyMemory<int>(ids));
            Array.Fill(ids, -1);

            await group.WriteAllPartsAsync<int>(
                NullableField,
                new ReadOnlyMemory<int>(packed),
                new ReadOnlyMemory<int>(Levels),
                null,
                cancellationToken: default
            );
            Array.Fill(packed, -1);
        }

        stream.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        using ParquetRowGroupReader groupReader = reader.OpenRowGroupReader(0);
        int[] ids2 = new int[Ids.Length];
        await groupReader.ReadAsync<int>(IdField, new Memory<int>(ids2));
        int?[] nullable = new int?[Ids.Length];
        await groupReader.ReadAsync<int>(NullableField, new Memory<int?>(nullable));

        ids2.ShouldBe(Ids);
        nullable.ShouldBe(ExpectedNullable);
    }

    [Fact]
    public async Task GeneratedWriterSurvivesConcurrentRentersPoisoningTheSharedPoolAsync()
    {
        List<EagerBufferModel> rows = Enumerable
            .Range(0, 200)
            .Select(i => new EagerBufferModel
            {
                Id = i,
                NullableInt = i % 3 == 0 ? null : i,
                Name = "n" + i,
                Price = i,
                NullableDouble = i % 5 == 0 ? null : i * 0.5,
                CorrelationId = Guid.NewGuid(),
                Payload = [(byte)i],
            })
            .ToList();
        using var stop = new System.Threading.CancellationTokenSource();

        // Other threads rent from the shared pools the writer uses, write garbage and return the
        // array: if the writer returned a buffer Parquet.Net still read from, that garbage lands in
        // the file.
        Task[] poisoners = Enumerable
            .Range(0, 4)
            .Select(_ =>
                Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        int[] ints = ArrayPool<int>.Shared.Rent(256);
                        Array.Fill(ints, -7);
                        ArrayPool<int>.Shared.Return(ints);
                        double[] doubles = ArrayPool<double>.Shared.Rent(256);
                        Array.Fill(doubles, -7.5);
                        ArrayPool<double>.Shared.Return(doubles);
                    }
                })
            )
            .ToArray();

        try
        {
            for (int round = 0; round < 40; round++)
            {
                using var stream = new MemoryStream();
                await rows.WriteParquetAsync(stream);
                stream.Position = 0;
                EagerBufferModel[] read = await EagerBufferModelParquet.From(stream).ToArrayAsync();

                read.Select(r => r.Id).ShouldBe(rows.Select(r => r.Id));
                read.Select(r => r.NullableInt).ShouldBe(rows.Select(r => r.NullableInt));
                read.Select(r => r.NullableDouble).ShouldBe(rows.Select(r => r.NullableDouble));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(poisoners);
        }
    }
}
