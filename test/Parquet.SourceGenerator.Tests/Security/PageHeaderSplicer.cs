using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Parquet.Meta;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Replaces the first page header of the first column chunk in a written file, then repairs the
/// footer so the chunk offsets and sizes still describe the file. Lets a test hand a real read
/// terminal a page header written in a legal but unusual Thrift encoding.
/// </summary>
internal static class PageHeaderSplicer
{
    private readonly record struct PageLocation(long Offset, PageHeader Header, int Length);

    public static async Task<byte[]> ReplaceFirstPageHeaderAsync(
        byte[] file,
        Func<PageHeader, byte[]> buildHeader
    )
    {
        PageLocation page = await LocateFirstPageAsync(file);
        byte[] replacement = buildHeader(page.Header);
        int delta = replacement.Length - page.Length;

        byte[] spliced = new byte[file.Length + delta];
        file.AsSpan(0, (int)page.Offset).CopyTo(spliced);
        replacement.CopyTo(spliced.AsSpan((int)page.Offset));
        file.AsSpan((int)page.Offset + page.Length)
            .CopyTo(spliced.AsSpan((int)page.Offset + replacement.Length));

        return await HostileParquetTests.RewriteColumnMetadataAsync(
            spliced,
            (metadata, _) =>
            {
                metadata.RowGroups[0].Columns[0].MetaData!.TotalCompressedSize += delta;
                foreach (RowGroup rowGroup in metadata.RowGroups)
                {
                    foreach (ColumnChunk chunk in rowGroup.Columns)
                    {
                        ColumnMetaData column = chunk.MetaData!;
                        if (column.DataPageOffset > page.Offset)
                        {
                            column.DataPageOffset += delta;
                        }

                        if (
                            column.DictionaryPageOffset is > 0
                            && column.DictionaryPageOffset > page.Offset
                        )
                        {
                            column.DictionaryPageOffset += delta;
                        }
                    }
                }
            }
        );
    }

    private static async Task<PageLocation> LocateFirstPageAsync(byte[] file)
    {
        using var input = new MemoryStream(file, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(input);
        ColumnMetaData column = reader.Metadata!.RowGroups[0].Columns[0].MetaData!;
        long offset = column.DictionaryPageOffset is > 0
            ? column.DictionaryPageOffset.Value
            : column.DataPageOffset;

        System.Type protoType = typeof(ParquetReader).Assembly.GetType(
            "Parquet.Meta.Proto.ThriftCompactProtocolReader",
            throwOnError: true
        )!;
        input.Position = offset;
        object proto = Activator.CreateInstance(
            protoType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [input],
            culture: null
        )!;
        var header = (PageHeader)
            typeof(PageHeader)
                .GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [proto])!;
        return new PageLocation(offset, header, (int)(input.Position - offset));
    }
}
