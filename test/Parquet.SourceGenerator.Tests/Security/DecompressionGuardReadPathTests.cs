using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Meta;
using Shouldly;
using Xunit;
using static Parquet.SourceGenerator.Tests.Security.DecompressionGuardTests;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// End-to-end pins for #359: a real file whose first page header is rewritten in a legal but
/// unusual Thrift encoding must still be bounded by the decompression limits on every read path.
/// </summary>
public sealed class DecompressionGuardReadPathTests
{
    private const int Oversize = 100_000_000;

    private static async Task<byte[]> WriteRowsAsync()
    {
        var items = Enumerable
            .Range(0, 200)
            .Select(i => new MultiRowGroupModel { Id = i, Name = "name_" + i })
            .ToList();
        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);
        return ms.ToArray();
    }

    private static byte[] DataPageHeaderStruct(PageHeader header)
    {
        DataPageHeader page =
            header.DataPageHeader
            ?? throw new InvalidOperationException("The first page is not a v1 data page.");
        return Concat(
            Short(1, 5, I32(page.NumValues)),
            Short(1, 5, I32((int)page.Encoding)),
            Short(1, 5, I32((int)page.DefinitionLevelEncoding)),
            Short(1, 5, I32((int)page.RepetitionLevelEncoding)),
            Stop
        );
    }

    /// <summary>
    /// The same page, written with compressed_page_size ahead of uncompressed_page_size (which
    /// needs the long form for the backwards id) and a crc field before the nested struct.
    /// </summary>
    private static byte[] Reordered(PageHeader header, int uncompressedSize) =>
        Concat(
            Short(1, 5, I32((int)header.Type)),
            Short(2, 5, I32(header.CompressedPageSize)),
            Long(2, 5, I32(uncompressedSize)),
            Long(4, 5, I32(0x1234)),
            Short(1, 12, DataPageHeaderStruct(header)),
            Stop
        );

    [Fact]
    public async Task ReorderedHeaderWithHonestSizesStillReadsAsync()
    {
        byte[] file = await PageHeaderSplicer.ReplaceFirstPageHeaderAsync(
            await WriteRowsAsync(),
            header => Reordered(header, header.UncompressedPageSize)
        );

        MultiRowGroupModel[] rows = await MultiRowGroupModelParquet
            .From(new MemoryStream(file, writable: false))
            .ToArrayAsync();

        rows.Length.ShouldBe(200);
        rows[199].Name.ShouldBe("name_199");
    }

    [Fact]
    public async Task ReorderedHeaderDeclaringAnOversizePageIsRejectedOnEveryReadPathAsync()
    {
        byte[] file = await PageHeaderSplicer.ReplaceFirstPageHeaderAsync(
            await WriteRowsAsync(),
            header => Reordered(header, Oversize)
        );

        var viaStream = await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new MemoryStream(file, writable: false)).ToArrayAsync()
        );
        viaStream.Message.ShouldContain("exceeding maximum allowed");

        var viaMemory = await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new ReadOnlyMemory<byte>(file)).ToArrayAsync()
        );
        viaMemory.Message.ShouldContain("exceeding maximum allowed");

        var viaParallel = await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new ReadOnlyMemory<byte>(file)).Parallel().ToArrayAsync()
        );
        viaParallel.Message.ShouldContain("exceeding maximum allowed");

        var viaStreaming = await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (
                var _ in MultiRowGroupModelParquet
                    .From(new MemoryStream(file, writable: false))
                    .AsAsyncEnumerable()
            ) { }
        });
        viaStreaming.Message.ShouldContain("exceeding maximum allowed");
    }

    [Fact]
    public async Task UnparseablePageHeaderIsRejectedRatherThanReadUnguardedAsync()
    {
        byte[] file = await PageHeaderSplicer.ReplaceFirstPageHeaderAsync(
            await WriteRowsAsync(),
            _ => Enumerable.Repeat((byte)0xFF, 24).ToArray()
        );

        var ex = await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new MemoryStream(file, writable: false)).ToArrayAsync()
        );
        ex.Message.ShouldContain("Parquet page header at offset");
    }
}
