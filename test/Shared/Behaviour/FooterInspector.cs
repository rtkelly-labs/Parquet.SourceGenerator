using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// What a written file's footer records, read with the Parquet.Net the project references rather
/// than through generated code: the row groups, their row counts and every column chunk's codec.
/// Uses only footer members Parquet.Net 4.25 and 6.x share.
/// </summary>
public sealed class FooterSummary
{
    public FooterSummary(IReadOnlyList<long> rowGroupRows, IReadOnlyList<string> chunkCodecs)
    {
        RowGroupRows = rowGroupRows;
        ChunkCodecs = chunkCodecs;
    }

    public IReadOnlyList<long> RowGroupRows { get; }

    /// <summary>The <c>CompressionCodec</c> name of every column chunk in the file.</summary>
    public IReadOnlyList<string> ChunkCodecs { get; }

    public static async Task<FooterSummary> ReadAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream);
        try
        {
            Meta.FileMetaData metadata = reader.Metadata!;

            var rows = metadata.RowGroups.Select(g => g.NumRows).ToList();
            var codecs = metadata
                .RowGroups.SelectMany(g => g.Columns)
                .Select(c => c.MetaData!.Codec.ToString())
                .ToList();
            return new FooterSummary(rows, codecs);
        }
        finally
        {
            // 6.x's reader is only IAsyncDisposable, 4.25's only IDisposable.
            await DisposeAsync(reader);
        }
    }

    private static async Task DisposeAsync(object disposable)
    {
        switch (disposable)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable syncDisposable:
                syncDisposable.Dispose();
                break;
        }
    }
}
