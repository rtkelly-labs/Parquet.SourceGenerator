using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record BufferModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// The <c>ReadOnlyMemory&lt;byte&gt;</c> entry point used to exist for <c>ReadParquetAsync</c>
/// alone, so choosing the array-backed or streaming reader meant wrapping the bytes by hand.
/// </summary>
public sealed class MemoryOverloadTests
{
    private static async Task<byte[]> WriteSampleAsync(int rowCount, int rowGroupSize)
    {
        List<BufferModel> rows = Enumerable
            .Range(1, rowCount)
            .Select(i => new BufferModel { Id = i, Name = $"Item_{i}" })
            .ToList();

        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
        );
        return stream.ToArray();
    }

    [Fact]
    public async Task SequentialReaderAcceptsABuffer()
    {
        byte[] bytes = await WriteSampleAsync(5, 2);

        List<BufferModel> read = await BufferModelParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .ToListAsync();

        read.Count.ShouldBe(5);
        read.Select(x => x.Id).ShouldBe(Enumerable.Range(1, 5));
    }

    [Fact]
    public async Task ArrayBackedReaderAcceptsABuffer()
    {
        byte[] bytes = await WriteSampleAsync(5, 2);

        List<BufferModel> read = await BufferModelParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .Parallel()
            .ToListAsync();

        read.Count.ShouldBe(5);
        read.Select(x => x.Id).ShouldBe(Enumerable.Range(1, 5));
        read[2].Name.ShouldBe("Item_3");
    }

    [Fact]
    public async Task StreamingReaderAcceptsABuffer()
    {
        byte[] bytes = await WriteSampleAsync(5, 2);

        var read = new List<BufferModel>();
        await foreach (
            BufferModel item in BufferModelParquet
                .From(new ReadOnlyMemory<byte>(bytes))
                .AsAsyncEnumerable()
        )
        {
            read.Add(item);
        }

        read.Count.ShouldBe(5);
        read.Select(x => x.Id).ShouldBe(Enumerable.Range(1, 5));
    }

    [Fact]
    public async Task StreamingReaderOverABufferSurvivesEarlyTermination()
    {
        // Breaking out disposes the iterator, which is what runs the `using` around the wrapped
        // stream. Returning the inner sequence directly instead of iterating it would leak here.
        byte[] bytes = await WriteSampleAsync(6, 2);

        var read = new List<BufferModel>();
        await foreach (
            BufferModel item in BufferModelParquet
                .From(new ReadOnlyMemory<byte>(bytes))
                .AsAsyncEnumerable()
        )
        {
            read.Add(item);
            if (read.Count == 3)
            {
                break;
            }
        }

        read.Count.ShouldBe(3);
    }

    [Fact]
    public async Task BufferSliceIsHonouredRatherThanTheWholeArray()
    {
        // A sliced ReadOnlyMemory is array-backed with a non-zero offset, which is the case
        // MemoryMarshal.TryGetArray exists to handle; reading the whole array would fail here.
        byte[] bytes = await WriteSampleAsync(4, 2);
        byte[] padded = new byte[bytes.Length + 8];
        bytes.CopyTo(padded, 4);

        List<BufferModel> read = await BufferModelParquet
            .From(new ReadOnlyMemory<byte>(padded, 4, bytes.Length))
            .ToListAsync();

        read.Count.ShouldBe(4);
        read.Select(x => x.Id).ShouldBe(Enumerable.Range(1, 4));
    }
}
