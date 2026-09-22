using System.Runtime.InteropServices;
using Parquet.SourceGenerator;

// The storage records are persisted data formats, not API conveniences: a field's name, type or
// meaning changes only with a data-format version decision (docs/45-NODATIME.md §Versioning).
// Column names are snake_case so the Parquet group reads naturally from Spark, DuckDB and PyArrow.

namespace Parquet.SourceGenerator.NodaTime.Storage;

/// <summary>
/// Lossless storage for <see cref="global::NodaTime.Instant"/> and the instant inside compound
/// values: whole days since the Unix epoch plus the nanosecond within that day.
/// </summary>
/// <remarks>
/// Floored, not truncated: a pre-epoch instant has a negative day count and a non-negative
/// <see cref="NanosecondOfDay"/> (<c>0 &lt;= NanosecondOfDay &lt; 86,400,000,000,000</c>), so
/// every instant has exactly one representation. A single signed 64-bit nanosecond count would
/// only cover 1677–2262; NodaTime's range is years -9998 to 9999.
/// </remarks>
/// <param name="DaysSinceUnixEpoch">Whole days since 1970-01-01T00:00:00Z, floored.</param>
/// <param name="NanosecondOfDay">Nanoseconds into that day.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaInstantStorage(
    [property: ParquetColumn("days_since_unix_epoch")] int DaysSinceUnixEpoch,
    [property: ParquetColumn("nanosecond_of_day")] long NanosecondOfDay
);

/// <summary>
/// Lossless storage for <see cref="global::NodaTime.Duration"/>: whole standard days plus the
/// nanosecond remainder, floored exactly like <see cref="NodaInstantStorage"/>.
/// </summary>
/// <param name="Days">Whole 24-hour days, floored (negative durations have negative days).</param>
/// <param name="NanosecondOfDay">The non-negative remainder in nanoseconds.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaDurationStorage(
    [property: ParquetColumn("days")] int Days,
    [property: ParquetColumn("nanosecond_of_day")] long NanosecondOfDay
);

/// <summary>
/// Lossless storage for <see cref="global::NodaTime.LocalDate"/>.
/// </summary>
/// <remarks>
/// <see cref="DayNumber"/> is calendar-independent — the proleptic ISO day, so two dates in
/// different calendars that denote the same day share it — and <see cref="CalendarId"/> carries
/// the calendar the value was expressed in (<see cref="global::NodaTime.CalendarSystem.Id"/>).
/// </remarks>
/// <param name="DayNumber">Days since ISO 1970-01-01 (negative before it).</param>
/// <param name="CalendarId">The value's calendar system identifier, e.g. <c>ISO</c>.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaLocalDateStorage(
    [property: ParquetColumn("day_number")] int DayNumber,
    [property: ParquetColumn("calendar_id")] string CalendarId
);

/// <summary>
/// Lossless storage for <see cref="global::NodaTime.LocalDateTime"/>: a calendar date and a
/// time of day. Deliberately carries no zone or offset — a local date-time is not an instant.
/// </summary>
/// <param name="DayNumber">Days since ISO 1970-01-01.</param>
/// <param name="NanosecondOfDay">Nanoseconds since local midnight.</param>
/// <param name="CalendarId">The value's calendar system identifier.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaLocalDateTimeStorage(
    [property: ParquetColumn("day_number")] int DayNumber,
    [property: ParquetColumn("nanosecond_of_day")] long NanosecondOfDay,
    [property: ParquetColumn("calendar_id")] string CalendarId
);

/// <summary>
/// Lossless storage for <see cref="global::NodaTime.OffsetDateTime"/>: the local date-time it
/// claims plus its offset, rather than a reduction to an instant.
/// </summary>
/// <param name="DayNumber">Days since ISO 1970-01-01, of the local date.</param>
/// <param name="NanosecondOfDay">Nanoseconds since local midnight.</param>
/// <param name="CalendarId">The value's calendar system identifier.</param>
/// <param name="OffsetSeconds">The offset from UTC, in seconds.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaOffsetDateTimeStorage(
    [property: ParquetColumn("day_number")] int DayNumber,
    [property: ParquetColumn("nanosecond_of_day")] long NanosecondOfDay,
    [property: ParquetColumn("calendar_id")] string CalendarId,
    [property: ParquetColumn("offset_seconds")] int OffsetSeconds
);

/// <summary>Lossless storage for <see cref="global::NodaTime.OffsetDate"/>.</summary>
/// <param name="DayNumber">Days since ISO 1970-01-01.</param>
/// <param name="CalendarId">The value's calendar system identifier.</param>
/// <param name="OffsetSeconds">The offset from UTC, in seconds.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaOffsetDateStorage(
    [property: ParquetColumn("day_number")] int DayNumber,
    [property: ParquetColumn("calendar_id")] string CalendarId,
    [property: ParquetColumn("offset_seconds")] int OffsetSeconds
);

/// <summary>Lossless storage for <see cref="global::NodaTime.OffsetTime"/>.</summary>
/// <param name="NanosecondOfDay">Nanoseconds since local midnight.</param>
/// <param name="OffsetSeconds">The offset from UTC, in seconds.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaOffsetTimeStorage(
    [property: ParquetColumn("nanosecond_of_day")] long NanosecondOfDay,
    [property: ParquetColumn("offset_seconds")] int OffsetSeconds
);

/// <summary>
/// Lossless storage for <see cref="global::NodaTime.ZonedDateTime"/>.
/// </summary>
/// <remarks>
/// The observed offset is persisted so reconstruction can fail loudly when the time-zone data
/// installed at read time would place the same instant at a different local time than it had
/// when written.
/// </remarks>
/// <param name="Instant">The instant, in <see cref="NodaInstantStorage"/> form.</param>
/// <param name="ZoneId">The TZDB zone identifier.</param>
/// <param name="CalendarId">The value's calendar system identifier.</param>
/// <param name="ObservedOffsetSeconds">The zone's offset at that instant when written.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaZonedDateTimeStorage(
    [property: ParquetColumn("instant")] NodaInstantStorage Instant,
    [property: ParquetColumn("zone_id")] string ZoneId,
    [property: ParquetColumn("calendar_id")] string CalendarId,
    [property: ParquetColumn("observed_offset_seconds")] int ObservedOffsetSeconds
);

/// <summary>
/// Storage for <see cref="global::NodaTime.Interval"/>. A null side is an unbounded side.
/// </summary>
/// <param name="Start">The inclusive start, or null when the interval has no start.</param>
/// <param name="End">The exclusive end, or null when the interval has no end.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaIntervalStorage(
    [property: ParquetColumn("start")] NodaInstantStorage? Start,
    [property: ParquetColumn("end")] NodaInstantStorage? End
);

/// <summary>
/// Storage for <see cref="global::NodaTime.DateInterval"/>: both inclusive endpoints through the
/// lossless date form, so calendar identity survives.
/// </summary>
/// <param name="Start">The inclusive start date.</param>
/// <param name="End">The inclusive end date.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaDateIntervalStorage(
    [property: ParquetColumn("start")] NodaLocalDateStorage Start,
    [property: ParquetColumn("end")] NodaLocalDateStorage End
);

/// <summary>Lossless storage for <see cref="global::NodaTime.YearMonth"/>.</summary>
/// <param name="Year">The absolute year in the value's calendar.</param>
/// <param name="Month">The month of year, 1-based.</param>
/// <param name="CalendarId">The value's calendar system identifier.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaYearMonthStorage(
    [property: ParquetColumn("year")] int Year,
    [property: ParquetColumn("month")] int Month,
    [property: ParquetColumn("calendar_id")] string CalendarId
);

/// <summary>Storage for <see cref="global::NodaTime.AnnualDate"/> (always ISO).</summary>
/// <param name="Month">The month of year, 1-based.</param>
/// <param name="Day">The day of month.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaAnnualDateStorage(
    [property: ParquetColumn("month")] int Month,
    [property: ParquetColumn("day")] int Day
);

/// <summary>
/// Component-preserving storage for <see cref="global::NodaTime.Period"/>. Never normalized:
/// "90 minutes" and "1 hour 30 minutes" are different periods and stay different.
/// </summary>
/// <param name="Years">Years component.</param>
/// <param name="Months">Months component.</param>
/// <param name="Weeks">Weeks component.</param>
/// <param name="Days">Days component.</param>
/// <param name="Hours">Hours component.</param>
/// <param name="Minutes">Minutes component.</param>
/// <param name="Seconds">Seconds component.</param>
/// <param name="Milliseconds">Milliseconds component.</param>
/// <param name="Ticks">Ticks component.</param>
/// <param name="Nanoseconds">Nanoseconds component.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NodaPeriodStorage(
    [property: ParquetColumn("years")] int Years,
    [property: ParquetColumn("months")] int Months,
    [property: ParquetColumn("weeks")] int Weeks,
    [property: ParquetColumn("days")] int Days,
    [property: ParquetColumn("hours")] long Hours,
    [property: ParquetColumn("minutes")] long Minutes,
    [property: ParquetColumn("seconds")] long Seconds,
    [property: ParquetColumn("milliseconds")] long Milliseconds,
    [property: ParquetColumn("ticks")] long Ticks,
    [property: ParquetColumn("nanoseconds")] long Nanoseconds
);
