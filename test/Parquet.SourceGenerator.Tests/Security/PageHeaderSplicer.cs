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
    public static async Task<byte[]> ReplaceFirstPageHeaderAsync(
        byte[] file,
        Func<PageHeader, byte[]> buildHeader
    )
    {
        long pageOffset;
        PageHeader original;
        long originalLength;
        using (var input = new MemoryStream(file, writable: false))
        {
            await using ParquetReader reader = await ParquetReader.CreateAsync(input);
            ColumnMetaData column = reader.Metadata!.RowGroups[0].Columns[0].MetaData!;
            pageOffset = column.DictionaryPageOffset is > 0
                ? column.DictionaryPageOffset.Value
                : column.DataPageOffset;

            System.Type protoType = typeof(ParquetReader).Assembly.GetType(
                "Parquet.Meta.Proto.ThriftCompactProtocolReader",
                throwOnError: true
            )!;
            input.Position = pageOffset;
            object proto = Activator.CreateInstance(
                protoType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [input],
                culture: null
            )!;
            original = (PageHeader)
                typeof(PageHeader)
                    .GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [proto])!;
            originalLength = input.Position - pageOffset;
        }

        byte[] replacement = buildHeader(original);
        int delta = replacement.Length - (int)originalLength;

        var spliced = new byte[file.Length + delta];
        Buffer.BlockCopy(file, 0, spliced, 0, (int)pageOffset);
        Buffer.BlockCopy(replacement, 0, spliced, (int)pageOffset, replacement.Length);
        Buffer.BlockCopy(
            file,
            (int)(pageOffset + originalLength),
            spliced,
            (int)pageOffset + replacement.Length,
            file.Length - (int)(pageOffset + originalLength)
        );

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
                        if (column.DataPageOffset > pageOffset)
                        {
                            column.DataPageOffset += delta;
                        }

                        if (
                            column.DictionaryPageOffset is > 0
                            && column.DictionaryPageOffset > pageOffset
                        )
                        {
                            column.DictionaryPageOffset += delta;
                        }
                    }
                }
            }
        );
    }
}
