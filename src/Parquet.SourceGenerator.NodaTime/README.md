# Parquet.SourceGenerator.NodaTime

[NodaTime](https://nodatime.org) type adapters for
[Parquet.SourceGenerator](https://github.com/rtkelly13/Parquet.SourceGenerator). Reference the
package and `Instant`, `LocalDate`, `ZonedDateTime` and the other NodaTime value types become
ordinary members of your `[ParquetSerializable]` models — resolved at compile time, called
directly by generated code, with no reflection and no runtime registry.

```bash
dotnet add package Parquet.SourceGenerator
dotnet add package Parquet.SourceGenerator.NodaTime
```

```csharp
using NodaTime;
using Parquet.SourceGenerator;

[ParquetSerializable]
public partial record Reading
{
    public Instant TakenAt { get; init; }
    public LocalDate Day { get; init; }
    public ZonedDateTime? ScheduledFor { get; init; }
    public Period Retention { get; init; } = Period.Zero;
}
```

## Lossless by default

Every default adapter preserves the full NodaTime value: nanosecond precision, the whole
NodaTime range (years -9998 to 9999), calendar identity, offsets, zone identity, and period
components without normalization. Most types are stored as small Parquet groups, e.g. an
`Instant` as `{ days_since_unix_epoch: INT32, nanosecond_of_day: INT64 }`; `LocalTime` and
`Offset` are single `INT64` / `INT32` columns.

| NodaTime type | Stored as |
|---|---|
| `Instant` | `{ days_since_unix_epoch, nanosecond_of_day }` (floored) |
| `Duration` | `{ days, nanosecond_of_day }` (floored) |
| `LocalDate` | `{ day_number, calendar_id }` |
| `LocalTime` | `INT64` nanoseconds since midnight |
| `LocalDateTime` | `{ day_number, nanosecond_of_day, calendar_id }` |
| `Offset` | `INT32` seconds |
| `OffsetDateTime` | `{ day_number, nanosecond_of_day, calendar_id, offset_seconds }` |
| `OffsetDate` / `OffsetTime` | the date or time fields plus `offset_seconds` |
| `ZonedDateTime` | `{ instant, zone_id, calendar_id, observed_offset_seconds }` |
| `Interval` | `{ start?, end? }` — null is an unbounded side |
| `DateInterval` | `{ start, end }` dates |
| `YearMonth` / `AnnualDate` | `{ year, month, calendar_id }` / `{ month, day }` |
| `Period` | every component, unnormalized |

`ZonedDateTime` resolves zones through `DateTimeZoneProviders.Tzdb` and fails loudly on read if
the installed TZDB data gives a different offset than the one observed when the value was written.

## Native timestamps, explicitly

When readers outside .NET need a native Parquet timestamp or date, choose an interop adapter for
that member. They narrow the value domain, so they are never defaults, and they throw rather than
truncate, shift or re-calendar a value that does not fit:

```csharp
[ParquetAdapter(typeof(Parquet.SourceGenerator.NodaTime.Adapters.InstantAsDateTimeMicrosecondsAdapter))]
[ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
public Instant EventTime { get; init; }
```

Available: `InstantAsDateTimeAdapter`, `InstantAsDateTimeMicrosecondsAdapter`,
`InstantAsUnixNanosecondsAdapter`, `IsoLocalDateAsDateTimeAdapter`,
`IsoLocalDateTimeAsDateTimeAdapter`, and on .NET 6+ `IsoLocalDateAsDateOnlyAdapter` and
`LocalTimeAsTimeOnlyAdapter`.

Structural (group) storage needs the default compound-capable generator (Parquet.Net 6, feature
level 2 or later). Full design: `docs/45-NODATIME.md` in the repository.
