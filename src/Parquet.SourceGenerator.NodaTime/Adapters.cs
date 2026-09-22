using System;
using NodaTime;
using Parquet.SourceGenerator.NodaTime.Storage;
using static Parquet.SourceGenerator.NodaTime.NodaConversions;

// Default adapters: lossless for every value NodaTime can represent, registered for the
// generator in AssemblyInfo.cs. Generated code calls these directly; application code normally
// never does. Nulls never reach them — the generator unwraps nullable members itself.

namespace Parquet.SourceGenerator.NodaTime.Adapters;

/// <summary>Lossless default for <see cref="Instant"/>: floored days plus nanosecond of day.</summary>
[ParquetTypeAdapter(typeof(Instant), typeof(NodaInstantStorage))]
public static class InstantAdapter
{
    /// <summary>Converts an instant to its storage form.</summary>
    public static NodaInstantStorage ToStorage(this Instant value) =>
        NodaConversions.ToStorage(value);

    /// <summary>Reconstructs an instant from its storage form.</summary>
    public static Instant FromStorage(this NodaInstantStorage value) => ToInstant(value);
}

/// <summary>Lossless default for <see cref="Duration"/>: floored days plus nanosecond remainder.</summary>
[ParquetTypeAdapter(typeof(Duration), typeof(NodaDurationStorage))]
public static class DurationAdapter
{
    /// <summary>Converts a duration to its storage form.</summary>
    public static NodaDurationStorage ToStorage(this Duration value)
    {
        (int days, long nanos) = Split(value);
        return new NodaDurationStorage(days, nanos);
    }

    /// <summary>Reconstructs a duration from its storage form.</summary>
    public static Duration FromStorage(this NodaDurationStorage value) =>
        Join(value.Days, value.NanosecondOfDay);
}

/// <summary>Lossless default for <see cref="LocalDate"/>: ISO day number plus calendar id.</summary>
[ParquetTypeAdapter(typeof(LocalDate), typeof(NodaLocalDateStorage))]
public static class LocalDateAdapter
{
    /// <summary>Converts a date to its storage form.</summary>
    public static NodaLocalDateStorage ToStorage(this LocalDate value) =>
        new(DayNumber(value), value.Calendar.Id);

    /// <summary>Reconstructs a date, in its original calendar, from its storage form.</summary>
    public static LocalDate FromStorage(this NodaLocalDateStorage value) =>
        ToLocalDate(value.DayNumber, value.CalendarId);
}

/// <summary>
/// Lossless default for <see cref="LocalTime"/>: nanoseconds since midnight, as a plain
/// <see cref="long"/> column.
/// </summary>
[ParquetTypeAdapter(typeof(LocalTime), typeof(long))]
public static class LocalTimeAdapter
{
    /// <summary>Converts a time of day to nanoseconds since midnight.</summary>
    public static long ToStorage(this LocalTime value) => value.NanosecondOfDay;

    /// <summary>Reconstructs a time of day from nanoseconds since midnight.</summary>
    public static LocalTime FromStorage(this long value)
    {
        if (value < 0 || value >= NanosecondsPerDay)
            throw new FormatException(
                $"Stored nanosecond-of-day {value} is outside [0, {NanosecondsPerDay})."
            );
        return LocalTime.FromNanosecondsSinceMidnight(value);
    }
}

/// <summary>
/// Lossless default for <see cref="LocalDateTime"/>. No time zone is involved in either
/// direction: a local date-time is stored as the local date and time it claims.
/// </summary>
[ParquetTypeAdapter(typeof(LocalDateTime), typeof(NodaLocalDateTimeStorage))]
public static class LocalDateTimeAdapter
{
    /// <summary>Converts a local date-time to its storage form.</summary>
    public static NodaLocalDateTimeStorage ToStorage(this LocalDateTime value) =>
        new(DayNumber(value.Date), value.TimeOfDay.NanosecondOfDay, value.Calendar.Id);

    /// <summary>Reconstructs a local date-time from its storage form.</summary>
    public static LocalDateTime FromStorage(this NodaLocalDateTimeStorage value) =>
        ToLocalDate(value.DayNumber, value.CalendarId)
            .At(LocalTimeAdapter.FromStorage(value.NanosecondOfDay));
}

/// <summary>Lossless default for <see cref="Offset"/>: signed seconds, as a plain <see cref="int"/> column.</summary>
[ParquetTypeAdapter(typeof(Offset), typeof(int))]
public static class OffsetAdapter
{
    /// <summary>Converts an offset to signed seconds.</summary>
    public static int ToStorage(this Offset value) => value.Seconds;

    /// <summary>Reconstructs an offset from signed seconds.</summary>
    public static Offset FromStorage(this int value) => Offset.FromSeconds(value);
}

/// <summary>Lossless default for <see cref="OffsetDateTime"/>: local date-time plus offset.</summary>
[ParquetTypeAdapter(typeof(OffsetDateTime), typeof(NodaOffsetDateTimeStorage))]
public static class OffsetDateTimeAdapter
{
    /// <summary>Converts an offset date-time to its storage form.</summary>
    public static NodaOffsetDateTimeStorage ToStorage(this OffsetDateTime value) =>
        new(
            DayNumber(value.Date),
            value.TimeOfDay.NanosecondOfDay,
            value.Calendar.Id,
            value.Offset.Seconds
        );

    /// <summary>Reconstructs an offset date-time from its storage form.</summary>
    public static OffsetDateTime FromStorage(this NodaOffsetDateTimeStorage value) =>
        new(
            ToLocalDate(value.DayNumber, value.CalendarId)
                .At(LocalTimeAdapter.FromStorage(value.NanosecondOfDay)),
            Offset.FromSeconds(value.OffsetSeconds)
        );
}

/// <summary>Lossless default for <see cref="OffsetDate"/>.</summary>
[ParquetTypeAdapter(typeof(OffsetDate), typeof(NodaOffsetDateStorage))]
public static class OffsetDateAdapter
{
    /// <summary>Converts an offset date to its storage form.</summary>
    public static NodaOffsetDateStorage ToStorage(this OffsetDate value) =>
        new(DayNumber(value.Date), value.Calendar.Id, value.Offset.Seconds);

    /// <summary>Reconstructs an offset date from its storage form.</summary>
    public static OffsetDate FromStorage(this NodaOffsetDateStorage value) =>
        new(
            ToLocalDate(value.DayNumber, value.CalendarId),
            Offset.FromSeconds(value.OffsetSeconds)
        );
}

/// <summary>Lossless default for <see cref="OffsetTime"/>.</summary>
[ParquetTypeAdapter(typeof(OffsetTime), typeof(NodaOffsetTimeStorage))]
public static class OffsetTimeAdapter
{
    /// <summary>Converts an offset time to its storage form.</summary>
    public static NodaOffsetTimeStorage ToStorage(this OffsetTime value) =>
        new(value.TimeOfDay.NanosecondOfDay, value.Offset.Seconds);

    /// <summary>Reconstructs an offset time from its storage form.</summary>
    public static OffsetTime FromStorage(this NodaOffsetTimeStorage value) =>
        new(
            LocalTimeAdapter.FromStorage(value.NanosecondOfDay),
            Offset.FromSeconds(value.OffsetSeconds)
        );
}

/// <summary>Default for <see cref="Interval"/>: each side optional, null meaning unbounded.</summary>
[ParquetTypeAdapter(typeof(Interval), typeof(NodaIntervalStorage))]
public static class IntervalAdapter
{
    /// <summary>Converts an interval to its storage form.</summary>
    public static NodaIntervalStorage ToStorage(this Interval value) =>
        new(
            value.HasStart ? NodaConversions.ToStorage(value.Start) : null,
            value.HasEnd ? NodaConversions.ToStorage(value.End) : null
        );

    /// <summary>Reconstructs an interval from its storage form.</summary>
    public static Interval FromStorage(this NodaIntervalStorage value) =>
        new(
            value.Start is { } start ? ToInstant(start) : null,
            value.End is { } end ? ToInstant(end) : null
        );
}

/// <summary>Default for <see cref="DateInterval"/>: both inclusive endpoints, calendar preserved.</summary>
[ParquetTypeAdapter(typeof(DateInterval), typeof(NodaDateIntervalStorage))]
public static class DateIntervalAdapter
{
    /// <summary>Converts a date interval to its storage form.</summary>
    public static NodaDateIntervalStorage ToStorage(this DateInterval value) =>
        new(value.Start.ToStorage(), value.End.ToStorage());

    /// <summary>Reconstructs a date interval from its storage form.</summary>
    public static DateInterval FromStorage(this NodaDateIntervalStorage value) =>
        new(value.Start.FromStorage(), value.End.FromStorage());
}

/// <summary>Lossless default for <see cref="YearMonth"/>.</summary>
[ParquetTypeAdapter(typeof(YearMonth), typeof(NodaYearMonthStorage))]
public static class YearMonthAdapter
{
    /// <summary>Converts a year-month to its storage form.</summary>
    public static NodaYearMonthStorage ToStorage(this YearMonth value) =>
        new(value.Year, value.Month, value.Calendar.Id);

    /// <summary>Reconstructs a year-month from its storage form.</summary>
    public static YearMonth FromStorage(this NodaYearMonthStorage value) =>
        new(value.Year, value.Month, Calendar(value.CalendarId));
}

/// <summary>Default for <see cref="AnnualDate"/>: month and day.</summary>
[ParquetTypeAdapter(typeof(AnnualDate), typeof(NodaAnnualDateStorage))]
public static class AnnualDateAdapter
{
    /// <summary>Converts an annual date to its storage form.</summary>
    public static NodaAnnualDateStorage ToStorage(this AnnualDate value) =>
        new(value.Month, value.Day);

    /// <summary>Reconstructs an annual date from its storage form.</summary>
    public static AnnualDate FromStorage(this NodaAnnualDateStorage value) =>
        new(value.Month, value.Day);
}

/// <summary>
/// Component-preserving default for <see cref="Period"/>. A period is never normalized into
/// elapsed time: its components are stored exactly as the value carries them.
/// </summary>
[ParquetTypeAdapter(typeof(Period), typeof(NodaPeriodStorage))]
public static class PeriodAdapter
{
    /// <summary>Converts a period to its storage form.</summary>
    public static NodaPeriodStorage ToStorage(this Period value) =>
        new(
            value.Years,
            value.Months,
            value.Weeks,
            value.Days,
            value.Hours,
            value.Minutes,
            value.Seconds,
            value.Milliseconds,
            value.Ticks,
            value.Nanoseconds
        );

    /// <summary>Reconstructs a period, components unchanged, from its storage form.</summary>
    public static Period FromStorage(this NodaPeriodStorage value) =>
        new PeriodBuilder
        {
            Years = value.Years,
            Months = value.Months,
            Weeks = value.Weeks,
            Days = value.Days,
            Hours = value.Hours,
            Minutes = value.Minutes,
            Seconds = value.Seconds,
            Milliseconds = value.Milliseconds,
            Ticks = value.Ticks,
            Nanoseconds = value.Nanoseconds,
        }.Build();
}

/// <summary>
/// Default for <see cref="ZonedDateTime"/>, resolving zones through
/// <see cref="DateTimeZoneProviders.Tzdb"/>.
/// </summary>
/// <remarks>
/// Fail-loud in both directions. Writing rejects a zone the TZDB provider cannot resolve by id
/// (a custom <see cref="DateTimeZone"/> could not be reconstructed). Reading re-derives the
/// zone's offset at the stored instant and throws when it differs from the offset observed at
/// write time — the installed time-zone data disagrees with the data the value was written
/// under, and silently shifting the local time would be data corruption.
/// </remarks>
[ParquetTypeAdapter(typeof(ZonedDateTime), typeof(NodaZonedDateTimeStorage))]
public static class ZonedDateTimeAdapter
{
    /// <summary>Converts a zoned date-time to its storage form.</summary>
    public static NodaZonedDateTimeStorage ToStorage(this ZonedDateTime value)
    {
        string zoneId = value.Zone.Id;
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId) is null)
        {
            throw new ArgumentException(
                $"Time zone '{zoneId}' is not resolvable through DateTimeZoneProviders.Tzdb, so a stored value could not be reconstructed.",
                nameof(value)
            );
        }

        return new NodaZonedDateTimeStorage(
            NodaConversions.ToStorage(value.ToInstant()),
            zoneId,
            value.Calendar.Id,
            value.Offset.Seconds
        );
    }

    /// <summary>Reconstructs a zoned date-time, validating the observed offset.</summary>
    public static ZonedDateTime FromStorage(this NodaZonedDateTimeStorage value)
    {
        DateTimeZone zone =
            DateTimeZoneProviders.Tzdb.GetZoneOrNull(value.ZoneId)
            ?? throw new FormatException(
                $"Stored time zone '{value.ZoneId}' is not present in the installed TZDB data ({DateTimeZoneProviders.Tzdb.VersionId})."
            );

        Instant instant = ToInstant(value.Instant);
        int actual = zone.GetUtcOffset(instant).Seconds;
        if (actual != value.ObservedOffsetSeconds)
        {
            throw new FormatException(
                $"Stored value in zone '{value.ZoneId}' was written with offset {value.ObservedOffsetSeconds}s, but the installed TZDB data ({DateTimeZoneProviders.Tzdb.VersionId}) gives {actual}s at that instant. The zone rules differ from those the value was written under."
            );
        }

        return new ZonedDateTime(instant, zone, Calendar(value.CalendarId));
    }
}
