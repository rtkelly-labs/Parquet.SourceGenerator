# 46 - Serialization Shapes: What You Can Model, and How Deep

This is the reference for **which C# model shapes the generator can serialize**: which member
types, how far types can nest, what can go inside a list, and where type adapters (NodaTime and
your own) can appear. Every "supported" / "rejected" statement here is one case in
`test/Parquet.SourceGenerator.Tests/SerializationShapeMatrixTests.cs`. The test runs each shape
through the real generator and compiles the output, so this page cannot silently drift from the
code. Case ids are quoted in `[brackets]`.

Applies to the default package (`Parquet.SourceGenerator`, Parquet.Net 6) at the default feature
level. The flat-only configurations are covered in [§8](#8-backends-and-feature-levels).

---

## 1. The short answer

```text
Row
├── leaf column                         int, string, DateTime, decimal, Guid, enum, byte[], ...
├── group            (nested type)      up to 6 group levels below the row
│   └── group
│       └── ...                         no lists inside groups
└── list             (List<T>, T[], ...) only as a member of the row itself
    └── element: leaf
               | group of leaves
               | group containing groups of leaves     ← deepest an element can go
```

| Question | Answer |
|---|---|
| How deeply can nested types nest? | **6 group levels** below the row. The 7th is `PARQ013`. `[group/depth-6]` `[group/depth-7]` |
| Where can a list appear? | **Only as a direct member of the serialized type.** Not inside a nested type `[group/list-inside-group]`, and not inside another list `[list/list-of-lists]`. |
| What can a list element be? | A leaf, a nested type of leaves, or a nested type whose members include **one** further level of nested types of leaves. `[list/element-with-group]` `[list/element-with-group-of-group]` |
| Dictionaries, sets? | Not supported (`PARQ006`). `[list/dictionary]` `[list/hashset]` |
| Where can an adapted type (e.g. `Instant`) appear? | **Anywhere a leaf or group can**: on the row, inside nested types, inside your own adapter's storage record, and as list elements or list-element members. It is subject to the same depth rules, because an adapter's storage record *is* a group. |
| Does the nested type need an attribute? | Yes, `[ParquetSerializable]` (and `partial`). The one exception is an adapter's storage record, which needs nothing. `[group/unattributed-class]` |

---

## 2. Building blocks

Every member becomes one of three Parquet shapes:

| C# member | Parquet shape | Example |
|---|---|---|
| A **leaf** type (§3) | one column | `public int Id` → `required int32 Id` |
| A **nested type** (`[ParquetSerializable]`, class or struct) or an adapter's **group surrogate** | a group of columns | `public Address Ship` → `optional group Ship { … }` |
| A **collection** (§5) | a `LIST` of elements | `public List<int> Ids` → `optional group Ids (LIST) { repeated … }` |

**Nullability** follows the C# annotation under `#nullable enable`:

- A leaf is `required` unless it is `T?`, a nullable reference type, or in an oblivious context.
- A group is always written `optional` in the schema. The C# side decides whether null can occur:
  a reference-type or `Nullable<T>` member round-trips `null`, and a non-nullable struct is
  always present. `[group/nullable-reference]` `[group/nullable-value]`
- A list distinguishes **`null` from empty**, and a nullable element type keeps **null
  elements** in place.

---

## 3. Leaf types

| Supported `[flat/*]` | Notes |
|---|---|
| `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double` | |
| `string` | |
| `decimal` | `[ParquetDecimal(precision, scale)]` optional |
| `DateTime` | INT96 by default (tick precision); `[ParquetTimestamp(Microseconds)]` for a micros timestamp |
| `DateOnly`, `TimeOnly`, `TimeSpan` | `TimeOnly` is a microsecond TIME column |
| `Guid` | stored as a string |
| any `enum` | stored as its underlying integer |
| `byte[]`, `ReadOnlyMemory<byte>`, `ReadOnlyMemory<char>` | |

Anything else — `char`, `DateTimeOffset`, `Uri`, your own value objects — is `PARQ006`
`[flat/unsupported-char]` `[flat/unsupported-datetimeoffset]`. To serialize it anyway, give it a
**type adapter** (§6): that is exactly what the NodaTime package does for `Instant` and friends.

---

## 4. Nested types (groups)

A member whose type is itself `[ParquetSerializable]` is written as a group. Classes, records,
structs and record structs all work, nullable or not. `[group/reference]` `[group/value]`

```csharp
[ParquetSerializable]
public partial record Address
{
    public string? City { get; init; }
    public int? Zip { get; init; }
}

[ParquetSerializable]
public partial record Order
{
    public int Id { get; init; }
    public Address Ship { get; init; } = new();   // group
    public Address? Bill { get; init; }          // group, null round-trips
}
```

```text
required int32  Id
optional group  Ship { optional string City; optional int32 Zip }
optional group  Bill { optional string City; optional int32 Zip }
```

### How deep

Groups nest inside groups up to **six levels below the row** (`TargetParser.MaxCompoundDepth`):

```text
Row.N         → L1   level 1
  .N          → L2   level 2
    .N        → L3   level 3
      .N      → L4   level 4
        .N    → L5   level 5
          .N  → L6   level 6   ✓  [group/depth-6]
            .N → L7  level 7   ✗  PARQ013  [group/depth-7]
```

Every group counts toward the six: nested `[ParquetSerializable]` types, **and adapter storage
groups** (an `Instant` is one group level; a `ZonedDateTime`, whose storage contains the instant
group, is two).

### Not allowed inside a group

- **A list** (`PARQ006`). Lists are row-level only. `[group/list-inside-group]`
- **A type without `[ParquetSerializable]`**. Opting in is deliberate: the parent inlines the
  child's schema, so the child must be a declared Parquet shape. `[group/unattributed-class]`
- **An open generic nested type** (`PARQ010`). A closed generic adapter storage record is fine
  (§6.6).
- **A type cycle** (`PARQ012`): `A` contains `B` contains `A`.

---

## 5. Lists

### Collection types

`T[]` (except `byte[]`, which is a leaf), `List<T>`, `IList<T>`, `ICollection<T>`,
`IReadOnlyCollection<T>`, `IReadOnlyList<T>`, `IEnumerable<T>`. Arrays read back as arrays, and
everything else reads back as a `List<T>`. `[list/leaves]`

Not supported: `Dictionary<,>` and other maps, `HashSet<T>` and other sets, and custom collection
types (`PARQ006`). `[list/dictionary]` `[list/hashset]`

### Where

**Only as a direct member of the `[ParquetSerializable]` type being written.** A list inside a
nested type, or a list of lists, is `PARQ006`. `[group/list-inside-group]` `[list/list-of-lists]`

### What the element can be — the element ladder

| Element | Example | |
|---|---|---|
| a leaf | `List<int>`, `string?[]`, `IEnumerable<double?>` | ✓ `[list/leaves]` |
| a nested type of leaves — reference or value type | `List<Address>`, `Point?[]` | ✓ `[list/reference-elements]` `[list/value-elements]` |
| a nested type containing **one** level of nested types of leaves | `List<Order>` where `Order.Ship` is an `Address` | ✓ `[list/element-with-group]` |
| a nested type containing nested types containing nested types | `List<A>` → `A.B` → `B.C` | ✗ `PARQ006` `[list/element-with-group-of-group]` |

In depth terms: **inside a list, the element can hold groups one level deep.** The element's own
group counts as the first level, so `List<X>` supports `X.Member.Leaf` but not
`X.Member.Member.Leaf`.

```csharp
[ParquetSerializable]
public partial record Shipment
{
    public List<Address> Stops { get; init; } = [];   // element = group of leaves
    public List<Order> Orders { get; init; } = [];    // element = group containing groups (Ship, Bill)
    public Address?[]? Alternates { get; init; }      // null list, null elements both preserved
}
```

```text
optional group Stops (LIST) {
  repeated group list { optional group element { optional string City; optional int32 Zip } }
}
optional group Orders (LIST) {
  repeated group list { optional group element {
    required int32 Id
    optional group Ship { optional string City; optional int32 Zip }
    optional group Bill { optional string City; optional int32 Zip }
  } }
}
```

---

## 6. Type adapters: NodaTime and your own types

An adapter converts a type the generator does not know (`Instant`, `Money`, `Id<T>`) to a
**surrogate** it does: either a leaf (`long`, `string`, `DateTime`, …) or a group (a struct or
class of members). Referencing `Parquet.SourceGenerator.NodaTime` registers adapters for every
NodaTime value type ([document 45](./45-NODATIME.md)). Your own are registered with
`[assembly: ParquetTypeAdapter(typeof(MyAdapter))]`, or chosen per member with
`[ParquetAdapter(typeof(MyAdapter))]` ([document 44](./44-TYPE-ADAPTERS.md)).

**An adapted member behaves exactly like its surrogate**, so every rule above applies to the
surrogate's shape:

| Surrogate is… | Behaves like | Examples |
|---|---|---|
| a leaf | a leaf column | `LocalTime` → `INT64`, `Offset` → `INT32`, interop `Instant` → `DateTime` |
| a group of leaves | a group (1 level) | `Instant`, `Duration`, `LocalDate`, `LocalDateTime`, `OffsetDateTime`, `Period`, … |
| a group containing groups of leaves | a group (2 levels) | `ZonedDateTime` (contains the instant), `Interval`, `DateInterval` |

### 6.1 Where adapted members can appear

| Position | Example | |
|---|---|---|
| on the row | `public Instant At`, `public Instant? MaybeAt` | ✓ `[adapter/root-scalar]` `[adapter/root-group]` `[adapter/root-group-of-group]` |
| inside a nested type, at any depth | `Measurement.At` where the row has a `Measurement` | ✓ `[adapter/in-nested-type]` |
| inside **your own adapter's storage record** | `AppointmentStorage(Instant At, LocalDate Day, string Title)` | ✓ (`NestedAdapterRoundTripTests`) |
| as a list element | `List<Instant>`, `LocalTime?[]`, `List<ZonedDateTime>` | ✓ `[adapter/list-scalar]` `[adapter/list-group]` `[adapter/list-group-of-group]` |
| as a member of a list element | `List<Measurement>` with `Measurement.At : Instant` | ✓ `[adapter/list-element-with-adapted-scalar]` `[adapter/list-element-with-adapted-group]` |
| as a dictionary value | `Dictionary<string, Instant>` | ✗ `PARQ006` (no maps) `[adapter/dictionary-value]` |

### 6.2 How deep, with adapters

Adapter groups count exactly like nested types:

- **Outside lists:** up to six group levels in total. A `ZonedDateTime` inside a nested type
  inside a nested type uses four (type, type, zoned, instant). Four nested types ending in a
  `ZonedDateTime`-shaped member reach exactly six; one more nested type is `PARQ013`.
  `[adapter/root-deeper]` `[adapter/depth-6]` `[adapter/depth-7]`
- **Inside a list:** the element's group plus one more level. So:
  - `List<Instant>` ✓ — element group only.
  - `List<ZonedDateTime>` ✓ — element group plus the instant group.
  - `List<Measurement>` with `Measurement.At : Instant` ✓ — element group plus the instant group.
  - `List<Measurement>` with `Measurement.When : ZonedDateTime` ✗ — that is three levels
    (element → zoned → instant), so `PARQ006`/`PARQ018`.
    `[adapter/list-element-with-adapted-group-of-group]` `[adapter/list-deeper]`

### 6.3 Worked example: a custom type with NodaTime fields

```csharp
[ParquetSerializable]
public partial record Measurement
{
    public double Value { get; init; }
    public Instant At { get; init; }        // adapted group, 1 level
    public LocalTime? Time { get; init; }   // adapted leaf
    public Period? Retention { get; init; } // adapted group
}

[ParquetSerializable]
public partial record SensorRow
{
    public Measurement Reading { get; init; } = new();   // group → contains Instant group (2 levels)
    public Measurement? Maybe { get; init; }
    public List<Measurement> History { get; init; } = [];// element group → Instant group ✓
    public Measurement?[]? MaybeHistory { get; init; }
}
```

The schema the generator writes (from the real output, list wrapper levels elided):

```text
optional group Reading {
  required double Value
  optional group At { required int32 days_since_unix_epoch; required int64 nanosecond_of_day }
  optional int64  Time
  optional group Retention { required int32 years; required int32 months; required int32 weeks;
                             required int32 days; required int64 hours; required int64 minutes;
                             required int64 seconds; required int64 milliseconds;
                             required int64 ticks; required int64 nanoseconds }
}
optional group Maybe { …same as Reading… }
optional group History (LIST) {
  repeated element: optional group {
    required double Value
    optional group At { required int32 days_since_unix_epoch; required int64 nanosecond_of_day }
    optional int64  Time
    optional group Retention { … }
  }
}
```

### 6.4 Worked example: your own adapter whose storage holds NodaTime fields

```csharp
public sealed record Appointment(Instant At, LocalDate Day, string Title);   // your domain type

public readonly record struct AppointmentStorage(Instant At, LocalDate Day, string Title);

[ParquetTypeAdapter(typeof(Appointment), typeof(AppointmentStorage))]
public static class AppointmentAdapter
{
    public static AppointmentStorage ToStorage(Appointment v) => new(v.At, v.Day, v.Title);
    public static Appointment FromStorage(AppointmentStorage v) => new(v.At, v.Day, v.Title);
}

[ParquetSerializable]
public partial record Agenda
{
    [ParquetAdapter(typeof(AppointmentAdapter))] public Appointment Main { get; init; } = new(default, default, "");
    [ParquetAdapter(typeof(AppointmentAdapter))] public List<Appointment> All { get; init; } = [];
}
```

```text
optional group Main {
  optional group At  { required int32 days_since_unix_epoch; required int64 nanosecond_of_day }
  optional group Day { required int32 day_number; required string calendar_id }
  required string Title
}
optional group All (LIST) { repeated element: optional group { …same three members… } }
```

The storage record needs no attribute and no `partial`. Its `Instant` and `LocalDate` fields are
converted by the NodaTime adapters in turn. What is **not** followed is a storage *type* that
itself needs an adapter (an adapter chain): write the storage record in terms of types that
already map.

### 6.5 Worked example: collections of NodaTime values

```csharp
[ParquetSerializable]
public partial record Timeline
{
    public List<Instant> Instants { get; init; } = [];          // element group
    public IReadOnlyList<LocalTime> Times { get; init; } = [];  // element leaf (int64)
    public List<LocalDate?> Dates { get; init; } = [];          // null elements kept
    public List<ZonedDateTime> Zoned { get; init; } = [];       // element group → instant group
    public List<Interval>? Windows { get; init; }               // element group → start?/end? groups
    public DateInterval?[] Ranges { get; init; } = [];          // element group → start/end date groups

    [ParquetAdapter(typeof(InstantAsUnixNanosecondsAdapter))]   // explicit adapter on the collection
    public List<Instant> Nanos { get; init; } = [];             // applies per element → int64
}
```

### 6.6 Generic types

One adapter can serve every construction of a generic type: `Id<Order>`, `Id<Customer>`,
`List<Id<Order>>`. See [document 44 §A.4a](./44-TYPE-ADAPTERS.md). A generic storage record
(`TaggedStorage<T>`) is closed per member and treated as an ordinary group, with the same depth
rules.

---

## 7. Which read/write APIs each shape gets

| API | Flat models (leaves only, incl. scalar-adapted) | Models with groups or lists |
|---|---|---|
| `WriteParquetAsync`, `WriteParquetBatchedAsync`, streaming write | ✓ | ✓ |
| `ReadParquetAsync`, `ReadParquetStreamAsync`, `ReadParquetParallelAsync`, memory reads | ✓ | ✓ |
| Row-group pruning predicates | ✓ on primitive and string columns | ✓ on the model's flat primitive columns; groups and lists are skipped |
| `[ParquetSortKey]` lookups | ✓ non-null integral, float, double, `DateTime` | the key must be a flat root column (`PARQ014`) |
| `ReadParquetBatchesAsync` (SoA `ColumnBatch`) | ✓ | — not emitted |
| Columnar hand-off batch | ✓ | — not emitted |
| Apache Arrow `RecordBatch` bridge | ✓ when every column has an Arrow mapping | — not emitted |

An adapted **root** member is read and written through a generated internal
`<Member>ParquetStorage` property. For flat models, the column-named APIs above expose that name
and the storage type (e.g. a `ReadOnlyMemory<long>` for a `LocalTime` column).

---

## 8. Backends and feature levels

| Configuration | Leaves | Groups | Lists | Scalar adapters | Group adapters |
|---|---|---|---|---|---|
| `Parquet.SourceGenerator`, feature level 2 (default) or 3 | ✓ | ✓ | ✓ | ✓ | ✓ |
| `Parquet.SourceGenerator`, `Level1Flat` | ✓ | ✗ `PARQ006` | ✗ `PARQ006` | ✓ | ✗ `PARQ018` |
| `Parquet.SourceGenerator.Legacy` (Parquet.Net 4.x/5.x) | ✓ | ✗ `PARQ006` | ✗ `PARQ006` | ✓ | ✗ `PARQ018` |

Evidence: `FeatureLevelOneIsFlatOnly`, `TheLegacyBackendIsFlatWithScalarAdapters`. Feature
levels are described in [document 29](./29-FEATURE-LEVELS.md).

---

## 9. When something is rejected

| Diagnostic | You hit it when… | Typical fix |
|---|---|---|
| `PARQ006` | the type has no mapping here: an unsupported leaf, a type without `[ParquetSerializable]`, a map or set, a list in a group, a list of lists, or an element nested too deep | add an adapter, add the attribute, or flatten the shape |
| `PARQ010` | a nested `[ParquetSerializable]` type is generic | close it, or use a generic adapter |
| `PARQ012` | nested types form a cycle | break the cycle, or `[ParquetIgnore]` one side |
| `PARQ013` | more than six group levels below the row | flatten, or `[ParquetIgnore]` the deep member |
| `PARQ016`–`PARQ019` | an adapter is malformed, ambiguous, has an unrepresentable surrogate (including a list element nested too deep, or a group surrogate on a flat backend), or tries to override a built-in | see [document 13](./13-COMPILER-DIAGNOSTICS.md) |

All of these are reported on the member that caused them, at compile time. A rejected shape never
produces generated code that fails to compile.

---

## 10. Evidence

| Section | Tests |
|---|---|
| §3–§5, §6.1–6.2, §8 | `SerializationShapeMatrixTests` (one case per bracketed id) |
| §4 round trips | `NestedStructRoundTripTests`, `NestedListRoundTripTests` |
| §6.3–6.4 | `NestedAdapterRoundTripTests` |
| §6.5 | `AdaptedCollectionRoundTripTests`, `NestedAdapterRoundTripTests.NodaTimeTypesWhoseStorageNestsAGroupWorkAsListElements` |
| §6.6 | `AdaptedCollectionRoundTripTests.GenericAdaptersCloseOverEachMemberType` |
| AOT | `test/Parquet.SourceGenerator.AotTest` (`NodaTimeChecks`) |
