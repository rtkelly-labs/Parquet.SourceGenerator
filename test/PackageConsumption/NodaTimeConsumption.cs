using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NodaTime;
using Parquet.SourceGenerator;

namespace PackageConsumption;

[ParquetSerializable]
public sealed partial record Appointment
{
    public Instant BookedAt { get; init; }
    public LocalDate Day { get; init; }
    public ZonedDateTime? StartsAt { get; init; }
    public Period Length { get; init; } = Period.Zero;
}

internal static class NodaTimeConsumption
{
    public static async Task<bool> RoundTripAsync()
    {
        var expected = new List<Appointment>
        {
            new()
            {
                BookedAt = Instant.FromUtc(2024, 5, 1, 9, 30) + Duration.FromNanoseconds(1),
                Day = new LocalDate(2024, 5, 2),
                StartsAt = new LocalDateTime(2024, 5, 2, 14, 0).InZoneLeniently(
                    DateTimeZoneProviders.Tzdb["Europe/London"]
                ),
                Length = Period.FromMinutes(90),
            },
            new()
            {
                BookedAt = Instant.MinValue,
                Day = new LocalDate(1582, 10, 15).WithCalendar(CalendarSystem.Julian),
                StartsAt = null,
                Length = Period.Zero,
            },
        };

        using var stream = new MemoryStream();
        await AppointmentParquetExtensions.WriteParquetAsync(expected, stream);
        stream.Position = 0;
        List<Appointment> actual = await AppointmentParquetExtensions.ReadParquetAsync(stream);

        for (int i = 0; i < expected.Count; i++)
        {
            if (actual.Count != expected.Count || !actual[i].Equals(expected[i]))
            {
                Console.Error.WriteLine($"FAILED: NodaTime record {i} did not round-trip.");
                return false;
            }
        }

        return true;
    }
}
