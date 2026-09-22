using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NodaTime;
using NodaTime.TimeZones;
using Parquet.Schema;
using Parquet.SourceGenerator.NodaTime.Adapters;
using Parquet.SourceGenerator.NodaTime.Storage;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The NodaTime adapter package end to end (docs/45-NODATIME.md): this assembly references
/// <c>Parquet.SourceGenerator.NodaTime</c> the way a consumer does, the generator discovers the
/// package's registrations at compile time, and the models below round-trip through the real
/// emitted code. The vectors are the cross-backend conformance set from the design (§23).
/// </summary>
public sealed class NodaTimeAdapterRoundTripTests
{
    private static readonly DateTimeZone London = DateTimeZoneProviders.Tzdb["Europe/London"];

    private static readonly CalendarSystem Julian = CalendarSystem.Julian;

    private static readonly CalendarSystem Coptic = CalendarSystem.Coptic;

    public static IEnumerable<object[]> InstantVectors() =>
        new[]
        {
            Instant.MinValue,
            Instant.MaxValue,
            NodaConstants.UnixEpoch,
            NodaConstants.UnixEpoch - Duration.FromNanoseconds(1),
            NodaConstants.UnixEpoch - Duration.FromDays(1) + Duration.FromNanoseconds(1),
            Instant.FromUtc(2024, 2, 29, 23, 59, 59) + Duration.FromNanoseconds(999_999_999),
            Instant.FromUtc(-9998, 1, 1, 0, 0) + Duration.FromNanoseconds(123),
        }.Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(InstantVectors))]
    public void InstantStorageIsFlooredAndRoundTrips(Instant value)
    {
        NodaInstantStorage storage = value.ToStorage();

        storage.NanosecondOfDay.ShouldBeGreaterThanOrEqualTo(0);
        storage.NanosecondOfDay.ShouldBeLessThan(86_400_000_000_000L);
        storage.FromStorage().ShouldBe(value);
    }

    [Fact]
    public void PreEpochFractionalInstantHasOneCanonicalForm()
    {
        Instant value = NodaConstants.UnixEpoch - Duration.FromNanoseconds(1);

        value.ToStorage().ShouldBe(new NodaInstantStorage(-1, 86_399_999_999_999L));
    }

    [Fact]
    public void NegativeDurationIsFloored()
    {
        Duration value = -Duration.FromHours(1);

        NodaDurationStorage storage = value.ToStorage();

        storage.ShouldBe(new NodaDurationStorage(-1, 23L * 3_600_000_000_000L));
        storage.FromStorage().ShouldBe(value);
    }

    [Fact]
    public void LocalDateDayNumberIsCalendarIndependent()
    {
        var iso = new LocalDate(2024, 3, 1);
        LocalDate julian = iso.WithCalendar(Julian);

        iso.ToStorage().DayNumber.ShouldBe(19783);
        julian.ToStorage().DayNumber.ShouldBe(19783);
        julian.ToStorage().CalendarId.ShouldBe(Julian.Id);
        julian.ToStorage().FromStorage().ShouldBe(julian);
        new LocalDate(-9998, 1, 1).ToStorage().FromStorage().ShouldBe(new LocalDate(-9998, 1, 1));
        new LocalDate(0, 2, 29).ToStorage().FromStorage().ShouldBe(new LocalDate(0, 2, 29));
    }

    [Fact]
    public void PeriodIsNotNormalized()
    {
        Period ninetyMinutes = Period.FromMinutes(90);

        Period back = ninetyMinutes.ToStorage().FromStorage();

        back.ShouldBe(ninetyMinutes);
        back.Hours.ShouldBe(0);
        back.Minutes.ShouldBe(90);
    }

    [Fact]
    public void ZonedDateTimeReadFailsLoudlyWhenTheObservedOffsetDisagrees()
    {
        ZonedDateTime value = Instant.FromUtc(2021, 7, 1, 12, 0).InZone(London);
        NodaZonedDateTimeStorage storage = value.ToStorage() with { ObservedOffsetSeconds = 0 };

        Should.Throw<FormatException>(() => storage.FromStorage()).Message.ShouldContain("TZDB");
    }

    [Fact]
    public void ZonedDateTimeRejectsAZoneTzdbCannotResolve()
    {
        var custom = new ZonedDateTime(NodaConstants.UnixEpoch, new FixedCustomZone());

        Should.Throw<ArgumentException>(() => custom.ToStorage());
    }

    [Fact]
    public async Task EveryDefaultAdapterRoundTripsThroughGeneratedCode()
    {
        List<NodaEvent> rows = NodaEventVectors().ToList();

        using var ms = new MemoryStream();
        await NodaEventParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<NodaEvent> back = await NodaEventParquetExtensions.ReadParquetAsync(ms);

        back.Count.ShouldBe(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            back[i].ShouldBe(rows[i], $"row {i}");
    }

    [Fact]
    public async Task AdaptedMembersKeepTheirColumnNamesAndLosslessGroups()
    {
        using var ms = new MemoryStream();
        await NodaEventParquetExtensions.WriteParquetAsync(NodaEventVectors().Take(1).ToList(), ms);
        ms.Position = 0;

        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        ParquetSchema schema = reader.Schema;

        var at = (StructField)schema.Fields.Single(f => f.Name == "At");
        at.Fields.Select(f => f.Name).ShouldBe(["days_since_unix_epoch", "nanosecond_of_day"]);
        ((DataField)at.Fields[0]).ClrType.ShouldBe(typeof(int));
        ((DataField)at.Fields[1]).ClrType.ShouldBe(typeof(long));

        var time = (DataField)schema.Fields.Single(f => f.Name == "Time");
        time.ClrType.ShouldBe(typeof(long));
        time.IsNullable.ShouldBeFalse();

        schema
            .Fields.Single(f => f.Name == "MaybeOffset")
            .ShouldBeOfType<DataField>()
            .IsNullable.ShouldBeTrue();
        schema.Fields.ShouldNotContain(f =>
            f.Name.EndsWith("ParquetStorage", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ExplicitInteropAdaptersWriteNativeColumnsAndRoundTrip()
    {
        var rows = new List<NodaInteropRow>
        {
            new()
            {
                Ticks = Instant.FromUtc(2024, 5, 6, 7, 8, 9) + Duration.FromTicks(1234567),
                Micros = Instant.FromUtc(1999, 12, 31, 23, 59, 59) + Duration.FromTicks(9_999_990),
                Nanos = Instant.FromUtc(2262, 4, 11, 0, 0) + Duration.FromNanoseconds(7),
                Date = new LocalDate(1, 1, 1),
                Local = new LocalDateTime(9999, 12, 31, 23, 59, 59).PlusTicks(9_999_999),
                Time = new LocalTime(23, 59, 59).PlusNanoseconds(999_999_000),
            },
            new()
            {
                Ticks = NodaConstants.UnixEpoch,
                Micros = NodaConstants.UnixEpoch - Duration.FromTicks(10),
                Nanos = null,
                Date = new LocalDate(2024, 2, 29),
                Local = new LocalDateTime(1970, 1, 1, 0, 0),
                Time = LocalTime.Midnight,
            },
        };

        using var ms = new MemoryStream();
        await NodaInteropRowParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<NodaInteropRow> back = await NodaInteropRowParquetExtensions.ReadParquetAsync(ms);
        back.ShouldBe(rows);

        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        reader.Schema.Fields.Single(f => f.Name == "Ticks").ShouldBeOfType<DateTimeDataField>();
        reader
            .Schema.Fields.Single(f => f.Name == "Nanos")
            .ShouldBeOfType<DataField>()
            .ClrType.ShouldBe(typeof(long));
    }

    [Fact]
    public void InteropAdaptersRejectValuesTheyCannotHoldExactly()
    {
        Instant subTick = NodaConstants.UnixEpoch + Duration.FromNanoseconds(1);
        Should.Throw<ArgumentException>(() => InstantAsDateTimeAdapter.ToStorage(subTick));
        Should.Throw<ArgumentException>(() =>
            InstantAsDateTimeMicrosecondsAdapter.ToStorage(
                NodaConstants.UnixEpoch + Duration.FromTicks(1)
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            InstantAsDateTimeAdapter.ToStorage(Instant.MinValue)
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            InstantAsUnixNanosecondsAdapter.ToStorage(Instant.MaxValue)
        );
        Should.Throw<ArgumentException>(() =>
            IsoLocalDateAsDateOnlyAdapter.ToStorage(new LocalDate(2024, 1, 1).WithCalendar(Julian))
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            IsoLocalDateAsDateTimeAdapter.ToStorage(new LocalDate(0, 1, 1))
        );
        Should.Throw<ArgumentException>(() =>
            IsoLocalDateTimeAsDateTimeAdapter.ToStorage(
                new LocalDateTime(2024, 1, 1, 0, 0).PlusNanoseconds(1)
            )
        );
        Should.Throw<ArgumentException>(() =>
            LocalTimeAsTimeOnlyAdapter.ToStorage(LocalTime.Midnight.PlusNanoseconds(100))
        );
    }

    [Fact]
    public async Task ValueTypeTargetsFieldsAndNestedTypesReadAndWriteThroughTheirShadows()
    {
        var rows = new List<NodaMutableRow>
        {
            new()
            {
                At = Instant.FromUnixTimeSeconds(-5),
                Offset = Offset.FromHoursAndMinutes(5, 45),
                Child = new NodaChild { When = new LocalDate(1900, 2, 28) },
                Point = new NodaPoint { At = NodaConstants.UnixEpoch, Off = Offset.MaxValue },
            },
            new()
            {
                At = Instant.FromUnixTimeSeconds(5),
                Offset = Offset.MinValue,
                Child = null,
                Point = new NodaPoint { At = Instant.FromUtc(2262, 4, 11, 23, 47, 16), Off = null },
            },
        };

        using var ms = new MemoryStream();
        await NodaMutableRowParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<NodaMutableRow> back = await NodaMutableRowParquetExtensions.ReadParquetAsync(ms);

        back.Count.ShouldBe(2);
        back[0].At.ShouldBe(rows[0].At);
        back[0].Offset.ShouldBe(rows[0].Offset);
        back[0].Child!.When.ShouldBe(new LocalDate(1900, 2, 28));
        back[0].Point.ShouldBe(rows[0].Point);
        back[1].Child.ShouldBeNull();
        back[1].Point.ShouldBe(rows[1].Point);

        var points = new List<NodaPoint> { rows[0].Point, rows[1].Point };
        using var pointStream = new MemoryStream();
        await NodaPointParquetExtensions.WriteParquetAsync(points, pointStream);
        pointStream.Position = 0;
        (await NodaPointParquetExtensions.ReadParquetAsync(pointStream)).ShouldBe(points);
    }

    [Fact]
    public async Task ProjectRegisteredStructuralAdapterRoundTrips()
    {
        var rows = new List<Invoice>
        {
            new() { Total = new Money(12.34m, "GBP"), Refund = null },
            new() { Total = new Money(-0.01m, "JPY"), Refund = new Money(99m, "EUR") },
        };

        using var ms = new MemoryStream();
        await InvoiceParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        (await InvoiceParquetExtensions.ReadParquetAsync(ms)).ShouldBe(rows);
    }

    private static IEnumerable<NodaEvent> NodaEventVectors()
    {
        var overlap = new LocalDateTime(2021, 10, 31, 1, 30);
        yield return new NodaEvent
        {
            Id = 0,
            At = Instant.MinValue,
            MaybeAt = null,
            Day = new LocalDate(-9998, 1, 1),
            MaybeDay = new LocalDate(2024, 2, 29).WithCalendar(Coptic),
            Time = LocalTime.MaxValue,
            Local = new LocalDateTime(2024, 2, 29, 12, 0).PlusNanoseconds(1).WithCalendar(Julian),
            Offset = Offset.FromHours(14),
            MaybeOffset = Offset.FromSeconds(-(12 * 3600) - 1),
            Elapsed = -Duration.FromNanoseconds(1),
            Stamp = new OffsetDateTime(new LocalDateTime(1, 1, 1, 0, 0), Offset.FromHours(-12)),
            DateWithOffset = new OffsetDate(
                new LocalDate(2000, 1, 1),
                Offset.FromHoursAndMinutes(5, 45)
            ),
            TimeWithOffset = new OffsetTime(new LocalTime(0, 0).PlusNanoseconds(1), Offset.Zero),
            Window = new Interval(null, null),
            Span = new DateInterval(new LocalDate(2024, 1, 1), new LocalDate(2024, 12, 31)),
            Month = new YearMonth(2024, 13, CalendarSystem.Coptic),
            Anniversary = new AnnualDate(2, 29),
            Retention = Period.FromMinutes(90),
            MaybeRetention = null,
            Zoned = London.AtLeniently(overlap),
            MaybeZoned = London.MapLocal(overlap).Last(),
        };

        yield return new NodaEvent
        {
            Id = 1,
            At = Instant.MaxValue,
            MaybeAt = NodaConstants.UnixEpoch - Duration.FromNanoseconds(1),
            Day = new LocalDate(9999, 12, 31),
            MaybeDay = null,
            Time = LocalTime.Midnight,
            Local = new LocalDateTime(-9998, 1, 1, 0, 0),
            Offset = Offset.MinValue,
            MaybeOffset = null,
            Elapsed = Duration.MaxValue,
            Stamp = Instant.FromUtc(2024, 3, 31, 1, 0).InZone(London).ToOffsetDateTime(),
            DateWithOffset = new OffsetDate(
                new LocalDate(2024, 1, 1).WithCalendar(Julian),
                Offset.MaxValue
            ),
            TimeWithOffset = new OffsetTime(LocalTime.MaxValue, Offset.MinValue),
            Window = new Interval(NodaConstants.UnixEpoch, null),
            Span = null,
            Month = new YearMonth(-9998, 1),
            Anniversary = new AnnualDate(12, 31),
            Retention = new PeriodBuilder
            {
                Years = -1,
                Days = 400,
                Nanoseconds = long.MaxValue,
            }.Build(),
            MaybeRetention = Period.Zero,
            Zoned = Instant.FromUtc(2021, 3, 28, 0, 59, 59).InZone(London),
            MaybeZoned = null,
        };

        yield return new NodaEvent
        {
            Id = 2,
            At = NodaConstants.UnixEpoch,
            MaybeAt = Instant.MinValue,
            Day = NodaConstants.UnixEpoch.InUtc().Date,
            MaybeDay = new LocalDate(1, 1, 1),
            Time = new LocalTime(12, 34, 56).PlusNanoseconds(789),
            Local = new LocalDateTime(9999, 12, 31, 23, 59, 59).PlusNanoseconds(999_999_999),
            Offset = Offset.Zero,
            MaybeOffset = Offset.MaxValue,
            Elapsed = Duration.MinValue,
            Stamp = new OffsetDateTime(
                new LocalDateTime(2024, 1, 1, 0, 0).WithCalendar(Coptic),
                Offset.FromSeconds(1)
            ),
            DateWithOffset = new OffsetDate(new LocalDate(-1, 12, 31), Offset.MinValue),
            TimeWithOffset = new OffsetTime(new LocalTime(6, 0), Offset.FromHours(-3)),
            Window = new Interval(Instant.MinValue, Instant.MaxValue),
            Span = new DateInterval(
                new LocalDate(2024, 1, 1).WithCalendar(Julian),
                new LocalDate(2024, 1, 1).WithCalendar(Julian)
            ),
            Month = new YearMonth(9999, 12),
            Anniversary = new AnnualDate(1, 1),
            Retention = Period.Zero,
            MaybeRetention = Period.FromTicks(-1),
            Zoned = new ZonedDateTime(
                Instant.FromUtc(2024, 6, 1, 0, 0),
                DateTimeZoneProviders.Tzdb["Asia/Kathmandu"],
                Julian
            ),
            MaybeZoned = NodaConstants.UnixEpoch.InUtc(),
        };
    }

    private sealed class FixedCustomZone : DateTimeZone
    {
        public FixedCustomZone()
            : base("Custom/Not-In-Tzdb", isFixed: true, Offset.Zero, Offset.Zero) { }

        public override ZoneInterval GetZoneInterval(Instant instant) =>
            new("custom", null, null, Offset.Zero, Offset.Zero);
    }
}

[ParquetSerializable]
public sealed partial record NodaEvent
{
    public int Id { get; init; }
    public Instant At { get; init; }
    public Instant? MaybeAt { get; init; }
    public LocalDate Day { get; init; }
    public LocalDate? MaybeDay { get; init; }
    public LocalTime Time { get; init; }
    public LocalDateTime Local { get; init; }
    public Offset Offset { get; init; }
    public Offset? MaybeOffset { get; init; }
    public Duration Elapsed { get; init; }
    public OffsetDateTime Stamp { get; init; }
    public OffsetDate DateWithOffset { get; init; }
    public OffsetTime TimeWithOffset { get; init; }
    public Interval Window { get; init; }
    public DateInterval? Span { get; init; }
    public YearMonth Month { get; init; }
    public AnnualDate Anniversary { get; init; }
    public Period Retention { get; init; } = Period.Zero;
    public Period? MaybeRetention { get; init; }
    public ZonedDateTime Zoned { get; init; }
    public ZonedDateTime? MaybeZoned { get; init; }
}

[ParquetSerializable]
public sealed partial record NodaInteropRow
{
    [ParquetAdapter(typeof(InstantAsDateTimeAdapter))]
    public Instant Ticks { get; init; }

    [ParquetAdapter(typeof(InstantAsDateTimeMicrosecondsAdapter))]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public Instant Micros { get; init; }

    [ParquetAdapter(typeof(InstantAsUnixNanosecondsAdapter))]
    public Instant? Nanos { get; init; }

    [ParquetAdapter(typeof(IsoLocalDateAsDateOnlyAdapter))]
    public LocalDate Date { get; init; }

    [ParquetAdapter(typeof(IsoLocalDateTimeAsDateTimeAdapter))]
    public LocalDateTime Local { get; init; }

    [ParquetAdapter(typeof(LocalTimeAsTimeOnlyAdapter))]
    public LocalTime Time { get; init; }
}

/// <summary>Mutable setters, a public field and a nested target carrying its own adapted member.</summary>
[ParquetSerializable]
public sealed partial class NodaMutableRow
{
    public Instant At { get; set; }

#pragma warning disable CA1051 // Public fields are part of what this model exercises.
    public Offset Offset;
#pragma warning restore CA1051

    public NodaChild? Child { get; set; }

    public NodaPoint Point { get; set; }
}

[ParquetSerializable]
public sealed partial class NodaChild
{
    public LocalDate When { get; set; }
}

/// <summary>A value-type target whose adapted members are init-only.</summary>
[ParquetSerializable]
public partial record struct NodaPoint
{
    [ParquetAdapter(typeof(InstantAsUnixNanosecondsAdapter))]
    public Instant At { get; init; }

    public Offset? Off { get; init; }
}
