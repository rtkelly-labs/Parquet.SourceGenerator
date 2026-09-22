using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NodaTime;
using Parquet.Schema;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Adapted members nested inside custom types (docs/44 §A.4c): a <c>[ParquetSerializable]</c>
/// child with NodaTime fields as a member, a nullable member, a list element and an array
/// element; a value-type child; an application surrogate that itself contains NodaTime fields;
/// and the NodaTime defaults whose storage nests a group, as list elements. Every nested
/// conversion is inline — no shadow member below the root.
/// </summary>
public sealed class NestedAdapterRoundTripTests
{
    private static readonly DateTimeZone London = DateTimeZoneProviders.Tzdb["Europe/London"];

    [Fact]
    public async Task CustomTypesWithNodaTimeFieldsRoundTripAtEveryPosition()
    {
        Measurement a = new()
        {
            Value = 1.5,
            At = Instant.MinValue,
            Time = LocalTime.Noon,
            Retention = Period.FromMinutes(90),
        };
        Measurement b = new()
        {
            Value = -2,
            At = NodaConstants.UnixEpoch - Duration.FromNanoseconds(1),
            Time = null,
            Retention = null,
        };
        var rows = new List<SensorRow>
        {
            new()
            {
                Reading = a,
                Maybe = b,
                History = [a, b],
                MaybeHistory = [null, b],
                Stamp = new Stamp { At = Instant.MaxValue, Seq = 7 },
                Stamps = [new Stamp { At = NodaConstants.UnixEpoch, Seq = 1 }],
            },
            new()
            {
                Reading = b,
                Maybe = null,
                History = [],
                MaybeHistory = null,
                Stamp = default,
                Stamps = [],
            },
        };

        using var ms = new MemoryStream();
        await SensorRowParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<SensorRow> back = await SensorRowParquetExtensions.ReadParquetAsync(ms);

        back.Count.ShouldBe(2);
        for (int i = 0; i < rows.Count; i++)
        {
            back[i].Reading.ShouldBe(rows[i].Reading);
            back[i].Maybe.ShouldBe(rows[i].Maybe);
            back[i].History.ShouldBe(rows[i].History);
            back[i].MaybeHistory.ShouldBe(rows[i].MaybeHistory);
            back[i].Stamp.ShouldBe(rows[i].Stamp);
            back[i].Stamps.ShouldBe(rows[i].Stamps);
        }

        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        var reading = (StructField)reader.Schema.Fields.Single(f => f.Name == "Reading");
        reading.Fields.Single(f => f.Name == "At").ShouldBeOfType<StructField>();
        var history = (ListField)reader.Schema.Fields.Single(f => f.Name == "History");
        ((StructField)history.Item)
            .Fields.Single(f => f.Name == "At")
            .ShouldBeOfType<StructField>();
    }

    [Fact]
    public async Task ASurrogateContainingNodaTimeFieldsRoundTrips()
    {
        var rows = new List<Agenda>
        {
            new()
            {
                Main = new Appointment(
                    Instant.FromUtc(2024, 5, 1, 9, 0),
                    new LocalDate(2024, 5, 1),
                    "standup"
                ),
                Extra = null,
                All =
                [
                    new Appointment(
                        NodaConstants.UnixEpoch,
                        new LocalDate(1970, 1, 1).WithCalendar(CalendarSystem.Julian),
                        "epoch"
                    ),
                ],
            },
            new()
            {
                Main = new Appointment(Instant.MaxValue, new LocalDate(9999, 12, 31), ""),
                Extra = new Appointment(Instant.MinValue, new LocalDate(-9998, 1, 1), "min"),
                All = [],
            },
        };

        using var ms = new MemoryStream();
        await AgendaParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<Agenda> back = await AgendaParquetExtensions.ReadParquetAsync(ms);

        for (int i = 0; i < rows.Count; i++)
        {
            back[i].Main.ShouldBe(rows[i].Main);
            back[i].Extra.ShouldBe(rows[i].Extra);
            back[i].All.ShouldBe(rows[i].All);
        }
    }

    [Fact]
    public async Task NodaTimeTypesWhoseStorageNestsAGroupWorkAsListElements()
    {
        ZonedDateTime overlap = London.AtLeniently(new LocalDateTime(2021, 10, 31, 1, 30));
        var rows = new List<Timeline>
        {
            new()
            {
                Zoned =
                [
                    overlap,
                    London.MapLocal(new LocalDateTime(2021, 10, 31, 1, 30)).Last(),
                    NodaConstants.UnixEpoch.InUtc(),
                ],
                Windows =
                [
                    new Interval(null, NodaConstants.UnixEpoch),
                    new Interval(Instant.MinValue, Instant.MaxValue),
                ],
                Ranges =
                [
                    new DateInterval(new LocalDate(2024, 1, 1), new LocalDate(2024, 12, 31)),
                    null,
                ],
            },
            new()
            {
                Zoned = [],
                Windows = null,
                Ranges = [],
            },
        };

        using var ms = new MemoryStream();
        await TimelineParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<Timeline> back = await TimelineParquetExtensions.ReadParquetAsync(ms);

        for (int i = 0; i < rows.Count; i++)
        {
            back[i].Zoned.ShouldBe(rows[i].Zoned);
            back[i].Windows.ShouldBe(rows[i].Windows);
            back[i].Ranges.ShouldBe(rows[i].Ranges);
        }
    }
}

/// <summary>A custom type with converters inside: NodaTime fields beside ordinary ones.</summary>
[ParquetSerializable]
public sealed partial record Measurement
{
    public double Value { get; init; }
    public Instant At { get; init; }
    public LocalTime? Time { get; init; }
    public Period? Retention { get; init; }
}

[ParquetSerializable]
public partial record struct Stamp
{
    public Instant At { get; init; }
    public int Seq { get; init; }
}

[ParquetSerializable]
public sealed partial record SensorRow
{
    public Measurement Reading { get; init; } = new();
    public Measurement? Maybe { get; init; }
    public List<Measurement> History { get; init; } = [];
    public Measurement?[]? MaybeHistory { get; init; }
    public Stamp Stamp { get; init; }
    public List<Stamp> Stamps { get; init; } = [];
}

/// <summary>An application value whose own surrogate holds NodaTime fields.</summary>
public sealed record Appointment(Instant At, LocalDate Day, string Title);

public readonly record struct AppointmentStorage(Instant At, LocalDate Day, string Title);

[ParquetTypeAdapter(typeof(Appointment), typeof(AppointmentStorage))]
public static class AppointmentAdapter
{
    public static AppointmentStorage ToStorage(Appointment value) =>
        new(value.At, value.Day, value.Title);

    public static Appointment FromStorage(AppointmentStorage value) =>
        new(value.At, value.Day, value.Title);
}

[ParquetSerializable]
public sealed partial record Agenda
{
    [ParquetAdapter(typeof(AppointmentAdapter))]
    public Appointment Main { get; init; } = new(default, default, "");

    [ParquetAdapter(typeof(AppointmentAdapter))]
    public Appointment? Extra { get; init; }

    [ParquetAdapter(typeof(AppointmentAdapter))]
    public List<Appointment> All { get; init; } = [];
}

[ParquetSerializable]
public sealed partial record Timeline
{
    public List<ZonedDateTime> Zoned { get; init; } = [];
    public List<Interval>? Windows { get; init; }
    public DateInterval?[] Ranges { get; init; } = [];
}
