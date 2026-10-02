using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.SourceGenerator;

namespace PackageConsumptionLegacy;

[ParquetSerializable]
public sealed partial class LabelOnly
{
    [ParquetColumn("label")]
    public string? Label { get; set; }
}

/// <summary>
/// Runs the decompression guard against a real Parquet.Net 4.25 read of a column chunk with more
/// than one page (#360). Parquet.Net 4.25 seeks once to the start of a chunk and then reads its
/// pages one after another, so a guard that only checked seek targets bounded the first page and
/// nothing after it. A dictionary-encoded column is two pages: a dictionary page of a few bytes,
/// then the data page of indexes that follows it.
/// </summary>
internal static class DecompressionGuardScenario
{
    // Between the two pages: above the dictionary page (26 bytes) and below the data page (about
    // 8 KB), so only the page reached by reading on can exceed it.
    private const int PageLimit = 100;

    public static async Task<bool> RunAsync()
    {
        string[] labels = ["alpha", "beta", "gamma"];
        List<LabelOnly> rows = Enumerable
            .Range(0, 4_000)
            .Select(i => new LabelOnly { Label = labels[i % labels.Length] })
            .ToList();

        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        long chunkStart = await ChunkStartAsync(stream);

        // The file reads under the default limits, so the failure below is the limit and not a
        // problem with the file.
        stream.Position = 0;
        List<LabelOnly> read = await LabelOnlyParquetLegacyExtensions.ReadParquetAsync(stream);
        if (read.Count != rows.Count)
        {
            Console.Error.WriteLine($"FAILED: expected {rows.Count} rows, read {read.Count}.");
            return false;
        }

        var options = new ParquetSerializerOptions { MaxDecompressedPageSize = PageLimit };
        try
        {
            stream.Position = 0;
            await LabelOnlyParquetLegacyExtensions.ReadParquetAsync(stream, options);
            Console.Error.WriteLine(
                "FAILED: the legacy page-size limit did not reject a page reached by reading on from the previous page."
            );
            return false;
        }
        catch (InvalidDataException exception)
        {
            if (!exception.Message.Contains("uncompressed size", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"FAILED: the legacy page-size limit raised the wrong error: {exception.Message}"
                );
                return false;
            }

            // The first page of the chunk is under the limit, so the page rejected is a later one.
            if (exception.Message.Contains($"offset {chunkStart} ", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"FAILED: the first page was rejected, so the file does not have the two pages this scenario needs: {exception.Message}"
                );
                return false;
            }
        }

        return true;
    }

    private static async Task<long> ChunkStartAsync(MemoryStream stream)
    {
        stream.Position = 0;
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        return reader.Metadata!.RowGroups[0].Columns[0].MetaData!.DataPageOffset;
    }
}
