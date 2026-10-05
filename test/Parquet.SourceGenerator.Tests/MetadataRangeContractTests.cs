using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Tests.Security;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public sealed partial record NoStatisticsRow
{
    public Guid Key { get; init; }

    public bool Flag { get; init; }
}

[ParquetSerializable]
public sealed partial record TimeOfDayRow
{
    public int Id { get; init; }

    public TimeOnly At { get; init; }

    public TimeOnly? MaybeAt { get; init; }
}

/// <summary>
/// Hostile input is documented to fail with <see cref="InvalidDataException"/>. A 64-bit row count
/// above <c>int.MaxValue</c> reached a checked cast first and surfaced as
/// <see cref="OverflowException"/>, and a <c>TIME_MICROS</c> value outside one day surfaced as
/// <see cref="ArgumentOutOfRangeException"/> (or, when the multiply wrapped, as a wrong time of
/// day) (#371).
/// </summary>
public sealed class MetadataRangeContractTests
{
    private static readonly Guid FirstKey = Guid.NewGuid();

    private const long Huge = 5_000_000_000;

    private static async Task<byte[]> WriteFlatAsync()
    {
        var rows = Enumerable
            .Range(0, 4)
            .Select(i => new BatchRequiredInFile { Id = i, Score = i })
            .ToList();
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        return stream.ToArray();
    }

    private static Task<byte[]> WithHugeRowCountAsync(
        byte[] file,
        int rowGroup,
        long rows = Huge
    ) =>
        HostileParquetTests.RewriteColumnMetadataAsync(
            file,
            (metadata, _) =>
            {
                metadata.NumRows = rows;
                metadata.RowGroups[rowGroup].NumRows = rows;
            }
        );

    [Fact]
    public async Task RowCountAboveIntRangeIsInvalidDataOnTheStreamingReadAsync()
    {
        byte[] huge = await WithHugeRowCountAsync(await WriteFlatAsync(), 0);

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (
                BatchRequiredInFile _ in BatchRequiredInFileParquet
                    .From(new MemoryStream(huge))
                    .AsAsyncEnumerable()
            )
            {
                // Draining is the point.
            }
        });
    }

    [Fact]
    public async Task RowCountAboveIntRangeIsInvalidDataOnTheBatchReadAsync()
    {
        byte[] huge = await WithHugeRowCountAsync(await WriteFlatAsync(), 0);

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (
                BatchRequiredInFileBatch _ in BatchRequiredInFileParquet
                    .From(new MemoryStream(huge))
                    .AsBatches()
            )
            {
                // Draining is the point.
            }
        });
    }

    [Fact]
    public async Task RowCountAboveIntRangeIsInvalidDataOnTheSortedRangeReadAsync()
    {
        List<SortedEvent> events = Enumerable
            .Range(0, 300)
            .Select(i => new SortedEvent
            {
                SequenceNumber = i,
                Timestamp = DateTime.UnixEpoch,
                Payload = "p",
            })
            .ToList();
        using var stream = new MemoryStream();
        await events.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 100 }
        );
        byte[] huge = await WithHugeRowCountAsync(stream.ToArray(), 1);

        await Should.ThrowAsync<InvalidDataException>(() =>
            SortedEventParquet
                .From(new MemoryStream(huge))
                .Where(m => m.SequenceNumber.MayContainBetween(0, 299))
                .ToArrayAsync()
        );
    }

    [Fact]
    public async Task TimeOfDayOutsideOneDayIsInvalidDataAsync()
    {
        var rows = new List<TimeOfDayRow>
        {
            new()
            {
                Id = 1,
                At = new TimeOnly(1, 0, 0),
                MaybeAt = new TimeOnly(2, 0, 0),
            },
        };
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        byte[] file = stream.ToArray();

        // One hour is 3.6e9 microseconds. Overwrite its PLAIN encoding with a value far past a day.
        byte[] needle = BitConverter.GetBytes(TimeSpan.FromHours(1).Ticks / 10);
        byte[] replacement = BitConverter.GetBytes(long.MaxValue / 7);
        int patched = 0;
        for (int at = file.AsSpan().IndexOf(needle); at >= 0; )
        {
            replacement.CopyTo(file, at);
            patched++;
            int next = file.AsSpan(at + needle.Length).IndexOf(needle);
            at = next < 0 ? -1 : at + needle.Length + next;
        }

        // The value also appears in the page header's min/max statistics; every copy is rewritten.
        patched.ShouldBeGreaterThan(0, "the TIME_MICROS value was not found verbatim in the file");

        await Should.ThrowAsync<InvalidDataException>(() =>
            TimeOfDayRowParquet.From(new MemoryStream(file)).ToArrayAsync()
        );
    }

    [Fact]
    public async Task RowCountAboveIntRangeIsInvalidDataWhenNoColumnHasStatisticsToPruneOnAsync()
    {
        // Guid and bool columns have no pruning statistics, so the read sizes its result from a plain
        // sum of the footer row counts rather than the pruning pre-pass.
        var rows = new List<NoStatisticsRow>
        {
            new() { Key = FirstKey, Flag = true },
        };
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        byte[] huge = await WithHugeRowCountAsync(stream.ToArray(), 0, int.MaxValue + 1L);

        await Should.ThrowAsync<InvalidDataException>(() =>
            NoStatisticsRowParquet.From(new MemoryStream(huge)).ToArrayAsync()
        );
    }

    [Fact]
    public async Task ValidTimeOfDayStillReadsAsync()
    {
        var rows = new List<TimeOfDayRow>
        {
            new()
            {
                Id = 1,
                At = new TimeOnly(23, 59, 59),
                MaybeAt = null,
            },
        };
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);

        TimeOfDayRow[] read = await TimeOfDayRowParquet
            .From(new MemoryStream(stream.ToArray()))
            .ToArrayAsync();

        read[0].At.ShouldBe(new TimeOnly(23, 59, 59));
        read[0].MaybeAt.ShouldBeNull();
    }
}
