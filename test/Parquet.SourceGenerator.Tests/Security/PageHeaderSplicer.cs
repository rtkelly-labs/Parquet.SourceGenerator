using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Parquet.Meta;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Replaces a page header in the first column chunk of a written file, then repairs the footer so
/// the chunk offsets and sizes still describe the file. Lets a test hand a real read terminal a
/// page header written in a legal but unusual Thrift encoding.
/// </summary>
internal static class PageHeaderSplicer
{
    private readonly record struct PageLocation(long Offset, PageHeader Header, int Length);

    public static Task<byte[]> ReplaceFirstPageHeaderAsync(
        byte[] file,
        Func<PageHeader, byte[]> buildHeader
    ) => ReplaceAsync(file, columnIndex: 0, secondPage: false, buildHeader);

    /// <summary>
    /// Replaces the header of the page that follows the first page of a column's chunk: for a
    /// dictionary-encoded column, the data page after the dictionary page. Parquet.Net 4.x reaches
    /// that page by reading on from the previous one rather than by seeking to it.
    /// </summary>
    public static Task<byte[]> ReplaceSecondPageHeaderAsync(
        byte[] file,
        int columnIndex,
        Func<PageHeader, byte[]> buildHeader
    ) => ReplaceAsync(file, columnIndex, secondPage: true, buildHeader);

    private static async Task<byte[]> ReplaceAsync(
        byte[] file,
        int columnIndex,
        bool secondPage,
        Func<PageHeader, byte[]> buildHeader
    )
    {
        PageLocation page = await LocatePageAsync(file, columnIndex, secondPage);
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
                metadata.RowGroups[0].Columns[columnIndex].MetaData!.TotalCompressedSize += delta;
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

    private static async Task<PageLocation> LocatePageAsync(
        byte[] file,
        int columnIndex,
        bool secondPage
    )
    {
        using var input = new MemoryStream(file, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(input);
        ColumnMetaData column = reader.Metadata!.RowGroups[0].Columns[columnIndex].MetaData!;

        // Parquet.Net starts a dictionary-encoded chunk with the dictionary page, at
        // DictionaryPageOffset when it records one and at DataPageOffset when it does not.
        long offset = column.DictionaryPageOffset is > 0
            ? column.DictionaryPageOffset.Value
            : column.DataPageOffset;
        PageLocation first = ReadHeader(input, offset);
        if (!secondPage)
        {
            return first;
        }

        if (first.Header.Type != PageType.DICTIONARY_PAGE)
        {
            throw new InvalidOperationException(
                "The column does not start with a dictionary page."
            );
        }

        return ReadHeader(input, offset + first.Length + first.Header.CompressedPageSize);
    }

    private static PageLocation ReadHeader(Stream input, long offset)
    {
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
