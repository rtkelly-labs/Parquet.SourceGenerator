# 45 - NodaTime Adapter Package

> **Status:** `Parquet.SourceGenerator.NodaTime` implements phases 0–4 below (adapter foundation,
> P0 core, native interop, compound values, zone-aware values). Phase 5 is Arrow-only.
> **Package:** `Parquet.SourceGenerator.NodaTime`, published independently from this repository.
> **Implementation notes:** [§A — As implemented](#a--as-implemented-in-parquetsourcegeneratornodatime).
> The adapter mechanism itself is [document 44](./44-TYPE-ADAPTERS.md).

The original design follows unchanged — it is the shared text the Arrow package is reviewed
against too.

---

## 1. Why NodaTime is the right first extension ecosystem

NodaTime is a strong test of the adapter model because it deliberately represents different temporal concepts with different CLR types:

```text
Instant
LocalDate
LocalTime
LocalDateTime
OffsetDate
OffsetTime
OffsetDateTime
ZonedDateTime
Duration
Period
Interval
DateInterval
YearMonth
AnnualDate
Offset
```

Treating all of those as variations of `DateTime` would destroy the distinction the library exists to provide.

It also exposes difficult storage questions immediately:

- absolute vs local time;
- calendar identity;
- time-zone identity;
- offset identity;
- fixed durations vs calendrical periods;
- nanosecond precision;
- open-ended intervals;
- value ranges that do not fit a single signed 64-bit nanosecond count.

That makes NodaTime a useful conformance suite for a general adapter mechanism.

## 2. Package topology

Start with two independent packages.

```text
Parquet.SourceGenerator repo
└── Parquet.SourceGenerator.NodaTime
    ├── own registrations
    ├── own surrogates
    ├── Parquet-specific tests
    └── no dependency on Arrow.SourceGenerator

Arrow.SourceGenerator repo
└── Arrow.SourceGenerator.NodaTime
    ├── own registrations
    ├── own surrogates
    ├── Arrow-specific tests
    └── no dependency on Parquet.SourceGenerator
```

Both depend on NodaTime.

Use backend-specific namespaces to avoid ambiguity when both packages are referenced:

```text
Parquet.SourceGenerator.NodaTime.Adapters.InstantAdapter
Arrow.SourceGenerator.NodaTime.Adapters.InstantAdapter
```

The implementations may deliberately duplicate logic at first.

## 3. Core principles

### Lossless defaults

Default adapters preserve:

- precision;
- represented range;
- calendar identity;
- zone identity where present;
- offset identity where present;
- period components without normalization.

No default adapter silently truncates nanoseconds to microseconds or turns local time into an instant.

### Native mappings are opt-in when narrower

Native Arrow/Parquet temporal types remain important for ecosystem interoperability, but if they narrow the NodaTime value domain they should be explicit.

Example:

```text
Instant default
→ lossless surrogate

Instant + explicit interoperability adapter
→ timestamp[ns, UTC]
→ range checked
```

### No universal string fallback

Text can be offered as an explicit compatibility adapter, but it should not become the default architecture because it:

- allocates;
- hides structure;
- weakens statistics/filtering;
- reduces usefulness to columnar consumers;
- moves semantics into parsing conventions.

### Static/AOT-friendly conversion

Adapters remain compile-time resolved static extension methods.

No runtime reflection or converter registry is introduced.

## 4. Representation vocabulary

Names below are illustrative. Final field names and epochs should be frozen only after implementation tests.

### Instant

A lossless representation needs more than a total `long` nanosecond count.

```csharp
public readonly record struct NodaInstantStorage(
    int DaysSinceUnixEpoch,
    long NanosecondOfDay);
```

Constraints:

```text
0 <= NanosecondOfDay < nanoseconds per standard day
```

Negative instants use a negative day count plus a positive nanosecond-of-day remainder.

There must be exactly one canonical normalization.

### LocalDate

```csharp
public readonly record struct NodaLocalDateStorage(
    int DayNumber,
    string CalendarId);
```

The integer epoch must be:

- stable;
- documented;
- calendar-independent.

`CalendarId` is required because `LocalDate` can carry non-ISO calendar identity.

### LocalTime

```csharp
public readonly record struct NodaLocalTimeStorage(
    long NanosecondOfDay);
```

### LocalDateTime

```csharp
public readonly record struct NodaLocalDateTimeStorage(
    int DayNumber,
    long NanosecondOfDay,
    string CalendarId);
```

It deliberately contains no zone or offset.

### Offset

```csharp
public readonly record struct NodaOffsetStorage(
    int Seconds);
```

### OffsetDateTime

Prefer the actual represented concepts rather than reducing universally to `Instant + Offset`:

```csharp
public readonly record struct NodaOffsetDateTimeStorage(
    int DayNumber,
    long NanosecondOfDay,
    string CalendarId,
    int OffsetSeconds);
```

### ZonedDateTime

A lossless shape should include enough information to detect zone-database drift:

```csharp
public readonly record struct NodaZonedDateTimeStorage(
    int InstantDaysSinceUnixEpoch,
    long InstantNanosecondOfDay,
    string ZoneId,
    string CalendarId,
    int ObservedOffsetSeconds);
```

The observed offset is persisted so reconstruction can fail loudly if a different zone-data version would change the represented local value.

## 5. Type support matrix

| NodaTime type | Default representation | Arrow native/interop option | Parquet native/interop option | Priority |
|---|---|---|---|---|
| `Instant` | day + ns-of-day struct | `timestamp[ns, UTC]`, range checked | timestamp where exact policy is available | P0 |
| `LocalDate` | day number + calendar ID | `date32` for explicit ISO-only mapping | DATE for explicit ISO-only mapping | P0 |
| `LocalTime` | ns-of-day | `time64[ns]` | exact TIME unit if backend supports it | P0 |
| `LocalDateTime` | day + ns-of-day + calendar ID | explicit ISO timestamp-like mapping | explicit ISO timestamp mapping | P0 |
| `Offset` | seconds | integer / metadata semantics | integer / logical custom semantics | P0 |
| `Duration` | day + ns remainder | `duration[ns]` only under range policy | native/integer only under explicit range policy | P0 |
| `OffsetDateTime` | local datetime + offset | struct | struct | P1 |
| `OffsetDate` | local date + offset | struct | struct | P1 |
| `OffsetTime` | local time + offset | struct | struct | P1 |
| `Interval` | optional start + optional end | struct | struct | P1 |
| `DateInterval` | start/end LocalDate storage | struct | struct | P1 |
| `YearMonth` | year + month + calendar ID | struct | struct | P1 |
| `AnnualDate` | month + day | struct | struct | P1 |
| `Period` | component-preserving struct | struct / possible Arrow extension | struct | P1 |
| `ZonedDateTime` | instant + zone + calendar + observed offset | struct / possible Arrow extension | struct | P1 |
| `CalendarSystem` | calendar ID | UTF-8 | string | P2 |
| `DateTimeZone` | no default generic adapter | explicit provider-bound adapter | explicit provider-bound adapter | P2 |
| `Instant64` | signed ns-from-epoch | natural `timestamp[ns, UTC]` | ns timestamp if backend supports it | P2 |
| `Duration64` | signed nanoseconds | natural `duration[ns]` | integer/native duration | P2 |

Priority is implementation order, not stability classification.

## 6. Instant policy

`Instant` is the clearest reason native timestamp mappings must be explicit.

A signed 64-bit nanosecond timestamp has a materially narrower range than ordinary NodaTime `Instant`.

Therefore this should **not** be the universal default:

```text
Instant → int64 nanoseconds since Unix epoch
```

### Default

```text
Instant
→ { daysSinceUnixEpoch, nanosecondOfDay }
```

### Explicit Arrow mapping

Offer an adapter such as:

```text
InstantTimestampNanosecondsAdapter
```

with:

```text
Instant → timestamp[ns, UTC]
```

and a clear range check.

### Explicit lower-precision mappings

If microsecond/millisecond timestamp adapters are offered, their precision policy must be explicit.

Default behaviour should reject values that are not exactly representable rather than silently truncate.

## 7. LocalDate and calendar identity

`LocalDate` can carry a non-ISO calendar.

Therefore:

```text
LocalDate → Date32
```

or:

```text
LocalDate → Parquet DATE
```

is only fully semantic under an ISO-only contract.

### Default

```text
LocalDate
→ { dayNumber, calendarId }
```

### Explicit native adapter

```text
IsoLocalDateAdapter
```

requires ISO calendar and maps to the backend-native date type.

Non-ISO values fail clearly.

## 8. LocalDateTime policy

`LocalDateTime` means:

```text
calendar date + local time
```

It does not identify an instant.

Never map it through UTC or the machine-local time zone.

### Default

```text
{ dayNumber, nanosecondOfDay, calendarId }
```

### Explicit native adapter

An ISO-only adapter may map it to a timezone-less timestamp-like representation where the backend supports one.

The adapter documentation must state that no timezone conversion occurs.

## 9. Duration and Period stay distinct

NodaTime deliberately separates:

```text
Duration
= fixed elapsed amount

Period
= calendar-relative components
```

The package must preserve that distinction.

### Duration

Conceptual lossless storage:

```csharp
public readonly record struct NodaDurationStorage(
    int Days,
    long NanosecondOfDay);
```

Use one canonical normalization for negative values.

`Duration64` can naturally map to a signed 64-bit nanosecond count.

### Period

Do not normalize a `Period` into elapsed nanoseconds.

Persist all supported period components separately.

Conceptually:

```csharp
public readonly record struct NodaPeriodStorage(
    long Years,
    long Months,
    long Weeks,
    long Days,
    long Hours,
    long Minutes,
    long Seconds,
    long Milliseconds,
    long Ticks,
    long Nanoseconds);
```

The final members/types should mirror the supported NodaTime version's public component model.

Round-trip tests must prove non-normalized values remain non-normalized.

## 10. Intervals

### Interval

NodaTime intervals can be bounded or unbounded.

```csharp
public readonly record struct NodaIntervalStorage(
    NodaInstantStorage? Start,
    NodaInstantStorage? End);
```

Null represents an unbounded side.

### DateInterval

Store start/end through the same lossless `LocalDate` representation so calendar identity is retained.

## 11. Offset types

### Offset

```text
signed seconds
```

### OffsetTime

```text
{ nanosecondOfDay, offsetSeconds }
```

### OffsetDate

```text
{ dayNumber, calendarId, offsetSeconds }
```

### OffsetDateTime

```text
{ dayNumber, nanosecondOfDay, calendarId, offsetSeconds }
```

These representations preserve what the value actually claims to know without inventing a time zone.

## 12. ZonedDateTime and zone-provider policy

`ZonedDateTime` exposes the first contextual reconstruction problem.

Persist:

```text
instant
zone ID
calendar ID
observed offset
```

### Initial read policy

Use:

```text
DateTimeZoneProviders.Tzdb
```

as the documented initial provider.

On read:

1. resolve `ZoneId`;
2. reconstruct the instant;
3. get the zone's offset at that instant;
4. compare with `ObservedOffsetSeconds`;
5. if they differ, fail with an exception explaining that persisted zone semantics and installed TZDB data disagree;
6. reconstruct with the stored calendar.

This is intentionally fail-loud.

### Future

A contextual adapter mechanism may allow an application-provided `IDateTimeZoneProvider`.

Do not block the first package on that broader abstraction.

## 13. DateTimeZone is not a normal default value adapter

`DateTimeZone` is provider/behaviour-backed and can have custom implementations.

Therefore:

```text
DateTimeZone → string ID
```

is not universally lossless.

The package may provide:

```text
TzdbDateTimeZoneAdapter
```

with the explicit contract that only TZDB-resolvable zones are accepted.

A generic default `DateTimeZone` adapter should wait for contextual adapter support.

## 14. CalendarSystem

`CalendarSystem` may be mapped through its stable ID if tests prove round-trip reconstruction for the supported NodaTime range.

Treat it as lower priority because most application models carry calendar information through date values rather than standalone `CalendarSystem` properties.

## 15. Arrow-specific design

The Arrow package should turn lossless surrogates into ordinary Arrow structures.

Example:

```text
NodaInstantStorage
→ StructArray<
    days: int32,
    nanosecondOfDay: int64
  >
```

and:

```text
NodaOffsetDateTimeStorage
→ StructArray<
    dayNumber: int32,
    nanosecondOfDay: int64,
    calendarId: utf8,
    offsetSeconds: int32
  >
```

### Arrow extension metadata

A later phase may layer Arrow extension metadata over the standard storage:

```text
storage:
struct<days:int32,nanosecondOfDay:int64>

extension name:
nodatime.instant
```

This allows Arrow-aware consumers to distinguish a NodaTime logical value from an arbitrary struct while retaining standard Arrow buffers.

Do not block the first package on extension types. External extension metadata is untrusted input and needs a robust validation design.

## 16. Parquet-specific design

The Parquet package should represent structural surrogates as nested groups where the modern backend supports them.

Example:

```text
Instant
→ group {
    days_since_unix_epoch: INT32
    nanosecond_of_day: INT64
  }
```

This is lossless but less universally interoperable than a native timestamp.

For users prioritising broad ecosystem interoperability, provide explicit adapters such as:

```text
InstantAsTimestampNanoseconds
IsoLocalDateAsDate
IsoLocalDateTimeAsTimestamp
```

These must:

- validate range;
- validate precision;
- validate calendar assumptions;
- fail rather than silently coerce.

The legacy backend may expose only the subset it can represent safely. That should be documented rather than forcing parity.

## 17. Public API shape

Each package auto-registers defaults through its own generator contract.

Conceptually:

```csharp
[assembly: ParquetTypeAdapter(typeof(
    Parquet.SourceGenerator.NodaTime.Adapters.InstantAdapter))]
```

with the Arrow equivalent in the Arrow package.

Adapter classes expose extension methods:

```csharp
public static class InstantAdapter
{
    public static NodaInstantStorage ToStorage(
        this NodaTime.Instant value);

    public static NodaTime.Instant FromStorage(
        this NodaInstantStorage value);
}
```

Consumers normally do not call these methods directly. Generated code calls them statically.

## 18. Explicit per-member representation

Where more than one valid representation exists, allow an explicit adapter.

Parquet concept:

```csharp
[ParquetAdapter(typeof(
    Parquet.SourceGenerator.NodaTime.Adapters.InstantTimestampNanosecondsAdapter))]
public Instant Timestamp { get; init; }
```

Arrow concept:

```csharp
[ArrowAdapter(typeof(
    Arrow.SourceGenerator.NodaTime.Adapters.InstantTimestampNanosecondsAdapter))]
public Instant Timestamp { get; init; }
```

The package default remains lossless.

## 19. Project-wide override can come later

Some applications will want every `Instant` to use a native timestamp representation.

A later project-level override can support that.

Do not require it for the first implementation. Per-member explicit adapters plus lossless defaults are enough to validate the architecture safely.

## 20. Schema metadata

Where the backend supports metadata, attach descriptive logical identity such as:

```text
nodatime.type = Instant
nodatime.contract = 1
```

Potential additional metadata:

```text
nodatime.calendar.policy = per-value
nodatime.zone.provider = TZDB
```

Metadata is descriptive, not trusted authority.

External data must still be validated against its physical structure.

## 21. Versioning

Each package has two compatibility dimensions:

```text
generator adapter contract
NodaTime supported version range
```

Pick one tested NodaTime 3.x floor during implementation and run CI against:

- the minimum supported version;
- the repository-pinned version;
- the newest supported 3.x version where practical.

Do not claim all 3.x versions until tests prove it.

Persisted surrogate contracts are data formats. Changing the meaning of fields such as:

```text
DaysSinceUnixEpoch
NanosecondOfDay
CalendarId
```

requires a data-format version decision, not just a package refactor.

## 22. AOT and trimming

Verify through actual generated consumers.

Parquet:

```text
NodaTime POCO
   ↓ generated adapter calls
Parquet write/read
   ↓
round trip
```

Arrow:

```text
NodaTime POCO
   ↓
RecordBatch
   ↓
NodaTime POCO
```

Publish the test apps Native AOT and execute the native binaries.

## 23. Cross-backend semantic conformance

Even with no shared code, both repos should implement the same conceptual test vectors.

For each supported type include cases such as:

```text
minimum/maximum ordinary values
Unix epoch
pre-epoch fractional instant
maximum nanosecond fraction
leap day
non-ISO calendar value
negative duration
non-normalized period
positive and negative unusual offsets
DST overlap ZonedDateTime
DST transition-adjacent ZonedDateTime
unbounded interval endpoints
```

Maintain equivalent semantic test cases in both repos.

This is how the projects share ideas without coupling implementation.

## 24. Interoperability tests

### Arrow package

Validate generated batches through Apache.Arrow itself:

- schema inspection;
- typed array access;
- IPC round trip;
- null bitmap correctness;
- nested children/offsets;
- extension metadata validation when added.

Add PyArrow fixtures where they provide independent value.

### Parquet package

Validate using:

- generated round trips;
- Parquet.Net low-level inspection;
- PyArrow/DuckDB for explicit native interoperability mappings;
- schema inspection for structural lossless mappings.

Native interop adapters should have stronger cross-language tests than NodaTime-specific structural storage.

## 25. Performance expectations

The adapter mechanism itself should be effectively free.

Compare:

```text
built-in BCL type
equivalent NodaTime adapter
hand-written conversion
```

Expected costs come from:

- semantic conversion;
- structural expansion;
- Arrow array construction;
- Parquet shredding.

They should not come from adapter lookup.

Benchmark both:

```text
lossless default
```

and:

```text
native interop adapter
```

because the native representation may be smaller/faster while intentionally supporting a narrower semantic domain.

## 26. Implementation phases

### Phase 0 — adapter foundation

Independently in each repo:

- registration attribute;
- exact-type lookup;
- conversion signature validation;
- deterministic precedence;
- ambiguity diagnostics;
- value-equatable adapter plan;
- one trivial test adapter;
- AOT proof.

### Phase 1 — P0 NodaTime core

Implement:

- `Instant`;
- `LocalDate`;
- `LocalTime`;
- `LocalDateTime`;
- `Offset`;
- `Duration`.

This proves:

- structural surrogates;
- calendar preservation;
- nanosecond preservation;
- pre-epoch arithmetic;
- negative duration canonicalization.

### Phase 2 — native interoperability adapters

Implement explicit:

- `Instant` timestamp adapter(s);
- ISO `LocalDate` date adapter;
- ISO `LocalDateTime` timestamp adapter;
- `LocalTime` native time adapter where exact;
- `Instant64` / `Duration64` native mappings if included in the supported NodaTime version.

Cross-language tests are mandatory here.

### Phase 3 — compound temporal values

Implement:

- `OffsetDate`;
- `OffsetTime`;
- `OffsetDateTime`;
- `Interval`;
- `DateInterval`;
- `YearMonth`;
- `AnnualDate`;
- `Period`.

### Phase 4 — zone-aware values

Implement:

- `ZonedDateTime`;
- explicit TZDB `DateTimeZone` adapter;
- observed-offset validation;
- provider/version failure behaviour.

### Phase 5 — Arrow extension metadata

Arrow only:

- stable extension names;
- metadata schema;
- defensive metadata validation;
- IPC interop tests.

Do not block initial NodaTime support on this phase.

## 27. Suggested issues in each repo

### Adapter foundation

**`feat(adapters): compile-time exact-type adapter registration and static conversion`**

Acceptance:

- assembly registration;
- exact source type;
- surrogate type;
- direct generated calls;
- diagnostics;
- no reflection;
- Native AOT.

### Structural surrogate support

**`feat(adapters): recursively plan structural surrogate types`**

Acceptance:

- nested surrogate;
- cycle detection;
- nesting bound;
- nullability;
- generated schema/round-trip tests.

### NodaTime core

**`feat(nodatime): lossless adapters for Instant, LocalDate, LocalTime, LocalDateTime, Offset and Duration`**

### NodaTime native mappings

**`feat(nodatime): explicit native temporal interoperability adapters`**

### NodaTime compound values

**`feat(nodatime): compound adapters for offset, interval, calendar and period types`**

### NodaTime zones

**`feat(nodatime): TZDB-backed ZonedDateTime adapter with persisted offset validation`**

Arrow additionally:

**`feat(nodatime): Arrow extension metadata for NodaTime logical types`**

## 28. Non-goals

The initial packages should not:

- replace NodaTime with BCL temporal values internally;
- silently truncate nanoseconds;
- silently force ISO calendars;
- silently use machine-local timezone;
- use reflection-based converter lookup;
- require shared Parquet/Arrow adapter code;
- support arbitrary custom `IDateTimeZoneProvider` injection in v1;
- promise native Arrow/Parquet representation for every NodaTime type;
- map service abstractions such as `IClock`.

## 29. Long-term extraction rule

If both independently implemented systems converge, NodaTime will provide strong evidence about what is actually common.

Only extract shared code when all of the following are true:

```text
same concept
same semantics
same failure behaviour
same versioning needs
proven in both backends
```

Until then, duplicated adapter infrastructure is preferable to a premature common framework.

The desired product shape is:

```text
domain model using NodaTime
          │
          ├──────────────────┐
          ▼                  ▼
Parquet.SourceGenerator   Arrow.SourceGenerator
.NodaTime package         .NodaTime package
          │                  │
          ▼                  ▼
      Parquet            RecordBatch
```

The application stays idiomatic NodaTime.

Storage representation becomes a generator concern rather than a reason to fall back to weaker temporal APIs.

---

## A — As implemented in Parquet.SourceGenerator.NodaTime

### A.1 Using it

```bash
dotnet add package Parquet.SourceGenerator
dotnet add package Parquet.SourceGenerator.NodaTime
```

Nothing else. The package's `AssemblyInfo.cs` registers every lossless default with
`[assembly: ParquetTypeAdapter(...)]`; the generator discovers them from the reference. The
interop adapters are chosen per member with `[ParquetAdapter(typeof(...))]`.

Namespaces: adapters in `Parquet.SourceGenerator.NodaTime.Adapters`, storage records in
`Parquet.SourceGenerator.NodaTime.Storage`. (Code that itself lives in a
`Parquet.SourceGenerator.*` namespace sees `NodaTime` resolve to the package namespace; write
`global::NodaTime` there. Generated code is always fully qualified.)

### A.2 Default representations (all registered)

| Type | Surrogate | Parquet shape | Notes |
|---|---|---|---|
| `Instant` | `NodaInstantStorage` | group `{ days_since_unix_epoch INT32, nanosecond_of_day INT64 }` | floored: `0 <= nanosecond_of_day < 86 400 000 000 000` |
| `Duration` | `NodaDurationStorage` | group `{ days, nanosecond_of_day }` | floored identically |
| `LocalDate` | `NodaLocalDateStorage` | group `{ day_number INT32, calendar_id STRING }` | day number = proleptic ISO days since 1970-01-01, calendar-independent |
| `LocalTime` | `long` | `INT64` | nanoseconds since midnight (scalar — see A.4) |
| `LocalDateTime` | `NodaLocalDateTimeStorage` | group `{ day_number, nanosecond_of_day, calendar_id }` | never converted through any zone |
| `Offset` | `int` | `INT32` | seconds (scalar — see A.4) |
| `OffsetDateTime` | `NodaOffsetDateTimeStorage` | group `{ day_number, nanosecond_of_day, calendar_id, offset_seconds }` | |
| `OffsetDate` | `NodaOffsetDateStorage` | group `{ day_number, calendar_id, offset_seconds }` | |
| `OffsetTime` | `NodaOffsetTimeStorage` | group `{ nanosecond_of_day, offset_seconds }` | |
| `ZonedDateTime` | `NodaZonedDateTimeStorage` | group `{ instant { … }, zone_id, calendar_id, observed_offset_seconds }` | §12 fail-loud policy |
| `Interval` | `NodaIntervalStorage` | group `{ start? { … }, end? { … } }` | null side = unbounded |
| `DateInterval` | `NodaDateIntervalStorage` | group `{ start { … }, end { … } }` | inclusive, calendar kept |
| `YearMonth` | `NodaYearMonthStorage` | group `{ year, month, calendar_id }` | |
| `AnnualDate` | `NodaAnnualDateStorage` | group `{ month, day }` | |
| `Period` | `NodaPeriodStorage` | group of all ten components | never normalized |

Column names inside groups are snake_case (§16) and fixed by `[property: ParquetColumn(...)]`
on the storage records: they are a data format (§21).

Collections of these types (`List<Instant>`, `LocalDate?[]`, `IReadOnlyList<Period>`, ...)
convert per element inline ([document 44 §A.4b](./44-TYPE-ADAPTERS.md)), and every default
above works as a list element, including the three whose storage nests a group (`ZonedDateTime`,
`Interval`, `DateInterval`). NodaTime fields inside your own types — a `[ParquetSerializable]`
child, a list element type, or the surrogate of your own adapter — convert inline too (§A.4c). An interop adapter applies to a collection's elements when placed on the
collection member: `[ParquetAdapter(typeof(InstantAsUnixNanosecondsAdapter))] List<Instant>`.

### A.3 Explicit interop adapters (not registered)

| Adapter | Maps | Rejects (throws) |
|---|---|---|
| `InstantAsDateTimeAdapter` | `Instant` → UTC `DateTime` | sub-tick precision; years outside 1–9999 |
| `InstantAsDateTimeMicrosecondsAdapter` | `Instant` → UTC `DateTime`, for `[ParquetTimestamp(Microseconds)]` | sub-microsecond precision; years outside 1–9999 |
| `InstantAsUnixNanosecondsAdapter` | `Instant` → `long` ns since epoch | outside 1677-09-21 … 2262-04-11 |
| `IsoLocalDateAsDateTimeAdapter` | ISO `LocalDate` → midnight `DateTime` | non-ISO; years outside 1–9999 |
| `IsoLocalDateTimeAsDateTimeAdapter` | ISO `LocalDateTime` → unspecified-kind `DateTime`, no zone conversion | non-ISO; years outside 1–9999; sub-tick |
| `IsoLocalDateAsDateOnlyAdapter` (net6+) | ISO `LocalDate` → `DateOnly` | non-ISO; years outside 1–9999 |
| `LocalTimeAsTimeOnlyAdapter` (net6+) | `LocalTime` → `TimeOnly` (microsecond TIME column) | sub-microsecond |

Parquet.Net's timestamp support tops out at microseconds, so there is no nanosecond timestamp
adapter; `InstantAsUnixNanosecondsAdapter` is the exact-nanosecond native option (a plain INT64).

The interop adapters are plain static methods, not extensions, so they never compete with the
defaults' `instant.ToStorage()` at a call site.

### A.4 Deviations from the design text

- **`LocalTime` and `Offset` are scalars**, not one-field records (§4 sketched
  `NodaLocalTimeStorage(long)` / `NodaOffsetStorage(int)`). A single-field group is strictly
  worse: the same information, one more nesting level for every reader, and no statistics on the
  column that matters. The representation is still lossless.
- **`ZonedDateTime` nests `NodaInstantStorage`** rather than flattening its two fields, so the
  instant has the same shape everywhere it appears.
- **Writing a `ZonedDateTime` whose zone TZDB cannot resolve by id throws** (a custom
  `DateTimeZone` could never be reconstructed); reading still performs the §12 observed-offset
  check.
- **Not implemented (P2):** `CalendarSystem`, `DateTimeZone`, and `Instant64` / `Duration64`.
  The last two arrived in NodaTime 3.3 (`NodaTime.HighPerformance`), above this package's 3.2
  floor, so supporting them means either raising the floor or multi-targeting the NodaTime
  dependency — a versioning decision (§21), not an adapter one.

### A.5 Versioning

NodaTime floor: **3.2.0** — what the package compiles against and what its nuspec declares. The
test suite overrides NodaTime to the newest 3.x (3.3.4 at the time of writing) so both ends of
the range are exercised; the package does not claim versions below the floor (§21).

### A.6 Evidence

- `NodaTimeAdapterRoundTripTests`: the §23 vector set — min/max, epoch, pre-epoch fractional
  instants, maximum nanosecond fraction, leap days, Julian and Coptic calendars, negative and
  extreme durations, non-normalized periods, ±14/−12 hour and sub-minute offsets, DST overlap
  and transition-adjacent zoned values, a non-ISO zoned value, unbounded intervals — through
  real generated code, plus schema inspection of the written file and interop range/precision
  rejections.
- `test/Parquet.SourceGenerator.AotTest`: NodaTime members (groups, nested groups, nullable,
  TZDB, interop) in a Native AOT binary.
- `test/PackageConsumption`: the packed generator plus the packed NodaTime package, nothing else.
