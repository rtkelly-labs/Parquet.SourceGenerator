using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record NullBypassRecord
{
    [ParquetColumn("optional_int")]
    public int? OptionalInt { get; init; }

    [ParquetColumn("optional_string")]
    public string? OptionalString { get; init; }

    [ParquetColumn("optional_bytes")]
    public byte[]? OptionalBytes { get; init; }

    [ParquetColumn("optional_guid")]
    public Guid? OptionalGuid { get; init; }

    [ParquetColumn("optional_date_time")]
    public DateTime? OptionalDateTime { get; init; }

    [ParquetColumn("optional_duration")]
    public TimeSpan? OptionalDuration { get; init; }

    [ParquetColumn("optional_status")]
    public EventStatus? OptionalStatus { get; init; }
}

public sealed class NullBypassReadTests
{
    [Fact]
    public async Task AllNullRowGroupRoundtripsThroughEveryReadPath()
    {
        var items = Enumerable.Range(0, 4).Select(_ => new NullBypassRecord()).ToList();
        byte[] parquet = await WriteAsync(items);

        await using (var reader = await ParquetReader.CreateAsync(new MemoryStream(parquet)))
        {
            using var rowGroup = reader.OpenRowGroupReader(0);
            rowGroup.RowCount.ShouldBe(4);
            foreach (var field in reader.Schema.DataFields)
            {
                rowGroup.GetStatistics(field)?.NullCount.ShouldBe(4);
            }
        }

        List<NullBypassRecord> sequential = await ReadSequentialAsync(parquet);
        NullBypassRecord[] array = await NullBypassRecordParquet
            .From(new MemoryStream(parquet))
            .ToArrayAsync();
        List<NullBypassRecord> parallel = await NullBypassRecordParquet
            .From(parquet)
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 2 })
            .Parallel()
            .ToListAsync();
        NullBypassRecord[] parallelArray = await NullBypassRecordParquet
            .From(parquet)
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 2 })
            .Parallel()
            .ToArrayAsync();

        var streamed = new List<NullBypassRecord>();
        await foreach (
            NullBypassRecord item in NullBypassRecordParquet
                .From(new MemoryStream(parquet))
                .AsAsyncEnumerable()
        )
        {
            streamed.Add(item);
        }

        AssertAllNull(sequential);
        AssertAllNull(array);
        AssertAllNull(parallel);
        AssertAllNull(parallelArray);
        AssertAllNull(streamed);
    }

    [Fact]
    public async Task MixedNullableValuesRoundtripWithoutChangingNullSemantics()
    {
        Guid firstGuid = Guid.NewGuid();
        Guid secondGuid = Guid.NewGuid();
        DateTime firstDate = new(2024, 6, 15, 12, 30, 0, 123, DateTimeKind.Utc);
        TimeSpan firstDuration = TimeSpan.FromMilliseconds(1234);
        var expected = new List<NullBypassRecord>
        {
            new()
            {
                OptionalInt = 7,
                OptionalString = "first",
                OptionalBytes = new byte[] { 1, 2 },
                OptionalGuid = firstGuid,
                OptionalDateTime = firstDate,
                OptionalDuration = firstDuration,
                OptionalStatus = EventStatus.Active,
            },
            new()
            {
                OptionalString = string.Empty,
                OptionalBytes = Array.Empty<byte>(),
                OptionalGuid = secondGuid,
                OptionalStatus = EventStatus.Closed,
            },
            new()
            {
                OptionalInt = -3,
                OptionalDateTime = firstDate.AddDays(1),
                OptionalDuration = TimeSpan.Zero,
            },
        };

        byte[] parquet = await WriteAsync(expected);
        List<NullBypassRecord> actual = await ReadSequentialAsync(parquet);

        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].OptionalInt.ShouldBe(expected[i].OptionalInt);
            actual[i].OptionalString.ShouldBe(expected[i].OptionalString);
            actual[i].OptionalBytes.ShouldBe(expected[i].OptionalBytes);
            actual[i].OptionalGuid.ShouldBe(expected[i].OptionalGuid);
            (actual[i].OptionalDateTime?.Ticks / TimeSpan.TicksPerMillisecond).ShouldBe(
                expected[i].OptionalDateTime?.Ticks / TimeSpan.TicksPerMillisecond
            );
            actual[i].OptionalDuration.ShouldBe(expected[i].OptionalDuration);
            actual[i].OptionalStatus.ShouldBe(expected[i].OptionalStatus);
        }
    }

    private static async Task<byte[]> WriteAsync(List<NullBypassRecord> items)
    {
        using var stream = new MemoryStream();
        await items.WriteParquetAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = items.Count }
        );
        return stream.ToArray();
    }

    private static async Task<List<NullBypassRecord>> ReadSequentialAsync(byte[] parquet)
    {
        return await NullBypassRecordParquet.From(new MemoryStream(parquet)).ToListAsync();
    }

    private static void AssertAllNull(IEnumerable<NullBypassRecord> items)
    {
        foreach (NullBypassRecord item in items)
        {
            item.OptionalInt.ShouldBeNull();
            item.OptionalString.ShouldBeNull();
            item.OptionalBytes.ShouldBeNull();
            item.OptionalGuid.ShouldBeNull();
            item.OptionalDateTime.ShouldBeNull();
            item.OptionalDuration.ShouldBeNull();
            item.OptionalStatus.ShouldBeNull();
        }
    }
}
