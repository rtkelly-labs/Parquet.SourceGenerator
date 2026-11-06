using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// ── New type test models ───────────────────────────────────────────────────

public enum EventStatus
{
    Pending = 0,
    Active = 1,
    Closed = 2,
}

[ParquetSerializable]
public partial record TypeCoverageRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("created_at")]
    public DateTime CreatedAt { get; init; }

    [ParquetColumn("correlation_id")]
    public Guid CorrelationId { get; init; }

    [ParquetColumn("status")]
    public EventStatus Status { get; init; }

    [ParquetColumn("duration_ms")]
    public TimeSpan Duration { get; init; }
}

[ParquetSerializable]
public partial record CompactTimestampRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("micro_ts")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime MicroTs { get; init; }
}

[ParquetSerializable]
public partial record NullableTypeCoverageRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("created_at")]
    public DateTime? CreatedAt { get; init; }

    [ParquetColumn("correlation_id")]
    public Guid? CorrelationId { get; init; }

    [ParquetColumn("status")]
    public EventStatus? Status { get; init; }
}

[ParquetSerializable]
public partial record TimeOnlyCoverageRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("time_of_day")]
    public TimeOnly TimeOfDay { get; init; }

    [ParquetColumn("optional_time")]
    public TimeOnly? OptionalTime { get; init; }
}

// ── Tests ──────────────────────────────────────────────────────────────────

public sealed class TypeCoverageTests
{
    [Fact]
    public async Task DateTimeRoundtripsCorrectly()
    {
        var now = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var items = new List<TypeCoverageRecord>
        {
            new()
            {
                Id = 1,
                CreatedAt = now,
                CorrelationId = Guid.Empty,
                Status = EventStatus.Active,
                Duration = TimeSpan.FromSeconds(30),
            },
        };

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.ShouldHaveSingleItem();
        // DateTime precision in Parquet Impala format is milliseconds
        (result[0].CreatedAt.Ticks / TimeSpan.TicksPerMillisecond).ShouldBe(
            now.Ticks / TimeSpan.TicksPerMillisecond
        );
    }

    [Fact]
    public async Task CompactMicrosecondTimestampRoundtripsCorrectly()
    {
        var now = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var items = new List<CompactTimestampRecord>
        {
            new() { Id = 1, MicroTs = now },
        };

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        byte[] bytes = stream.ToArray();

        // Test zero-copy ReadOnlyMemory overload as well!
        ReadOnlyMemory<byte> mem = bytes;
        var result = await CompactTimestampRecordParquet.From(mem).ToArrayAsync();

        result.ShouldHaveSingleItem();
        (result[0].MicroTs.Ticks / TimeSpan.TicksPerMillisecond).ShouldBe(
            now.Ticks / TimeSpan.TicksPerMillisecond
        );
    }

    [Fact]
    public async Task CustomOptionsRoundtripCorrectly()
    {
        var items = TestFakers.CreateTypeCoverageRecordFaker().Generate(100);

        var options = new ParquetSerializerOptions
        {
            RowGroupSize = 25,
            MaxDegreeOfParallelism = 2,
            CompressionMethod = ParquetCompressionMethod.Snappy,
        };

        var stream = new MemoryStream();
        await items.WriteParquetBatchedAsync(stream, options: options);
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet
            .From(stream.ToArray())
            .WithOptions(options)
            .Parallel()
            .ToArrayAsync();

        result.Length.ShouldBe(100);
        result[50].Id.ShouldBe(items[50].Id);
    }

    [Fact]
    public async Task AsyncEnumerableStreamingRoundtripsCorrectly()
    {
        static async IAsyncEnumerable<TypeCoverageRecord> GenerateAsyncStream()
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Yield();
                yield return new TypeCoverageRecord
                {
                    Id = i,
                    CreatedAt = DateTime.UtcNow,
                    CorrelationId = Guid.NewGuid(),
                    Status = (EventStatus)(i % 3),
                    Duration = TimeSpan.FromMilliseconds(i * 50),
                };
            }
        }

        var stream = new MemoryStream();
        await GenerateAsyncStream()
            .WriteParquetAsync(stream, new ParquetSerializerOptions { RowGroupSize = 10 });
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.Length.ShouldBe(50);
        result[0].Id.ShouldBe(0);
        result[49].Id.ShouldBe(49);
    }

    [Fact]
    public async Task GuidRoundtripsCorrectly()
    {
        var id = Guid.NewGuid();
        var items = new List<TypeCoverageRecord>
        {
            new()
            {
                Id = 1,
                CreatedAt = DateTime.UtcNow,
                CorrelationId = id,
                Status = EventStatus.Pending,
                Duration = TimeSpan.Zero,
            },
        };

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.ShouldHaveSingleItem();
        result[0].CorrelationId.ShouldBe(id);
    }

    [Fact]
    public async Task EnumRoundtripsCorrectly()
    {
        var items = Enum.GetValues<EventStatus>()
            .Select(
                (s, idx) =>
                    new TypeCoverageRecord
                    {
                        Id = idx,
                        CreatedAt = DateTime.UtcNow,
                        CorrelationId = Guid.NewGuid(),
                        Status = s,
                        Duration = TimeSpan.FromMinutes(idx),
                    }
            )
            .ToList();

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.Length.ShouldBe(items.Count);
        for (int i = 0; i < items.Count; i++)
            result[i].Status.ShouldBe(items[i].Status);
    }

    [Fact]
    public async Task TimeSpanRoundtripsCorrectly()
    {
        var duration = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(15);
        var items = new List<TypeCoverageRecord>
        {
            new()
            {
                Id = 1,
                CreatedAt = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid(),
                Status = EventStatus.Active,
                Duration = duration,
            },
        };

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.ShouldHaveSingleItem();
        // TimeSpan in Parquet MilliSeconds format — verify millisecond precision
        (result[0].Duration.Ticks / TimeSpan.TicksPerMillisecond).ShouldBe(
            duration.Ticks / TimeSpan.TicksPerMillisecond
        );
    }

    [Fact]
    public async Task MultipleRowGroupsBatchedRoundtripsCorrectly()
    {
        var items = TestFakers.CreateTypeCoverageRecordFaker().Generate(1_000);

        var stream = new MemoryStream();
        // Write in 3 row groups (rowGroupSize=333 → 3 groups + tail)
        await ((IEnumerable<TypeCoverageRecord>)items).WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 333 }
        );
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.Length.ShouldBe(items.Count);
        result[500].Id.ShouldBe(items[500].Id);
        result[500].CorrelationId.ShouldBe(items[500].CorrelationId);
        result[500].Status.ShouldBe(items[500].Status);
    }

    [Fact]
    public async Task ParallelToArrayAsyncRoundtripsCorrectly()
    {
        var items = TestFakers.CreateTypeCoverageRecordFaker().Generate(1_000);

        var stream = new MemoryStream();
        await ((IEnumerable<TypeCoverageRecord>)items).WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 250 }
        );
        stream.Position = 0;

        var result = await TypeCoverageRecordParquet
            .From(stream.ToArray())
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
            .Parallel()
            .ToArrayAsync();

        result.Length.ShouldBe(items.Count);
        result[0].Id.ShouldBe(items[0].Id);
        result[500].Id.ShouldBe(items[500].Id);
        result[999].Id.ShouldBe(items[999].Id);
        result[500].CorrelationId.ShouldBe(items[500].CorrelationId);
    }

    [Fact]
    public async Task NullableTypesRoundtripCorrectly()
    {
        var id1 = Guid.NewGuid();
        var items = new List<NullableTypeCoverageRecord>
        {
            new()
            {
                Id = 1,
                CreatedAt = null,
                CorrelationId = null,
                Status = null,
            },
            new()
            {
                Id = 2,
                CreatedAt = DateTime.UtcNow,
                CorrelationId = id1,
                Status = EventStatus.Closed,
            },
        };

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        stream.Position = 0;

        var result = await NullableTypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.Length.ShouldBe(2);
        result[0].CreatedAt.ShouldBeNull();
        result[0].CorrelationId.ShouldBeNull();
        result[0].Status.ShouldBeNull();
        result[1].CorrelationId.ShouldBe(id1);
        result[1].Status.ShouldBe(EventStatus.Closed);
    }

    [Fact]
    public async Task NullableTypesFuzzedWithBogusRoundtripsCorrectly()
    {
        var items = TestFakers.CreateNullableTypeCoverageRecordFaker().Generate(100);

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        stream.Position = 0;

        var result = await NullableTypeCoverageRecordParquet.From(stream).ToArrayAsync();

        result.Length.ShouldBe(100);
        for (int i = 0; i < items.Count; i++)
        {
            result[i].Id.ShouldBe(items[i].Id);
            result[i].CorrelationId.ShouldBe(items[i].CorrelationId);
            result[i].Status.ShouldBe(items[i].Status);
        }
    }

    [Fact]
    public async Task TimeOnlyRoundtripsCorrectly()
    {
        var t1 = new TimeOnly(14, 30, 45, 123);
        var t2 = new TimeOnly(9, 15, 0, 500);

        var items = new List<TimeOnlyCoverageRecord>
        {
            new()
            {
                Id = 1,
                TimeOfDay = t1,
                OptionalTime = null,
            },
            new()
            {
                Id = 2,
                TimeOfDay = t2,
                OptionalTime = t1,
            },
        };

        var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);
        stream.Position = 0;

        var result = await TimeOnlyCoverageRecordParquet.From(stream).ToArrayAsync();

        result.Length.ShouldBe(2);
        result[0].Id.ShouldBe(1);
        result[0].TimeOfDay.ShouldBe(t1);
        result[0].OptionalTime.ShouldBeNull();

        result[1].Id.ShouldBe(2);
        result[1].TimeOfDay.ShouldBe(t2);
        result[1].OptionalTime.ShouldBe(t1);
    }
}
