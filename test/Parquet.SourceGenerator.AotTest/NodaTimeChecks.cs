using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NodaTime;
using Parquet.SourceGenerator.NodaTime.Adapters;

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
    public Offset? Offset { get; init; }
    public Interval Window { get; init; }
    public Period Retention { get; init; } = Period.Zero;
    public ZonedDateTime Zoned { get; init; }

    [ParquetAdapter(typeof(InstantAsDateTimeMicrosecondsAdapter))]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public Instant EventTime { get; init; }
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
                Offset = global::NodaTime.Offset.FromHoursAndMinutes(5, 45),
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
                Offset = null,
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
