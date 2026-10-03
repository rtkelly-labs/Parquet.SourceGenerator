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
/// <c>MaxAllocationValues</c> bounds a count; a footer declaring ten million rows is within it for
/// every column and reserved about a gigabyte of pooled buffers before a single page was read
/// (#361). <c>MaxAllocationBytes</c> bounds the bytes, from the row count and the model's per-row
/// buffer size, before renting.
/// </summary>
public sealed class AllocationBudgetTests
{
    private const int DeclaredRows = 10_000_000;

    private static async Task<byte[]> WriteAsync(int rows)
    {
        List<BoundsCheckRecord> items = Enumerable
            .Range(0, rows)
            .Select(i => new BoundsCheckRecord
            {
                Id = i,
                Amount = i,
                Ratio = i,
                OptionalValue = i % 2 == 0 ? i : null,
                CreatedAt = DateTime.UnixEpoch,
                Name = "n" + (i % 10),
            })
            .ToList();
        using var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        return stream.ToArray();
    }

    private static Task<byte[]> WithDeclaredRowsAsync(byte[] file, long rows) =>
        HostileParquetTests.RewriteColumnMetadataAsync(
            file,
            (metadata, _) =>
            {
                metadata.NumRows = rows;
                metadata.RowGroups[0].NumRows = rows;
            }
        );

    [Fact]
    public async Task TinyFileClaimingTenMillionRowsFailsFastUnderTheDefaultBudgetAsync()
    {
        byte[] hostile = await WithDeclaredRowsAsync(await WriteAsync(5), DeclaredRows);
        hostile.Length.ShouldBeLessThan(4096);

        InvalidDataException array = await Should.ThrowAsync<InvalidDataException>(() =>
            BoundsCheckRecordParquet.From(new MemoryStream(hostile)).ToArrayAsync()
        );
        InvalidDataException buffer = await Should.ThrowAsync<InvalidDataException>(() =>
            BoundsCheckRecordParquet.From(new ReadOnlyMemory<byte>(hostile)).ToArrayAsync()
        );
        InvalidDataException batches = await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (
                BoundsCheckRecordBatch _ in BoundsCheckRecordParquet
                    .From(new MemoryStream(hostile))
                    .AsBatches()
            )
            {
                // Draining is the point.
            }
        });
        InvalidDataException streamed = await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (
                BoundsCheckRecord _ in BoundsCheckRecordParquet
                    .From(new MemoryStream(hostile))
                    .AsAsyncEnumerable()
            )
            {
                // Draining is the point.
            }
        });

        foreach (InvalidDataException error in new[] { array, buffer, batches, streamed })
        {
            error.Message.ShouldContain("MaxAllocationBytes");
        }
    }

    [Fact]
    public async Task LegitimateLargeReadPassesOnceTheBudgetIsRaisedAsync()
    {
        byte[] file = await WriteAsync(60_000);

        // About 68 bytes a row for this model: 60,000 rows is roughly 4 MB of buffers.
        var tight = new ParquetSerializerOptions { MaxAllocationBytes = 1_000_000 };
        InvalidDataException refused = await Should.ThrowAsync<InvalidDataException>(() =>
            BoundsCheckRecordParquet.From(new MemoryStream(file)).WithOptions(tight).ToArrayAsync()
        );
        refused.Message.ShouldContain("MaxAllocationBytes");

        var raised = new ParquetSerializerOptions { MaxAllocationBytes = 64L * 1024 * 1024 };
        BoundsCheckRecord[] rows = await BoundsCheckRecordParquet
            .From(new MemoryStream(file))
            .WithOptions(raised)
            .ToArrayAsync();
        rows.Length.ShouldBe(60_000);

        // The default admits it too: the default is for hostile files, not for ordinary ones.
        (
            await BoundsCheckRecordParquet.From(new MemoryStream(file)).ToArrayAsync()
        ).Length.ShouldBe(60_000);
    }

    [Fact]
    public async Task ListColumnsShareOneBudgetWithEachOtherAndTheRowBuffersAsync()
    {
        var rows = new List<ListRow>
        {
            new()
            {
                Id = 1,
                Tags = Enumerable.Range(0, 100).Select(i => (string?)("t" + i)).ToList(),
                Scores = Enumerable.Range(0, 100).ToList(),
                Keys = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray(),
                When = Enumerable.Range(0, 100).Select(_ => (DateTime?)DateTime.UnixEpoch).ToList(),
                Blobs = Enumerable.Range(0, 100).Select(i => new[] { (byte)i }).ToList(),
            },
        };
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        byte[] file = stream.ToArray();

        // Each of the five list columns holds 100 entries, about 2,400 bytes apiece: any one fits in 3,000
        // bytes, all five together with the row buffers do not.
        var tight = new ParquetSerializerOptions { MaxAllocationBytes = 3_000 };
        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() =>
            ListRowParquet.From(new MemoryStream(file)).WithOptions(tight).ToArrayAsync()
        );
        error.Message.ShouldContain("already allocated");

        var roomy = new ParquetSerializerOptions { MaxAllocationBytes = 1_000_000 };
        ListRow[] read = await ListRowParquet
            .From(new MemoryStream(file))
            .WithOptions(roomy)
            .ToArrayAsync();
        read.Length.ShouldBe(1);
    }

    [Fact]
    public async Task ListColumnsAreCheckedAgainstTheBudgetPerColumnAsync()
    {
        var rows = new List<ListRow>
        {
            new() { Id = 1, Scores = [1, 2, 3] },
        };
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);

        var tiny = new ParquetSerializerOptions { MaxAllocationBytes = 8 };
        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() =>
            ListRowParquet.From(new MemoryStream(stream.ToArray())).WithOptions(tiny).ToArrayAsync()
        );

        error.Message.ShouldContain("MaxAllocationBytes");
    }
}
