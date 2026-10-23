using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Meta;
using Parquet.SourceGenerator;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record IcebergColumnsRecord
{
    [ParquetColumn("id", Order = 1, FieldId = 1)]
    public long Id { get; init; }

    [ParquetColumn("local_ts", Order = 2, FieldId = 2)]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds, AdjustedToUtc = false)]
    public DateTime LocalTs { get; init; }

    [ParquetColumn("event_ts", Order = 3, FieldId = 3)]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime EventTs { get; init; }

    [ParquetColumn("day", Order = 4, FieldId = 4)]
    public DateOnly Day { get; init; }

    [ParquetColumn("note", Order = 5)]
    public string? Note { get; init; }

    [ParquetColumn("maybe_day", Order = 6)]
    public DateOnly? MaybeDay { get; init; }
}

/// <summary>
/// The column shapes Apache Iceberg (and every engine reading by Parquet field ID) needs: field IDs,
/// a local (not UTC-adjusted) timestamp and INT32 DATE for <c>DateOnly</c>.
/// </summary>
public sealed class IcebergColumnTests
{
    private static readonly IcebergColumnsRecord[] Rows = Enumerable
        .Range(0, 3)
        .Select(i => new IcebergColumnsRecord
        {
            Id = i,
            LocalTs = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(10 * i),
            EventTs = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddMinutes(i),
            Day = new DateOnly(2026, 1, 1).AddDays(i),
            Note = i == 1 ? null : $"n{i}",
            MaybeDay = i == 2 ? null : new DateOnly(1970, 1, 1).AddDays(-i),
        })
        .ToArray();

    private static async Task<(MemoryStream Stream, SchemaElement[] Leaves)> WriteAsync()
    {
        var stream = new MemoryStream();
        await Rows.WriteParquetAsync(stream);
        stream.Position = 0;
        await using var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true);
        var leaves = reader.Metadata!.Schema.Skip(1).ToArray();
        stream.Position = 0;
        return (stream, leaves);
    }

    [Fact]
    public async Task FieldIdsAreWrittenOnlyWhereDeclared()
    {
        var (_, leaves) = await WriteAsync();
        leaves.Select(l => l.FieldId).ShouldBe(new int?[] { 1, 2, 3, 4, null, null });
    }

    [Fact]
    public async Task TimestampsCarryTheDeclaredUtcAdjustment()
    {
        var (_, leaves) = await WriteAsync();
        leaves[1].Type.ShouldBe(Parquet.Meta.Type.INT64);
        leaves[1].LogicalType!.TIMESTAMP!.IsAdjustedToUTC.ShouldBeFalse();
        leaves[1].LogicalType!.TIMESTAMP!.Unit.MICROS.ShouldNotBeNull();
        // Unchanged: the UTC-adjusted column keeps the encoding earlier releases wrote.
        leaves[2].Type.ShouldBe(Parquet.Meta.Type.INT64);
        leaves[2].ConvertedType.ShouldBe(ConvertedType.TIMESTAMP_MICROS);
    }

    [Fact]
    public async Task DateOnlyIsInt32Date()
    {
        var (_, leaves) = await WriteAsync();
        leaves[3].Type.ShouldBe(Parquet.Meta.Type.INT32);
        leaves[3].ConvertedType.ShouldBe(ConvertedType.DATE);
        leaves[5].Type.ShouldBe(Parquet.Meta.Type.INT32);
    }

    [Fact]
    public async Task IcebergColumnsRoundTrip()
    {
        var (stream, _) = await WriteAsync();
        var back = await IcebergColumnsRecordParquet.From(stream).ToArrayAsync();
        back.Length.ShouldBe(Rows.Length);
        for (int i = 0; i < Rows.Length; i++)
        {
            back[i].Id.ShouldBe(Rows[i].Id);
            back[i].LocalTs.Ticks.ShouldBe(Rows[i].LocalTs.Ticks);
            back[i].EventTs.ShouldBe(Rows[i].EventTs);
            back[i].Day.ShouldBe(Rows[i].Day);
            back[i].Note.ShouldBe(Rows[i].Note);
            back[i].MaybeDay.ShouldBe(Rows[i].MaybeDay);
        }
    }
}
