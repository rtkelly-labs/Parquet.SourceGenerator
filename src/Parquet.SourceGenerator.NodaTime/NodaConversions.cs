using System;
using NodaTime;
using Parquet.SourceGenerator.NodaTime.Storage;

namespace Parquet.SourceGenerator.NodaTime;

/// <summary>
/// The arithmetic every adapter shares: floored day/nanosecond splits and the proleptic ISO day
/// number. Kept in one place so there is exactly one canonical normalization.
/// </summary>
internal static class NodaConversions
{
    public const long NanosecondsPerDay = 86_400_000_000_000L;

    public static NodaInstantStorage ToStorage(Instant value)
    {
        (int days, long nanos) = Split(value - NodaConstants.UnixEpoch);
        return new NodaInstantStorage(days, nanos);
    }

    public static Instant ToInstant(NodaInstantStorage value) =>
        NodaConstants.UnixEpoch + Join(value.DaysSinceUnixEpoch, value.NanosecondOfDay);

    /// <summary>
    /// Splits a duration into floored whole days and a non-negative nanosecond remainder.
    /// NodaTime's own <see cref="Duration.Days"/> truncates toward zero, which would give
    /// pre-epoch values a negative nanosecond part — two representations for one moment.
    /// </summary>
    public static (int Days, long NanosecondOfDay) Split(Duration value)
    {
        int days = value.Days;
        long nanos = value.NanosecondOfDay;
        if (nanos < 0)
        {
            days--;
            nanos += NanosecondsPerDay;
        }

        return (days, nanos);
    }

    public static Duration Join(int days, long nanosecondOfDay)
    {
        if (nanosecondOfDay < 0 || nanosecondOfDay >= NanosecondsPerDay)
        {
            throw new FormatException(
                $"Stored nanosecond-of-day {nanosecondOfDay} is outside [0, {NanosecondsPerDay}); the value was not written by this adapter's normalization."
            );
        }

        return Duration.FromDays(days) + Duration.FromNanoseconds(nanosecondOfDay);
    }

    /// <summary>The proleptic ISO day number of a date in any calendar (days since 1970-01-01).</summary>
    public static int DayNumber(LocalDate date)
    {
        LocalDate iso =
            date.Calendar == CalendarSystem.Iso ? date : date.WithCalendar(CalendarSystem.Iso);
        return DaysFromCivil(iso.Year, iso.Month, iso.Day);
    }

    public static LocalDate ToLocalDate(int dayNumber, string calendarId)
    {
        (int year, int month, int day) = CivilFromDays(dayNumber);
        var iso = new LocalDate(year, month, day);
        return calendarId == CalendarSystem.Iso.Id ? iso : iso.WithCalendar(Calendar(calendarId));
    }

    public static CalendarSystem Calendar(string calendarId)
    {
        try
        {
            return CalendarSystem.ForId(calendarId);
        }
        catch (Exception ex)
            when (ex is ArgumentException or System.Collections.Generic.KeyNotFoundException)
        {
            throw new FormatException(
                $"Stored calendar id '{calendarId}' is not a calendar system NodaTime recognises.",
                ex
            );
        }
    }

    // Howard Hinnant's days_from_civil / civil_from_days: exact over the whole proleptic
    // Gregorian calendar, including year 0 and negative years, which is ISO as NodaTime models it.
    private static int DaysFromCivil(int year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        int era = (year >= 0 ? year : year - 399) / 400;
        int yearOfEra = year - (era * 400);
        int dayOfYear = (((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5) + day - 1;
        int dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (era * 146097) + dayOfEra - 719468;
    }

    private static (int Year, int Month, int Day) CivilFromDays(int days)
    {
        days += 719468;
        int era = (days >= 0 ? days : days - 146096) / 146097;
        int dayOfEra = days - (era * 146097);
        int yearOfEra =
            (dayOfEra - (dayOfEra / 1460) + (dayOfEra / 36524) - (dayOfEra / 146096)) / 365;
        int year = yearOfEra + (era * 400);
        int dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
        int monthIndex = ((5 * dayOfYear) + 2) / 153;
        int day = dayOfYear - (((153 * monthIndex) + 2) / 5) + 1;
        int month = monthIndex + (monthIndex < 10 ? 3 : -9);
        return (year + (month <= 2 ? 1 : 0), month, day);
    }
}
