using System;
using NodaTime;

// Explicit native-interoperability adapters. Each maps to a type the generator writes as a
// native Parquet column, which ecosystem readers understand without knowing NodaTime — at the
// price of a narrower value domain. None is registered as a default: a member opts in with
// [ParquetAdapter(typeof(...))], and every one of them throws rather than silently truncating,
// shifting or re-calendaring a value that does not fit. They are plain static methods rather
// than extensions so they never compete with the defaults' ToStorage() at a call site.

namespace Parquet.SourceGenerator.NodaTime.Adapters;

/// <summary>
/// Explicit interop adapter: <see cref="Instant"/> as a UTC <see cref="DateTime"/> column.
/// </summary>
/// <remarks>
/// Accepts instants in <see cref="DateTime"/>'s range (years 1–9999) that are whole 100 ns ticks.
/// The generator's default <see cref="DateTime"/> column encoding keeps full tick precision; if
/// the member also carries <c>[ParquetTimestamp(Microseconds)]</c>, use
/// <see cref="InstantAsDateTimeMicrosecondsAdapter"/> so sub-microsecond values are rejected
/// instead of truncated by the column.
/// </remarks>
[ParquetTypeAdapter(typeof(Instant), typeof(DateTime))]
public static class InstantAsDateTimeAdapter
{
    /// <summary>Converts an instant to a UTC <see cref="DateTime"/>, rejecting any loss.</summary>
    public static DateTime ToStorage(Instant value) =>
        Interop.ToUtcDateTime(value, Interop.TicksPerTick);

    /// <summary>Reconstructs an instant from a UTC (or unspecified-kind) <see cref="DateTime"/>.</summary>
    public static Instant FromStorage(DateTime value) => Interop.FromUtcDateTime(value);
}

/// <summary>
/// Explicit interop adapter: <see cref="Instant"/> as a microsecond-precision UTC timestamp —
/// the representation Spark, DuckDB and PyArrow read as a native timestamp. Pair it with
/// <c>[ParquetTimestamp(ParquetTimestampUnit.Microseconds)]</c> on the member.
/// </summary>
[ParquetTypeAdapter(typeof(Instant), typeof(DateTime))]
public static class InstantAsDateTimeMicrosecondsAdapter
{
    /// <summary>Converts an instant, rejecting sub-microsecond values and out-of-range years.</summary>
    public static DateTime ToStorage(Instant value) =>
        Interop.ToUtcDateTime(value, Interop.TicksPerMicrosecond);

    /// <summary>Reconstructs an instant from a UTC (or unspecified-kind) <see cref="DateTime"/>.</summary>
    public static Instant FromStorage(DateTime value) => Interop.FromUtcDateTime(value);
}

/// <summary>
/// Explicit interop adapter: <see cref="Instant"/> as signed nanoseconds since the Unix epoch in a
/// plain <see cref="long"/> column. Exact, but only for instants between 1677-09-21 and
/// 2262-04-11; anything outside that range throws.
/// </summary>
[ParquetTypeAdapter(typeof(Instant), typeof(long))]
public static class InstantAsUnixNanosecondsAdapter
{
    /// <summary>Converts an instant to Unix nanoseconds, rejecting values outside the int64 range.</summary>
    public static long ToStorage(Instant value)
    {
        (int days, long nanos) = NodaConversions.Split(value - NodaConstants.UnixEpoch);
        try
        {
            return checked((days * NodaConversions.NanosecondsPerDay) + nanos);
        }
        catch (OverflowException ex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Instant is outside the signed 64-bit nanosecond range (1677-09-21 to 2262-04-11). Use the default InstantAdapter for the full NodaTime range. "
                    + ex.Message
            );
        }
    }

    /// <summary>Reconstructs an instant from Unix nanoseconds.</summary>
    public static Instant FromStorage(long value) =>
        NodaConstants.UnixEpoch + Duration.FromNanoseconds(value);
}

/// <summary>
/// Explicit interop adapter: an ISO <see cref="LocalDate"/> as a <see cref="DateTime"/> at
/// midnight (unspecified kind). Non-ISO dates and years outside 1–9999 throw.
/// </summary>
[ParquetTypeAdapter(typeof(LocalDate), typeof(DateTime))]
public static class IsoLocalDateAsDateTimeAdapter
{
    /// <summary>Converts an ISO date to midnight of that date.</summary>
    public static DateTime ToStorage(LocalDate value)
    {
        Interop.RequireIso(value.Calendar, nameof(value));
        Interop.RequireDateTimeYear(value.Year, nameof(value));
        return new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Unspecified);
    }

    /// <summary>Reconstructs the ISO date; a stored value with a time component throws.</summary>
    public static LocalDate FromStorage(DateTime value)
    {
        if (value.TimeOfDay != TimeSpan.Zero)
            throw new FormatException(
                $"Stored date {value:O} carries a time of day; it was not written as a LocalDate."
            );
        return new LocalDate(value.Year, value.Month, value.Day);
    }
}

/// <summary>
/// Explicit interop adapter: an ISO <see cref="LocalDateTime"/> as a <see cref="DateTime"/> of
/// unspecified kind. No time-zone conversion happens in either direction — the stored value is
/// the local wall-clock reading. Non-ISO values, years outside 1–9999 and sub-tick values throw.
/// </summary>
[ParquetTypeAdapter(typeof(LocalDateTime), typeof(DateTime))]
public static class IsoLocalDateTimeAsDateTimeAdapter
{
    /// <summary>Converts an ISO local date-time to an unspecified-kind <see cref="DateTime"/>.</summary>
    public static DateTime ToStorage(LocalDateTime value)
    {
        Interop.RequireIso(value.Calendar, nameof(value));
        Interop.RequireDateTimeYear(value.Year, nameof(value));
        if (value.NanosecondOfSecond % 100 != 0)
        {
            throw new ArgumentException(
                $"Local date-time {value:uuuu-MM-ddTHH:mm:ss.fffffffff} is not a whole number of 100 ns ticks and cannot be stored as a DateTime without truncation.",
                nameof(value)
            );
        }

        return value.ToDateTimeUnspecified();
    }

    /// <summary>Reconstructs the ISO local date-time. The stored kind is ignored, not converted.</summary>
    public static LocalDateTime FromStorage(DateTime value) =>
        LocalDateTime.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified));
}

#if NET6_0_OR_GREATER
/// <summary>
/// Explicit interop adapter: an ISO <see cref="LocalDate"/> as a <see cref="DateOnly"/> column.
/// Non-ISO dates and years outside 1–9999 throw.
/// </summary>
[ParquetTypeAdapter(typeof(LocalDate), typeof(DateOnly))]
public static class IsoLocalDateAsDateOnlyAdapter
{
    /// <summary>Converts an ISO date to a <see cref="DateOnly"/>.</summary>
    public static DateOnly ToStorage(LocalDate value)
    {
        Interop.RequireIso(value.Calendar, nameof(value));
        Interop.RequireDateTimeYear(value.Year, nameof(value));
        return new DateOnly(value.Year, value.Month, value.Day);
    }

    /// <summary>Reconstructs the ISO date.</summary>
    public static LocalDate FromStorage(DateOnly value) => new(value.Year, value.Month, value.Day);
}

/// <summary>
/// Explicit interop adapter: <see cref="LocalTime"/> as a <see cref="TimeOnly"/> column, which
/// the generator writes as a microsecond-precision Parquet TIME. Sub-microsecond values throw
/// rather than being truncated by the column.
/// </summary>
[ParquetTypeAdapter(typeof(LocalTime), typeof(TimeOnly))]
public static class LocalTimeAsTimeOnlyAdapter
{
    /// <summary>Converts a time of day to a <see cref="TimeOnly"/>, rejecting sub-microsecond values.</summary>
    public static TimeOnly ToStorage(LocalTime value)
    {
        if (value.NanosecondOfDay % 1_000 != 0)
        {
            throw new ArgumentException(
                $"Time {value:HH:mm:ss.fffffffff} is not a whole number of microseconds and cannot be stored in a microsecond TIME column without truncation.",
                nameof(value)
            );
        }

        return new TimeOnly(value.NanosecondOfDay / 100);
    }

    /// <summary>Reconstructs the time of day.</summary>
    public static LocalTime FromStorage(TimeOnly value) =>
        LocalTime.FromNanosecondsSinceMidnight(value.Ticks * 100);
}
#endif

/// <summary>Validation shared by the interop adapters.</summary>
internal static class Interop
{
    public const long TicksPerTick = 1;
    public const long TicksPerMicrosecond = 10;

    public static DateTime ToUtcDateTime(Instant value, long requiredTickMultiple)
    {
        (_, long nanos) = NodaConversions.Split(value - NodaConstants.UnixEpoch);
        if (nanos % (requiredTickMultiple * 100) != 0)
        {
            throw new ArgumentException(
                $"Instant {value} has precision finer than {(requiredTickMultiple == 1 ? "a 100 ns tick" : "a microsecond")} and cannot be stored in this column without truncation. Use the default InstantAdapter for nanosecond precision.",
                nameof(value)
            );
        }

        LocalDate date = value.InUtc().Date;
        RequireDateTimeYear(date.Year, nameof(value));
        return value.ToDateTimeUtc();
    }

    public static Instant FromUtcDateTime(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => Instant.FromDateTimeUtc(value),
            DateTimeKind.Unspecified => Instant.FromDateTimeUtc(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)
            ),
            _ => throw new FormatException(
                "Stored timestamp has local kind; an instant column must be read as UTC."
            ),
        };

    public static void RequireIso(CalendarSystem calendar, string parameter)
    {
        if (calendar != CalendarSystem.Iso)
        {
            throw new ArgumentException(
                $"Calendar '{calendar.Id}' is not ISO; this interop adapter stores ISO values only. Use the default adapter to keep the calendar.",
                parameter
            );
        }
    }

    public static void RequireDateTimeYear(int year, string parameter)
    {
        if (year < 1 || year > 9999)
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                year,
                "Year is outside the 1–9999 range a DateTime column can hold. Use the default adapter for the full NodaTime range."
            );
        }
    }
}
