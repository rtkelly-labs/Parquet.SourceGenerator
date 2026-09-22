using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NodaTime;
using Parquet.SourceGenerator.NodaTime.Adapters;

[assembly: Parquet.SourceGenerator.ParquetTypeAdapter(
    typeof(Parquet.SourceGenerator.AotTest.AotIdAdapter)
)]

namespace Parquet.SourceGenerator.AotTest;

/// <summary>
/// Adapted members read and write through generated storage shadows that call the adapters'
/// static methods directly (docs/44-TYPE-ADAPTERS.md). Nothing is looked up at runtime, so this
/// must work unchanged in a native binary: scalar surrogates, group surrogates, nested groups,
/// nullable members, the TZDB-backed zone adapter and an explicit interop adapter.
/// </summary>
[ParquetSerializable]
public sealed partial record AotNodaRecord
{
    public Instant At { get; init; }
    public Instant? MaybeAt { get; init; }
    public LocalDate Day { get; init; }
    public LocalTime Time { get; init; }
    public Offset? Off { get; init; }
    public Interval Window { get; init; }
    public Period Retention { get; init; } = Period.Zero;
    public ZonedDateTime Zoned { get; init; }

    [ParquetAdapter(typeof(InstantAsDateTimeMicrosecondsAdapter))]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public Instant EventTime { get; init; }

    // Adapted collection elements, converted inline per element.
    public List<Instant> History { get; init; } = [];
    public LocalTime?[]? Slots { get; init; }

    // A generic adapter closed over two different constructions.
    public AotId<AotNodaRecord> Key { get; init; }
    public List<AotId<string>> Links { get; init; } = [];

    public bool Equals(AotNodaRecord? other) =>
        other is not null
        && At == other.At
        && MaybeAt == other.MaybeAt
        && Day == other.Day
        && Time == other.Time
        && Off == other.Off
        && Window == other.Window
        && Retention.Equals(other.Retention)
        && Zoned == other.Zoned
        && EventTime == other.EventTime
        && History.SequenceEqual(other.History)
        && (
            Slots is null
                ? other.Slots is null
                : other.Slots is not null && Slots.SequenceEqual(other.Slots)
        )
        && Key == other.Key
        && Links.SequenceEqual(other.Links);

    public override int GetHashCode() => At.GetHashCode();
}

public readonly record struct AotId<T>(Guid Value);

[ParquetTypeAdapter(typeof(AotId<>), typeof(Guid))]
public static class AotIdAdapter
{
    public static Guid ToStorage<T>(AotId<T> value) => value.Value;

    public static AotId<T> FromStorage<T>(Guid value) => new(value);
}

internal static class NodaTimeChecks
{
    public static async Task RoundTripAsync()
    {
        DateTimeZone london = DateTimeZoneProviders.Tzdb["Europe/London"];
        var rows = new List<AotNodaRecord>
        {
            new()
            {
                At = Instant.MinValue,
                MaybeAt = NodaConstants.UnixEpoch - Duration.FromNanoseconds(1),
                Day = new LocalDate(2024, 2, 29).WithCalendar(CalendarSystem.Julian),
                Time = LocalTime.MaxValue,
                Off = Offset.FromHoursAndMinutes(5, 45),
                History = [Instant.MinValue, NodaConstants.UnixEpoch],
                Slots = [LocalTime.Noon, null],
                Key = new AotId<AotNodaRecord>(Guid.NewGuid()),
                Links = [new AotId<string>(Guid.NewGuid())],
                Window = new Interval(NodaConstants.UnixEpoch, null),
                Retention = Period.FromMinutes(90),
                Zoned = london.AtLeniently(new LocalDateTime(2021, 10, 31, 1, 30)),
                EventTime = Instant.FromUtc(2024, 1, 2, 3, 4, 5) + Duration.FromTicks(10),
            },
            new()
            {
                At = Instant.MaxValue,
                MaybeAt = null,
                Day = new LocalDate(-9998, 1, 1),
                Time = LocalTime.Midnight,
                Off = null,
                Window = new Interval(null, null),
                Retention = Period.Zero,
                Zoned = NodaConstants.UnixEpoch.InUtc(),
                EventTime = NodaConstants.UnixEpoch,
            },
        };

        using var stream = new MemoryStream();
        await AotNodaRecordParquetExtensions.WriteParquetAsync(rows, stream);
        stream.Position = 0;
        List<AotNodaRecord> back = await AotNodaRecordParquetExtensions.ReadParquetAsync(stream);

        if (back.Count != rows.Count)
            throw new InvalidOperationException($"Expected {rows.Count} rows, read {back.Count}.");

        for (int i = 0; i < rows.Count; i++)
        {
            if (!back[i].Equals(rows[i]))
                throw new InvalidOperationException(
                    $"Row {i} differs after round trip: {back[i]} vs {rows[i]}."
                );
        }
    }
}
