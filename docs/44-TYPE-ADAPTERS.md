# 44 - Type Adapters

> **Status:** Implemented in `Parquet.SourceGenerator` (first version, §15 scope). The Arrow
> generator implements the same model independently; this document is the shared contract.
> **Implementation notes for this repository:** [§A — As implemented](#a--as-implemented-in-parquetsourcegenerator)
> at the end of this document. The NodaTime reference package is [document 45](./45-NODATIME.md).

The original design follows unchanged, so the two projects keep reviewing against the same text.

---


## 1. Design principle

Both generators need the same kind of extensibility:

```text
domain CLR type
      ↓
compile-time adapter
      ↓
backend-supported surrogate
      ↓
backend-specific planning and emission
```

Examples include NodaTime types, strongly typed IDs, ULIDs, money/value objects, IP addresses, UnitsNet quantities, and application-specific value types.

The generators should not grow a permanent hard-coded type-classification branch for every popular library. At the same time, they should **not share adapter implementation code yet**.

```text
Parquet.SourceGenerator                Arrow.SourceGenerator
        │                                      │
        ├─ own adapter discovery               ├─ own adapter discovery
        ├─ own diagnostics                     ├─ own diagnostics
        ├─ own adapter model                   ├─ own adapter model
        └─ own emission                        └─ own emission

                  shared by convention:
              concepts + API shape + semantics
```

This deliberately accepts some duplication so each design can evolve under real backend constraints before anything is extracted.

## 2. Core abstraction

An adapter declares that a **domain type** can be represented by a **surrogate type** the backend generator already understands:

```text
TDomain ↔ TSurrogate
```

The surrogate can be:

1. a scalar/leaf representation; or
2. a structural representation composed of fields the generator can recursively map.

This is intentionally more general than “convert everything to `long`”.

## 3. Conversion surface: static extension methods

Adapters expose ordinary static extension methods:

```csharp
public static TSurrogate ToStorage(this TDomain value);
public static TDomain FromStorage(this TSurrogate value);
```

The generator resolves the pair at compile time and emits fully-qualified direct calls:

```csharp
global::MyAdapters.InstantAdapter.ToStorage(value);
```

This preserves the desirable properties of the existing generators:

- no reflection;
- no runtime `Type` → converter dictionary;
- no required virtual dispatch;
- no `Expression.Compile`;
- no `Activator`;
- Native AOT friendly;
- trimming friendly;
- static methods remain visible to JIT/AOT inlining;
- conversion failures remain ordinary direct exceptions.

## 4. Mirrored API shape, independent contracts

The projects should mirror the shape while using backend-specific attributes.

### Parquet

```csharp
[assembly: ParquetTypeAdapter(typeof(
    MyCompany.ParquetAdapters.InstantAdapter))]

namespace MyCompany.ParquetAdapters;

[ParquetTypeAdapter(
    sourceType: typeof(NodaTime.Instant),
    surrogateType: typeof(InstantStorage))]
public static class InstantAdapter
{
    public static InstantStorage ToStorage(this NodaTime.Instant value)
        => ...;

    public static NodaTime.Instant FromStorage(this InstantStorage value)
        => ...;
}
```

### Arrow

```csharp
[assembly: ArrowTypeAdapter(typeof(
    MyCompany.ArrowAdapters.InstantAdapter))]

namespace MyCompany.ArrowAdapters;

[ArrowTypeAdapter(
    sourceType: typeof(NodaTime.Instant),
    surrogateType: typeof(InstantStorage))]
public static class InstantAdapter
{
    public static InstantStorage ToStorage(this NodaTime.Instant value)
        => ...;

    public static NodaTime.Instant FromStorage(this InstantStorage value)
        => ...;
}
```

The exact names can change. What should stay aligned is:

```text
assembly registration
    ↓
adapter descriptor
    ↓
domain type
surrogate type
ToStorage
FromStorage
```

No binary compatibility between the two projects is implied.

## 5. Explicit discovery

The generators should **not scan every type in every referenced assembly** looking for extension methods.

Preferred registration:

```csharp
[assembly: ParquetTypeAdapter(typeof(InstantAdapter))]
[assembly: ParquetTypeAdapter(typeof(LocalDateAdapter))]
```

and equivalently for Arrow.

An integration package can carry those assembly attributes itself, so referencing the package automatically makes its adapters discoverable.

The generator inspects only:

- adapter registration attributes on the consuming assembly; and
- adapter registration attributes on referenced assemblies.

It never needs to enumerate every type in those assemblies.

## 6. Compile-time validation

For each adapter, validate:

- adapter type is accessible;
- source type resolves;
- surrogate type resolves;
- exactly one write conversion matches `TDomain -> TSurrogate`;
- exactly one read conversion matches `TSurrogate -> TDomain`;
- conversion methods are static;
- surrogate is representable;
- no adapter cycle exists;
- no ambiguous default exists.

A malformed adapter should produce a generator diagnostic at registration/use, not broken generated C#.

## 7. Nullability belongs to the generator

The first adapter contract should be non-null:

```csharp
InstantStorage ToStorage(this Instant value)
Instant FromStorage(this InstantStorage value)
```

The generator handles nullable members outside the adapter:

```text
nullable domain member
       ↓
generator null handling
       ↓ present
adapter conversion
       ↓
surrogate
```

That keeps Arrow validity bitmaps and Parquet definition levels in the backend where they belong.

A future “adapter handles nulls itself” mode can be added only if a real use case requires it.

## 8. Surrogate types are the extensibility mechanism

### Scalar surrogate

```csharp
public static long ToStorage(this DeviceId value);
```

The generator already knows `long`.

### Structural surrogate

```csharp
public readonly record struct MoneyStorage(
    decimal Amount,
    string Currency);

public static MoneyStorage ToStorage(this Money value);
public static Money FromStorage(this MoneyStorage value);
```

The backend recursively plans `MoneyStorage`.

```text
Money
  ↓
MoneyStorage
  ├─ Amount
  └─ Currency
```

Parquet can emit a group; Arrow can emit a `StructArray`. No second schema DSL is needed.

## 9. Surrogates do not need root serialization attributes

A surrogate is implementation representation, not a user serialization root.

The user should not have to mark it `[ParquetSerializable]` or the Arrow equivalent.

Adapter resolution enters explicit surrogate-planning mode:

```text
member
  ↓ built-in unsupported
adapter found
  ↓
surrogate type
  ↓
recursive backend classification
```

Surrogates still obey:

- cycle checks;
- nesting bounds;
- accessibility rules;
- deterministic member ordering;
- backend shape restrictions.

## 10. Adapter chains

The long-term model may permit:

```text
DomainA
  ↓ adapter
DomainB
  ↓ adapter
int64
```

but must track the resolution path and reject cycles.

For the first implementation, **one adapter hop only** is a sensible restriction. Structural surrogates already cover most useful cases without recursive adapter chains.

## 11. Deterministic precedence

Recommended precedence:

1. **member-level explicit adapter**
2. **project-level explicit adapter override**
3. **generator built-in mapping**
4. **referenced adapter-package default**
5. unsupported-type diagnostic

This means installing an adapter package can make an otherwise unsupported type work but cannot silently replace the generator’s established representation for built-in types.

Overriding a built-in should require explicit intent, e.g.:

```csharp
[ParquetAdapter(typeof(CustomDateTimeAdapter))]
public DateTime HappenedAt { get; init; }
```

The Arrow project should mirror the final shape.

## 12. Ambiguity is a compiler error

If two referenced packages both register defaults for the same source type, do not choose by reference order or assembly name.

Report both registrations and require an explicit choice.

This matters especially for temporal, currency, identifier, and units types where multiple valid storage representations may exist.

## 13. Lossless-by-default policy

A default adapter must preserve the represented domain value.

It must not silently:

- reduce precision;
- narrow valid range;
- discard calendar identity;
- discard time-zone identity;
- discard offset identity;
- normalize where normalization changes meaning;
- reinterpret local time as a global instant;
- discard currency/units/domain metadata.

If a backend-native type has a narrower semantic domain, expose that as an **explicit interoperability adapter** instead.

Example:

```text
Instant
  default → lossless structural surrogate

Instant
  explicit → timestamp[ns, UTC]
             + range validation
```

## 14. Backend semantics remain backend-owned

Adapters primarily describe a domain-to-surrogate conversion.

They should not force Parquet concepts into Arrow or Arrow concepts into Parquet.

```text
Instant -> InstantStorage
             │
      ┌──────┴──────┐
      ▼             ▼
   Parquet         Arrow
   planning        planning
```

Either backend may later add specialised native descriptors, but those stay backend-specific.

## 15. First-version scope

### Include

- exact closed source types;
- static adapter classes;
- explicit assembly registration;
- `ToStorage` / `FromStorage`;
- generator-managed nullability;
- scalar surrogates;
- structural surrogates where backend compound support exists;
- direct static emitted calls;
- compile-time diagnostics;
- deterministic precedence.

### Defer

- open generic adapters such as `StronglyTypedId<T>`;
- dependency-injected adapters;
- runtime converter selection;
- adapters whose behaviour changes by row;
- dynamic schema generation;
- automatic extension-method scanning;
- collection-level adapters;
- shared cross-generator implementation package.

## 16. Context-dependent adapters

Some values need external context to reconstruct. Time zones are the canonical case:

```text
stored zone ID
    +
IDateTimeZoneProvider
    ↓
DateTimeZone
```

Do not hide service lookup inside the basic adapter contract.

The first contract stays:

```text
pure value ↔ pure surrogate
```

A later contextual contract might look conceptually like:

```csharp
public static ZonedDateTime FromStorage(
    this ZonedDateTimeStorage value,
    in NodaTimeReadContext context);
```

but that should be designed from actual usage after the simple model is proven.

## 17. Incremental-generator requirements

Adapter support must not cache Roslyn-heavy objects.

Do not retain these in cached adapter-plan values:

- `Compilation`;
- `GeneratorSyntaxContext`;
- `ITypeSymbol`;
- `IMethodSymbol`;
- `Location`.

Resolve symbols during discovery, then persist value-like data such as:

```text
source metadata name
surrogate metadata name
adapter fully-qualified type name
write method name
read method name
contract version
logical flags
```

The adapter model should be value-equatable.

A package/reference change should invalidate adapter resolution; unrelated source edits should not invalidate every generated model.

## 18. Diagnostics

Each project owns its own IDs, but align the concepts:

- malformed adapter registration;
- missing `ToStorage`;
- missing `FromStorage`;
- invalid conversion signature;
- ambiguous source mapping;
- unsupported surrogate;
- adapter cycle;
- inaccessible adapter;
- attempted built-in override without explicit opt-in;
- unsupported adapter contract version.

Diagnostics should point to the member or registration that caused resolution.

## 19. Generated-source safety

All adapter-derived names and metadata must use the generator’s standard escaping layer for:

- identifiers;
- C# string literals;
- comments;
- XML documentation.

Adapters must not reintroduce raw interpolation vulnerabilities.

## 20. Versioning

Each generator’s own attribute assembly is the adapter contract.

If protocol versioning is required later, use a small integer contract version:

```csharp
[ParquetTypeAdapter(..., ContractVersion = 1)]
```

Do not create a shared runtime abstraction merely for version negotiation.

## 21. Testing

Every implementation needs:

### Discovery
- no package → unsupported;
- add package → adapter available;
- remove package → unsupported again;
- unrelated reference changes preserve unrelated generated output.

### Diagnostics
- malformed registration;
- missing/reversed conversion;
- inaccessible adapter;
- duplicate defaults;
- cycle;
- unsupported surrogate.

### Semantics
- non-null round trip;
- nullable round trip;
- boundary values;
- structural surrogate round trip.

### Toolchain
- Native AOT;
- trimming;
- generated-code semantic compilation;
- incremental invalidation checks;
- legacy backend where applicable.

### Performance
Compare:

```text
built-in mapping
adapter mapping
hand-written equivalent
```

The adapter lookup mechanism should have no runtime cost. Any overhead should come from the actual conversion required by the domain type.

## 22. Fit with Parquet.SourceGenerator today

Today the Parquet parser largely classifies supported CLR values into `PropertyKind` values such as primitive, decimal, `DateTime`, `Guid`, enum, list, struct, and map.

Adapters should sit **before the unsupported-type failure**:

```text
member symbol
   ↓
nullable unwrap
   ↓
built-in classification succeeds?
   ├─ yes → existing path
   └─ no
       ↓
   adapter resolution
       ↓
   surrogate
       ↓
   ordinary backend classification
```

Longer term this can evolve toward:

```text
CLR symbol
   ↓
logical/surrogate resolution
   ↓
Primitive | Logical | Struct | List | Map
   ↓
backend plan
```

Arrow.SourceGenerator can independently evolve to the same conceptual stages.

## 23. What the projects should share now

Share by documentation and review:

- terminology;
- registration shape;
- `ToStorage` / `FromStorage` model;
- deterministic precedence;
- lossless-default rule;
- nullability ownership;
- surrogate concept;
- ambiguity behaviour;
- test categories;
- NodaTime semantic decisions.

Do **not** share yet:

- attributes assembly;
- parser/model records;
- Roslyn discovery code;
- diagnostics implementation;
- emitters;
- backend plans;
- buffer ownership;
- runtime helper packages.

## 24. Success criterion

Adding support for a domain library should no longer require changing the generator core.

The workflow becomes:

```text
create adapter package
      ↓
declare adapters + surrogates
      ↓
reference package
      ↓
existing generator understands the model
```

No reflection, no runtime registry, and no new hard-coded core type case for that external library.

---

## A — As implemented in Parquet.SourceGenerator

### A.1 The contract

Both attribute forms live in `Parquet.SourceGenerator.Attributes`:

```csharp
// Descriptor, on the adapter class.
[ParquetTypeAdapter(sourceType: typeof(Money), surrogateType: typeof(MoneyStorage))]
public static class MoneyAdapter
{
    public static MoneyStorage ToStorage(this Money value) => new(value.Amount, value.Currency);
    public static Money FromStorage(this MoneyStorage value) => new(value.Amount, value.Currency);
}

// Default registration, on the consuming assembly or on an adapter package.
[assembly: ParquetTypeAdapter(typeof(MoneyAdapter))]

// Explicit per-member selection — the only way to override a built-in mapping.
[ParquetAdapter(typeof(TicksAdapter))]
public DateTime HappenedAt { get; init; }
```

Conversions are `static` methods named `ToStorage` / `FromStorage`, taking exactly the source
(resp. surrogate) type by value or `in`, returning exactly the other. Extension methods qualify
but are not required. `ContractVersion` defaults to `1`, the only version.

### A.2 How generated code reaches the adapter: storage shadows

The generator does not thread adapter calls through each emitter. Instead, for every adapted
member it adds an **internal storage property** to the partial target type, in its own file
(`<Type>.ParquetAdapters.g.cs`):

```csharp
partial record Reading
{
    [DebuggerBrowsable(Never)] [EditorBrowsable(Never)]
    internal global::MyApp.MoneyStorage? RefundParquetStorage
    {
        get { if (this.Refund is { } __value) return global::MyApp.MoneyAdapter.ToStorage(__value); return null; }
        init { if (value is { } __storage) this.Refund = global::MyApp.MoneyAdapter.FromStorage(__storage); else this.Refund = null; }
    }
}
```

The member's column model is then the **surrogate's** model under the shadow's name, with the
member's own column name, order and leaf annotations (`[ParquetColumn]`, `[ParquetTimestamp]`,
`[ParquetDecimal]`) carried across. Every emitter — row read/write, columnar batches, pruning,
the Arrow bridge, the legacy backend — sees an ordinary member of a type it already maps, and the
only adapter calls in the whole output are the two direct static calls in the shadow's accessors.
That is the §3 property list (no reflection, no registry, no virtual dispatch, inlinable) with no
per-emitter adapter code to keep consistent.

Consequences worth knowing:

- Adapted members require the declaring type and every containing type to be `partial` (the
  target already must be; containing types are checked and reported as PARQ016).
- The shadow name is `<Member>ParquetStorage`. A member already using that name is PARQ016.
- `required` members cannot be adapted (the reader sets the shadow, not the required member).
- Adapted collection *elements* need none of this: they convert inline (§A.4b).
- Emitted public API that is named after columns — columnar batch fields, row-group statistics
  members, sort-key lookups — is named after the shadow and typed as the surrogate, because
  that is what those APIs actually carry.
- An inherited adapted member gets its shadow from its declaring base when that base is itself a
  `[ParquetSerializable]` target, so the derived type does not hide it.
- Structural surrogates are read member-by-member, so a group surrogate's `ToStorage` runs once
  per leaf on write. Adapter conversions should be cheap and pure (§3 already requires the latter).
- The single-field blittable fast path is disabled for any type with an adapted member.

### A.3 Resolution and precedence

Adapter resolution runs after nullable unwrapping and before PARQ006 (§22). Precedence (§11):

1. `[ParquetAdapter]` on the member — always wins, including over built-in mappings.
2. A registration on the consuming assembly — for types the generator does **not** map itself.
3. The generator's built-in mapping.
4. A registration carried by a referenced assembly.
5. PARQ006.

A consuming-assembly registration for a built-in type is ignored with warning **PARQ019**;
referenced-package defaults for built-in types lose silently. Two registrations at the same
level for one source type are **PARQ017**, listing both — never resolved by order (§12).

Discovery reads only `[assembly: ParquetTypeAdapter]` on the consuming assembly and its
references. The registry is memoised per consuming assembly symbol (new per compilation) and
holds symbols only for the parse; the cached model holds strings (§17). An incrementality test
asserts unrelated edits leave every generator step cached.

### A.4 Surrogates

- A surrogate the generator maps as a leaf (`long`, `string`, `DateTime`, `decimal`, ...) becomes
  a plain column, on both backends.
- Any other class or struct is planned recursively as a **group** — without
  `[ParquetSerializable]` (§9), with the ordinary cycle, depth, accessibility, parameterless
  constructor and ordering rules. Groups need the Parquet.Net 6 backend at feature level 2 or
  later; elsewhere they are **PARQ018**.
- Members *inside* a surrogate resolve adapters like any other member (§A.4c), so an
  application surrogate can hold `Instant`s. Adapter **chains** — a surrogate type that itself
  needs an adapter — are still one hop (§10); a cycle through surrogates is PARQ012. Nested
  classes and structs inside a surrogate are planned as groups without `[ParquetSerializable]`.
- `Nullable<T>` source or surrogate types are refused (§7): the generator owns nulls.
- A **closed construction of a generic surrogate** (`TaggedStorage<int>`) is a concrete group and
  is planned like any other; open generic `[ParquetSerializable]` children remain PARQ010.
- Deferred: context-dependent adapters, project-wide overrides of a default, adapters whose
  *source* is itself a collection type, and adapted map values (maps are not in the v6 pipeline).

### A.4a Generic adapters

§15 deferred open generic adapters; they are implemented. A descriptor may name an **open
generic source** — `typeof(Id<>)` — and then serves every closed construction of it:

```csharp
// Generic conversion methods of the source's arity...
[ParquetTypeAdapter(typeof(Id<>), typeof(Guid))]
public static class IdAdapter
{
    public static Guid ToStorage<T>(this Id<T> value) => value.Value;
    public static Id<T> FromStorage<T>(this Guid value) => new(value);
}

// ...or a generic adapter class of that arity, with a same-arity open surrogate.
[ParquetTypeAdapter(typeof(Tagged<>), typeof(TaggedStorage<>))]
public static class TaggedAdapter<T>
{
    public static TaggedStorage<T> ToStorage(Tagged<T> value) => new(value.Value, value.Tag);
    public static Tagged<T> FromStorage(TaggedStorage<T> value) => new(value.Value, value.Tag);
}

[assembly: ParquetTypeAdapter(typeof(IdAdapter))]
[assembly: ParquetTypeAdapter(typeof(TaggedAdapter<>))]
```

For each member the resolver **closes** the descriptor over the member's type arguments — it
constructs the adapter class or the conversion methods, constructs an open surrogate with the
same arguments (a closed surrogate such as `Guid` is used as is), and validates the closed
conversions exactly as a non-generic adapter's. Generic parameter constraints (`class`,
`struct`, `unmanaged`, `new()`, base types and interfaces) are checked there too, so a
violating type argument is PARQ016 at the member, never a compile error in generated code. The
generated calls spell the construction out: `global::IdAdapter.ToStorage<global::Order>(...)`,
`global::TaggedAdapter<int>.FromStorage(...)`.

Lookup takes an exact registration first and the open definition second, so a closed adapter
for one construction (`[ParquetTypeAdapter(typeof(Id<Special>), typeof(string))]`) specialises
a generic default without ambiguity.

### A.4b Adapted collection elements

A list, array or other supported collection member (`T[]`, `List<T>`, `IList<T>`,
`ICollection<T>`, `IReadOnlyCollection<T>`, `IReadOnlyList<T>`, `IEnumerable<T>`) whose element
type `T` (or `T?`) resolves to an adapter is an ordinary list column of the surrogate. The list
emitter converts **inline** — `ToStorage` per element as it shreds, `FromStorage` per element as
it rebuilds the lane — so there is no shadow member and no per-row copy of the collection.
Precedence is the same as for a member, applied to the element type: an explicit
`[ParquetAdapter]` on the collection member whose source is the element type (this is how a
built-in element type such as `DateTime` is re-stored), then the project's defaults, then
packages'. Generic adapters apply to elements too (`List<Id<Order>>`).

Element shapes, which follow the list emitter's own:

- **Scalar surrogates** — any element surrogate the generator maps as a leaf.
- **Group surrogates** of single-column members — reference or value type. Lifting the
  value-type restriction here also made plain `List<SomeStruct>` / `SomeStruct?[]` members of
  `[ParquetSerializable]` structs work; a non-nullable value-type element has no null rung.
- **Groups one level inside an element** — an element member that is a nested type or an
  adapted group (`List<Measurement>` where `Measurement.At` is an `Instant`; `ZonedDateTime`,
  `Interval` and `DateInterval` elements).
- **Not yet:** deeper nesting inside elements, and lists below the top level — PARQ018 names the
  reason.

### A.4c Adapted members nested in custom types

An adapted member anywhere below the root — a field of a `[ParquetSerializable]` child type, of
a list element, or of an application surrogate — is converted **inline**, like a collection
element: `ToStorage` where the write ladder reads the member off its parent, `FromStorage` where
the reader assigns the rebuilt value to its parent, nullable members converting only when
present. The storage shadow of §A.2 is used only for the root type's own members, where every
emitter (row, columnar, pruning, Arrow) reads them; nested types therefore need no shadow and no
`partial` beyond what `[ParquetSerializable]` already asks of them, and a surrogate — which
nobody can reopen — can carry adapted fields.

```csharp
[ParquetSerializable]
public partial record Measurement
{
    public double Value { get; init; }
    public Instant At { get; init; }        // group inside a group
    public LocalTime? Time { get; init; }   // scalar, nullable
}

[ParquetSerializable]
public partial record SensorRow
{
    public Measurement Reading { get; init; } = new();
    public List<Measurement> History { get; init; } = [];   // group inside a list element
}
```

### A.5 Diagnostics

| Concept (§18) | ID |
|---|---|
| Malformed registration, missing / reversed / ambiguous conversion, bad signature, inaccessible adapter, wrong source type, unsupported contract version, self-cycle, unshadowable member, generic arity mismatch, violated generic constraint | **PARQ016** |
| Two defaults for one source type | **PARQ017** |
| Unsupported surrogate (including groups on the legacy backend, element groups with nested groups, lists where lists are not emitted) | **PARQ018** |
| Registration would override a built-in without explicit opt-in | **PARQ019** (warning) |

All four are reported at the member that resolved to the adapter. See
[document 13](./13-COMPILER-DIAGNOSTICS.md).

### A.6 Evidence

- `TypeAdapterDiagnosticTests` — discovery with/without a package, precedence, ambiguity, every
  malformed shape, legacy scalar vs group surrogates, nested/struct/partial chains, generated-code
  compilation, incremental caching.
- `NodaTimeAdapterRoundTripTests` — the §21 semantic categories through real emitted code.
- `AdaptedCollectionRoundTripTests` — every collection shape with scalar and group surrogates,
  nullable elements and collections, explicit element adapters, generic adapters closed per
  member (including an exact specialisation), and unadapted value-type struct elements.
- `test/Parquet.SourceGenerator.AotTest` — adapted members in a published native binary.
- `test/PackageConsumption` — the packed generator discovering the packed NodaTime package.
