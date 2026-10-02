using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Tests.Security;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The read path rents a buffer for the row group's declared <c>num_rows</c> and asks Parquet.Net to
/// fill it, but Parquet.Net fills only the column chunk's own <c>num_values</c>. A hostile footer
/// that declares more rows than the chunk holds therefore leaves the tail of the buffer unwritten,
/// and a pooled value-type buffer is returned without clearing, so the tail is whatever an earlier,
/// unrelated read left in it (#382).
/// </summary>
public sealed class ShortFillTests
{
    private static readonly int[] ExpectedIds = [1, 2, 3];

    private static async Task<byte[]> WriteAsync(int count, int idBase)
    {
        List<BatchRequiredInFile> rows = Enumerable
            .Range(0, count)
            .Select(i => new BatchRequiredInFile { Id = idBase + i, Score = idBase + i })
            .ToList();
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        return stream.ToArray();
    }

    private static async Task DirtyThePoolAsync()
    {
        // Leave recognisable values in pooled int and double buffers of the sizes the next read rents.
        for (int i = 0; i < 20; i++)
        {
            byte[] dirty = await WriteAsync(16, 777_000);
            await BatchRequiredInFileParquet.From(new MemoryStream(dirty)).ToArrayAsync();
        }
    }

    private static Task<byte[]> DeclareMoreRowsThanTheChunkHoldsAsync(byte[] valid) =>
        HostileParquetTests.RewriteColumnMetadataAsync(
            valid,
            (metadata, _) =>
            {
                metadata.NumRows += 4;
                metadata.RowGroups[0].NumRows += 4;
            }
        );

    [Fact]
    public async Task RowsReadFromAShortChunkAreRejectedNotFilledWithPoolDataAsync()
    {
        await DirtyThePoolAsync();
        byte[] damaged = await DeclareMoreRowsThanTheChunkHoldsAsync(await WriteAsync(3, 1));

        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() =>
            BatchRequiredInFileParquet.From(new MemoryStream(damaged)).ToArrayAsync()
        );

        error.Message.ShouldContain("Id");
    }

    [Fact]
    public async Task BatchesReadFromAShortChunkAreRejectedAsync()
    {
        await DirtyThePoolAsync();
        byte[] damaged = await DeclareMoreRowsThanTheChunkHoldsAsync(await WriteAsync(3, 1));

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (
                BatchRequiredInFileBatch batch in BatchRequiredInFileParquet
                    .From(new MemoryStream(damaged))
                    .AsBatches()
            )
            {
                batch.RowCount.ShouldBeGreaterThan(0);
            }
        });
    }

    [Fact]
    public async Task HonestFilesStillReadAsync()
    {
        BatchRequiredInFile[] rows = await BatchRequiredInFileParquet
            .From(new MemoryStream(await WriteAsync(3, 1)))
            .ToArrayAsync();

        rows.Select(r => r.Id).ShouldBe(ExpectedIds);
    }
}
