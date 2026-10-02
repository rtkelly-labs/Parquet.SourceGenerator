using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Every read path bounds the total rows it will materialise by
/// <c>MaxAllocationValues</c>; the pruned range reader capped each row group but not their sum
/// (#387).
/// </summary>
public sealed class PrunedRangeAggregateCapTests
{
    private static async Task<byte[]> WriteAsync(int rows, int rowGroupSize)
    {
        var events = Enumerable
            .Range(0, rows)
            .Select(i => new SortedEvent
            {
                SequenceNumber = i,
                Timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                Bucket = i % 7,
                Payload = "p" + i,
            })
            .ToList();
        using var stream = new MemoryStream();
        await events.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
        );
        return stream.ToArray();
    }

    [Fact]
    public async Task RangeSpanningGroupsThatTogetherExceedTheCapIsRejectedAsync()
    {
        // Six groups of 100 rows: each is under the cap of 250, the six together are not.
        byte[] bytes = await WriteAsync(600, rowGroupSize: 100);
        var options = new ParquetSerializerOptions { MaxAllocationValues = 250 };

        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() =>
            SortedEventParquetExtensions.ReadParquetSequenceNumberRangeAsync(
                new MemoryStream(bytes),
                0,
                599,
                options: options
            )
        );

        error.Message.ShouldContain("Total row count");
    }

    [Fact]
    public async Task RangeWithinTheCapStillReadsAsync()
    {
        byte[] bytes = await WriteAsync(600, rowGroupSize: 100);
        var options = new ParquetSerializerOptions { MaxAllocationValues = 250 };

        List<SortedEvent> rows =
            await SortedEventParquetExtensions.ReadParquetSequenceNumberRangeAsync(
                new MemoryStream(bytes),
                120,
                210,
                options: options
            );

        rows.Count.ShouldBe(91);
    }
}
