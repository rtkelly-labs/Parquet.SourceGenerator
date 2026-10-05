# API Change Ledger

Every line added to a governed catalogue — an emitted-API `*.api.txt`, `src/api/seams.txt`, or a
`PublicAPI.Unshipped.txt` — needs an entry here classifying its semver impact. Newest first.

The rule, the three surfaces and the author process are in
[18 - API Change Contract](../18-API-CHANGE-CONTRACT.md). The buckets are `additive-minor`,
`breaking-major`, `internal` and `generated-shape`.

> **Everything dated before 2026-09-10 was catalogued retrospectively.** The single
> `0.0.x inherited surface` entry below covers the whole surface that existed when this contract
> was introduced. Those members were **not** reviewed under this contract, no per-member rationale
> was written for them, and none should be read as approved. That is the point of seeding it this
> way: the freeze diff for [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230)
> is then exactly the set of entries dated on or after 2026-09-10, which *were* reviewed.

<!-- Add new entries directly below this line, newest first. -->

### 2026-10-05 — Internalize sorted-key reads and `ParquetPruneStatistics`, express key lookups through `Where` (#584)

- **Surface:** unshipped
- **Semver:** breaking-major
- **Issue:** [#584](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/584), part of tracker
  [#477](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/477)
- **Change:** `ParquetPruneStatistics` changed from `public` to `internal` (removing 16 entries from
  `PublicAPI.Unshipped.txt`). `ReadParquetBy{Key}Async` and `ReadParquet{Key}RangeAsync` emitted on
  `<Model>ParquetExtensions` are removed. Key point-lookups and range queries are expressed using the
  fluent `<Model>Parquet.From(...).Where(meta => meta.<Key>.MayContain(...))` and `MayContainBetween(...)`
  grammar.
- **Rationale:** The emitted `ReadParquetBy*` and `ReadParquet*RangeAsync` methods were the last remaining
  `Stream`-only and `List<T>`-returning reads, taking custom statistics parameters where the rest of the
  read surface accepts options and tokens. Row-group zone-map pushdown is already uniformly available via
  `.Where(...)` over `<Model>RowGroupMetadata` using `MayContain*` helpers, which operates over the footer
  statistics without decompressing skipped row groups. Internalizing `ParquetPruneStatistics` and removing
  the sorted read overloads contracts the public API surface (-16 package API lines, -4 emitted members per
  sorted model) while unifying all row-group filtering under the fluent reader.

### 2026-10-05 — Collapse `ParquetGeneratorFeatureLevel` and `ParquetGeneratorOptionsAttribute` to `ParquetGeneratorFlatOnly` (#587)

- **Surface:** unshipped
- **Semver:** breaking-major
- **Issue:** [#587](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/587), part of tracker
  [#477](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/477)
- **Change:** Removed `ParquetGeneratorFeatureLevel` enum (3 values: `Level1Flat`, `Level2CompoundPreview`,
  `Level3ModernCSharp`) and `ParquetGeneratorOptionsAttribute` from `Parquet.SourceGenerator.Attributes`.
  Replaced with the MSBuild property `<ParquetGeneratorFlatOnly>true</ParquetGeneratorFlatOnly>`. Retired
  diagnostic `PARQ015`.
- **Rationale:** The feature-level enum and assembly attribute were redundant mechanisms to spell a single
  binary behavior toggle: only `Level1Flat` ever altered emitter behavior (by disabling compound types).
  `Level2CompoundPreview` was an unnecessary preview label for default supported functionality, and
  `Level3ModernCSharp` was unused. Collapsing this to a single MSBuild boolean property removes 8 unshipped
  API lines, eliminates the assembly attribute scan, and avoids confusing multi-tiered feature levels.

### 2026-10-05 — Hide `ParquetColumnStatistics<T>` constructor and `FromRaw` with `[EditorBrowsable(Never)]` (#586)

- **Surface:** unshipped
- **Semver:** breaking-major
- **Issue:** [#586](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/586), part of tracker
  [#477](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/477)
- **Change:** `ParquetColumnStatistics(bool, T, bool, T, long?, long?)` constructor, the static class
  `ParquetColumnStatistics`, and its `FromRaw<T>(object?, object?, long?, long?)` projection helper are
  annotated with `[EditorBrowsable(EditorBrowsableState.Never)]`.
- **Rationale:** `ParquetColumnStatistics<T>` is a shared runtime type whose query surface (`Min`, `Max`,
  `NullCount`, `DistinctCount`, `HasMinMax`, `IsKnownNonNull`, `MayContain*`) is designed for row-group
  filtering expressions in `.Where(...)`. Generated reader code calls `FromRaw<T>` from the consumer
  assembly as row groups are inspected, and `FromRaw<T>` invokes the constructor in the attributes assembly.
  `[EditorBrowsable(EditorBrowsableState.Never)]` hides these members from IntelliSense without preventing direct
  calls, preserving the required cross-assembly plumbing without introducing redundant per-assembly emitted helper types.
- **Classification of Equality:** `ParquetColumnStatistics<T>` is a `readonly struct` and retains
  `IEquatable<ParquetColumnStatistics<T>>`, `Equals`, `GetHashCode`, `==`, and `!=` for standard .NET
  value-type semantics and CA1815 compliance.

### 2026-10-05 — Internalize `ParquetSchema Schema` field across emitted extensions (#585)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#585](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/585), part of tracker
  [#477](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/477)
- **Change:** `public static readonly global::Parquet.Schema.ParquetSchema Schema` on generated
  `<Model>ParquetExtensions` is now `internal static readonly`.
- **Rationale:** `ParquetSchema` is an external dependency type that differs per backend (Parquet.Net 6
  vs 4.25), so exposing it on the public contract leaked the backend package choice into consumer API
  surfaces. Internalizing the field hides the backend dependency while keeping the schema definition
  accessible for intra-assembly deserialization and internal helpers.

### 2026-10-03 — `ParquetSerializerOptions.MaxAllocationBytes`, a byte budget for what a read allocates (#361)

- **Surface:** unshipped
- **Semver:** additive-minor
- **Issue:** [#361](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/361), part of tracker
  [#477](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/477)
- **Change:** adds `long MaxAllocationBytes { get; set; }` to `ParquetSerializerOptions`, default
  268,435,456 (256 MiB). The generated readers (modern and classic) multiply a row group's declared
  row count by the model's per-row buffer size and throw `InvalidDataException`, naming the option,
  before renting; repeated columns are checked as their chunks are read and added to the row group's running total.
- **Rationale:** `MaxAllocationValues` bounds a count, which is the same number for a `bool` and a
  `Guid` column and applies per column, so a roughly 1 KB file declaring ten million rows reserved
  about a gigabyte for a nine column model before any page was read. The issue's other proposal,
  rejecting a column whose `num_values` could not be encoded in its `total_uncompressed_size`, was
  not taken: RLE, dictionary, all-null and delta encodings legitimately carry millions of values in a
  few bytes (UPSTREAM_DEPENDENCY_LIMITATIONS.md). The only sound bound is the one the caller chooses.
- **Default:** 256 MiB per row group admits any realistic group (a million rows of a ten column model
  is about 100 MiB) and refuses the ten million rows a hostile footer can declare. A file with larger
  row groups raises the option; the error message says which.
- **Alternatives considered:** *dividing the budget across parallel workers* — not done: the budget is
  per row group and per worker, documented on the option, because the split would change a limit a
  caller sets for one read depending on the host's core count. *A hard-coded limit* — rejected: a
  legitimately large file must be readable.

### 2026-10-02 — `{T}ColumnarBatch` and the nested `ColumnBatch` become one top-level `{T}Batch` (#508)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#508](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/508), part of tracker
  [#505](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/505); ownership model
  [#369](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/369) (partly)
- **Change:** for each flat model, `public readonly struct {T}ColumnarBatch` (write input) and
  `{T}ParquetExtensions.ColumnBatch` (read item type, `ReadOnlySpan<T>` accessors named
  `<Column>Span`, plus `RowGroupIndex`) are removed. `public readonly struct {T}Batch` replaces both,
  at namespace scope. It keeps the write-side shape: a validating public constructor `{T}Batch(int
  rowCount, <one ReadOnlyMemory<…> per column, plus a ReadOnlyMemory<int> definition-levels lane after
  each nullable value column>)`, a get-only `RowCount` and one get-only `ReadOnlyMemory<…>` property
  per column (`<Column>DefinitionLevels` for the levels). The columnar `WriteParquetAsync(this
  {T}Batch batch, Stream, ParquetSerializerOptions?, CancellationToken)` now takes it, and the reader's
  `AsBatches()` yields it. Read-side consumers move from `ReadOnlySpan<T> x = batch.AmountSpan` to
  `batch.Amount.Span`; a nullable value column reads as packed values plus levels instead of
  `ReadOnlySpan<T?>`; text and binary columns read as inline-nullable `ReadOnlyMemory<char>` /
  `ReadOnlyMemory<byte>` per row instead of `string` / `byte[]`; `RowGroupIndex` is gone.
- **Rationale:** one name had meant two unrelated types, so what a reader yielded could not be handed
  to the columnar writer, and the near-identical names hid it. Layout A (packed values plus
  definition levels, inline-nullable text and binary) was chosen from the #561 experiment: cheapest
  to write, read and store for numerics, and storing strings packed cost 28-280 us per column. See
  [12](../12-BUFFER-REUSE-AND-EXTRACTION-STRATEGIES.md) §7.
- **Ownership:** a batch from `AsBatches()` is borrowed (valid until the next `MoveNextAsync`) and
  carries a lease the iterator expires before returning the pooled buffers; a kept batch throws
  `ObjectDisposedException` from every lane property, the fill methods and `WriteParquetAsync`. A
  batch a caller constructs never expires. This is lease/token validation from #369's candidate
  list, and the owner chose it as the stable 0.1 contract: using a batch or a lane after the
  enumerator advances is invalid; a `ReadOnlyMemory<T>` lane already copied out of a live batch is
  not checked yet (lane-level checking is #580, additive, no API change); lanes of a borrowed batch
  are not guaranteed to be array-backed; batches are not thread-safe; to keep data, copy each lane
  with `.ToArray()` into the public constructor. No escape method and no owned, callback or
  ref-counted variant in 0.1; an owned-batch terminal, `ToOwned()` and a callback form can be added
  later without a break.
- **Alternatives considered:** *`T?` spans for nullable columns (layout B)* — rejected: costs a
  transpose on write and doubles the width of small types. *A cached `T?` accessor (layout C)* —
  rejected: it allocated large arrays and was 2x slower under Server GC in the experiment; replaced
  by the explicit `Fill<Column>Nullable` method below. *Packed storage for strings* — rejected: the
  writer takes `ReadOnlyMemory<ReadOnlyMemory<char>?>` and packing costs 28-280 us per column.
  *Keep two types and add a converter* — rejected: that is the surface this entry removes.
  *Callback-scoped borrowing* (`Func<Batch, ValueTask>`) — not chosen: it gives up the `await
  foreach` shape and needs the lease anyway; not in 0.1 (can be added later without a break).
  *Reference counting* — rejected: the most surface for the same hole.
- **Fixes folded in:** #384 part a (a derived `<Column>DefinitionLevels` member no longer collides
  with a property of that name; it becomes `<Column>DefinitionLevels_`) and #550/#562's
  readonly-struct shape carries over.
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump.

### 2026-10-02 — added `{T}Batch.Fill<Column>Nullable(Span<T?> destination)` for each nullable value column (#508)

- **Surface:** emitted
- **Semver:** additive-minor
- **Issue:** [#508](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/508)
- **Change:** one `public void Fill<Column>Nullable(System.Span<T?> destination)` per nullable
  value column of a flat model, where `T` is the column's wire element type (the one its packed lane
  uses). It expands the packed values and definition levels into the caller's buffer, null where the
  row is null. It throws `ArgumentException` if the buffer is shorter than the row count and
  `InvalidOperationException` if the levels mark more rows present than the packed lane holds.
- **Rationale:** the packed layout is the cheapest to produce and consume, but a caller sometimes
  wants a row-aligned `T?` view. An explicit method makes the cost visible (an O(rows) pass and a
  buffer the caller owns); no existing member can express that without a cached property.
- **Alternatives considered:** *a cached `T?` property per column* — rejected: per-batch large
  allocations, 2x slower under Server GC (#561). *A `T?` span returned from a rented buffer* —
  rejected: it would hand the caller a second borrowed buffer to track. *Nothing, let callers write
  the loop* — rejected: the loop is easy to get wrong (the packed index only advances on present
  rows) and every consumer would carry a copy.
- **Open:** classification of this member and of `AsBatches()` under #482 (stable / optional /
  preview) is the owner's call.

### 2026-10-02 — renamed `{T}ParquetReader.Batches(CancellationToken)` to `AsBatches(CancellationToken)` (#507)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#507](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/507), part of tracker
  [#505](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/505)
- **Change:** `Batches(CancellationToken cancellationToken = default) ->
  IAsyncEnumerable<{T}ParquetExtensions.ColumnBatch>` is now `AsBatches(CancellationToken
  cancellationToken = default) -> IAsyncEnumerable<{T}Batch>`. The `NotSupportedException` rules
  (after `Parallel()`, after `Where(...)`) are unchanged, with the method name in the message updated.
- **Rationale:** "batch" is the columnar industry term (Arrow `RecordBatch`, Spark `ColumnarBatch`,
  pyarrow `iter_batches`), and `As…` marks a view or stream where `To…` marks an owned result
  (`ToArrayAsync`); a batch is a borrowed view, so `As` is the accurate prefix.
- **Alternatives considered:** `…Iterator`, `AsColumnBatches`, `AsColumns`, `AsRecordBatches`
  (reserved for real Arrow `RecordBatch` output); reasons in #507.
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump. Landed with the #508 entry above
  as the single breaking change #505 asks for.

### 2026-10-01 — `{T}ColumnarBatch` becomes a `readonly struct` with get-only properties and a validating constructor (#550)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#550](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/550), part of
  [#508](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/508) and tracker
  [#554](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/554)
- **Change:** for each flat model, `public struct {T}ColumnarBatch` with one public mutable field
  per column (plus `RowCount`) is now `public readonly struct {T}ColumnarBatch` with a get-only
  property per column and a public constructor `{T}ColumnarBatch(int rowCount, <one
  ReadOnlyMemory<…> per column in schema order, plus a definition-levels lane after each nullable
  value column>)`. The constructor throws `ArgumentOutOfRangeException` for a negative row count
  and `ArgumentException` (naming the column) for a lane shorter than the row count. The column
  layout is unchanged: packed values plus definition levels for nullable value columns,
  inline-nullable text and binary.
- **Rationale:** a mutable public struct let `RowCount` drift from the lanes it describes, and
  public fields cannot become properties later without a binary break (CA1051, S1104 in the
  generated-code analysis). Validating once at construction makes an inconsistent batch
  unrepresentable, so the write call no longer re-checks; `default(T)` is a valid empty batch.
- **Replaces** the earlier note that `{T}ColumnarBatch` fields stay public because they are how a
  caller builds the batch: the constructor is now how.
- **Not decided here:** unifying it with the read-side `ColumnBatch` and the nullable layout
  remain open in #508.
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump.

### 2026-09-23 — removed `ToListAsync(CancellationToken)` from the generated reader (#479)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#479](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/479) (0.1 contract
  tracker [#477](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/477))
- **Change:** `ToListAsync(CancellationToken cancellationToken = default) -> Task<List<T>>` is gone
  from every modern model. It existed on all four #217 state types, so 24 catalogue lines are
  removed (four per modern golden model) — they are counted in the #478 entry below, which removes
  the types that carried them. The stable terminals are `ToArrayAsync(CancellationToken)` and
  `AsAsyncEnumerable(CancellationToken)`; `Batches(CancellationToken)` is unchanged (#369).
- **Rationale:** `List<T>` versus `T[]` is a collection preference, not a storage-engine capability
  ([47](../47-0.1-CONTRACT-AND-DESIGN-GOALS.md) §4.2). The array terminal already pre-sizes from the
  footer row counts, so a caller who wants a list pays one copy (`.ToList()` /
  `new List<T>(array)`), which is what the memory and parallel `ToListAsync` paths already did
  internally. The stream `ToListAsync` was a second full materialising body per model that
  differed from the array body only in its collection type. With no terminal reaching them, the
  internal `ReadListCoreAsync` (both overloads) and `ReadParallelListCoreAsync` are no longer
  emitted; every golden `.g.cs` loses them.
- **Alternatives considered:** *Keep `ToListAsync` as a convenience forwarder over the array* —
  rejected: it is a permanent member that expresses no read capability, and every future source or
  execution mode would be expected to offer it. *Return `IReadOnlyList<T>` from a single terminal*
  — rejected: it hides the concrete array the reader already builds and gives callers nothing an
  array does not. *Keep the internal List bodies unreachable "in case"* — rejected: unreachable
  generated code compiles into every consumer assembly and is covered by no test.
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump.

### 2026-09-23 — `WithOptions`, `Where`, `Parallel`, `ToArrayAsync`, `AsAsyncEnumerable` and `Batches` move onto `{T}ParquetReader` (#478)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#478](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/478)
- **Change:** each composing member and terminal is now declared once, on `{T}ParquetReader`, and
  the composing members return `{T}ParquetReader`. Signatures are otherwise unchanged:
  `WithOptions(ParquetSerializerOptions options)`, `Where(Func<{T}RowGroupMetadata, bool>
  predicate)` (models with prunable columns), `Parallel()`, `ToArrayAsync(CancellationToken
  cancellationToken = default) -> Task<T[]>`, `AsAsyncEnumerable(CancellationToken
  cancellationToken = default) -> IAsyncEnumerable<T>` and `Batches(CancellationToken
  cancellationToken = default) -> IAsyncEnumerable<{T}ParquetExtensions.ColumnBatch>` (flat models).
- **Semantics chosen for the combinations the old types made unrepresentable** (the choice
  [47](../47-0.1-CONTRACT-AND-DESIGN-GOALS.md) §4.2 asks this entry to record). Rule: *the call that
  completes an unsupported combination throws `NotSupportedException`*; nothing is silently
  degraded. `Parallel()` throws on a stream source (message names `From(ReadOnlyMemory<byte>)`) and
  on a reader with a predicate; `Where()` throws on a parallel reader and on a reader that already
  has a predicate; `AsAsyncEnumerable()` and `Batches()` throw on a parallel reader; `Batches()`
  throws on a filtered reader. No filtered parallel path or filtered batch path exists internally,
  so none was wired. `WithOptions(null)` / `Where(null)` throw `ArgumentNullException` as before.
  One test per combination in `ReadBuilderTests`.
- **Rationale:** throwing from `Parallel()` rather than at the terminal (the docs/47 default for
  `From(Stream).Parallel()`) reports the mistake on the line that makes it, when the source is
  already known; only the terminal-specific cases wait for the terminal. A second `Where()` throws
  rather than AND-composing because composition would allocate a closure on a path that is
  otherwise allocation-free, and relaxing a throw later is additive while changing composition
  semantics is not.
- **Alternatives considered:** *Throw every invalid combination at the terminal* — rejected: the
  exception then surfaces far from the call that caused it, and a reader value could carry a state
  no terminal can run. *Degrade to sequential reads* — rejected by docs/47: it hides a policy the
  caller asked for. *Keep capability-specific public states* — rejected: it is the state-type
  cross-product this change removes.
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump.

### 2026-09-23 — `{T}ParquetReader` replaces `{T}ParquetStreamSource`, `{T}ParquetMemorySource`, `{T}ParquetFilteredSource` and `{T}ParquetParallelSource`; `From(Stream)` / `From(ReadOnlyMemory<byte>)` return it (#478)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#478](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/478) (0.1 contract
  tracker [#477](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/477))
- **Change:** the four #217 type-state structs are no longer emitted. One `public readonly struct
  {T}ParquetReader` holds the state privately — source (a `Stream` or a `ReadOnlyMemory<byte>`),
  options, predicate and a parallel flag — and `static {T}Parquet.From(Stream stream)` and
  `static {T}Parquet.From(ReadOnlyMemory<byte> parquetBytes)` both return it (return-type change on
  two lines per model). Catalogue effect per golden model, from `.api.shape.txt`: `OrderEvent` and
  `ScalarMetric` 65 → 48 members, 48 → 34 parameters; `ListOrder`, `NestedOrder`, `PocoOrder`
  35 → 19 members, 31 → 18 parameters; `SortedShipment` 58 → 41 members, 64 → 50 parameters.
  Legacy (`LegacyRecord` 6 / 14) unchanged — it has no builder (#246). The figures include the
  #479 `ToListAsync` removal.
- **Rationale:** the #217 builder removed the method-name cross-product and replaced it with a
  public state-type cross-product: four types, each with its own terminal set, whose names answer an
  implementation question ("which reader state is active?") rather than a user one (docs/47 §2). The
  reader keeps every internal read path (`ReadArrayCoreAsync`, `ReadEnumerableCoreAsync`,
  `ReadBatchesCoreAsync`, `ReadParallelArrayCoreAsync`) and dispatches to them; composing a read
  remains struct copies only (asserted allocation-free by `ComposingAReadAllocatesNothing`).
- **Alternatives considered:** *Keep the type-state structs* — rejected: every new axis multiplies
  public types again, and docs/47 names this as the multiplier left after #217. *A class-based
  reader* — rejected: composing would allocate. *Carry the source kind as an explicit enum field*
  — rejected as redundant: a non-null stream field already discriminates it, and a default-valued
  reader behaves as the default `MemorySource` did (an empty buffer).
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump.

### 2026-09-22 — `{T}RowGroupMetadata(int rowGroupIndex, long rowCount, bool hasStatistics, …column_N)` constructor made internal (#459)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#459](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/459) (0.1 contract
  tracker [#477](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/477))
- **Change:** the zone-map struct's constructor is emitted `internal`. The type and its read-only
  properties (`RowGroupIndex`, `RowCount`, `HasStatistics`, one `ParquetColumnStatistics<T>` per
  prunable column) stay public. 6 catalogue lines are **removed**, one per modern golden model
  (`ListOrder`, `NestedOrder`, `OrderEvent`, `PocoOrder`, `ScalarMetric`, `SortedShipment`);
  nothing is added.
- **Rationale:** the parameters were named after emitter slot indices (`column_0, column_2,
  column_3`), so the signature leaked the generator's internal column numbering and would change
  whenever a property was added or reordered. Consumers only ever *inspect* a metadata value inside
  a `.Where(...)` / `predicate:` lambda; the generated reader is the only constructor call site, and
  it lives in the same assembly.
- **Alternatives considered:** *Rename the parameters after the properties and keep the constructor
  public* — rejected: that still freezes a positional, per-model constructor into the 0.1 contract
  for a type nobody needs to build, and every added prunable column would still be a breaking change.
  *Make the whole struct internal* — rejected: it appears in the public `Func<{T}RowGroupMetadata,
  bool>` predicate parameters. *Keep it public for tests that fabricate metadata* — rejected: tests
  that need it compile the model into their own assembly, where `internal` is visible.
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump.

### 2026-09-22 — low-level row-group writers and columnar helpers made internal (#481)

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#481](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/481) (0.1 contract
  tracker [#477](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/477))
- **Change:** these emitted members are now `internal`, removing 16 catalogue lines:
  - `WriteParquetRowGroupAsync(this ParquetWriter writer, IReadOnlyCollection<T> chunk, CancellationToken)`
    — every modern model (6 lines).
  - `WriteParquetRowGroupAsync(this ParquetWriter writer, {T}ColumnarBatch batch, CancellationToken)`
    — flat models with a columnar batch (`OrderEvent`, `ScalarMetric`, `SortedShipment`; 3 lines).
  - `WriteParquetRowGroupColumnarAsync(this ParquetWriter writer, int rowCount, ReadOnlyMemory<…>…)`
    — same three models (3 lines).
  - `AsColumnarText(string?)` / `AsColumnarBinary(byte[]?)` — models with text / binary columns
    (`OrderEvent` both, `SortedShipment` text; 3 lines).
  - Classic emitter: `WriteRowGroupAsync(this ParquetWriter writer, IReadOnlyList<T> items, CancellationToken)`
    on `LegacyRecordParquetLegacyExtensions` (1 line). `BackendCompatibilityPolicyTests` and
    [14](../14-COMPATIBILITY-MATRIX.md) drop row-group write from the classic core surface.
- **Rationale:** the rule applied member by member was *does this describe user intent or an
  implementation strategy?* Each of these is the strategy the intent-level writers are built from:
  `items.WriteParquetAsync(stream, …)`, `asyncItems.WriteParquetAsync(stream, …)`,
  `items.WriteParquetBatchedAsync(stream, …)` and `{T}ColumnarBatch.WriteParquetAsync(stream, …)`
  all remain public and cover writing a collection, a stream of rows, and caller-owned column
  buffers. The positional `WriteParquetRowGroupColumnarAsync` in particular is defect 8 of
  [19](../19-PUBLIC-API-SURFACE.md): adding a property silently re-means every later argument.
  The `ParquetWriter`-taking overloads also leaked Parquet.Net's writer type into the 0.1 contract.
  Because generated code is emitted into the consumer's own assembly, `internal` keeps every one of
  these callable by the model's owning project — only downstream assemblies lose them.
- **Kept public, deliberately:** the Arrow bridge's
  `WriteParquetRowGroupAsync(ParquetWriter, RecordBatch, ParquetSerializerOptions?, CancellationToken)`
  (emitted only when Apache.Arrow is referenced, not in these baselines) — it is the *only* Arrow
  ingestion entry point, has no intent-level equivalent yet, and is documented in the README. It
  does not call any of the members made internal here: it builds a `{T}ColumnarBatch`, opens its
  own row group with `writer.CreateRowGroup()` and writes each column through
  `groupWriter.WriteAsync` / `WriteAllPartsAsync` (asserted by `ArrowConditionalEmissionTests`).
  `static readonly Schema` also stays public: the Arrow path needs it to create the writer.
  `{T}ColumnarBatch` fields stay public: they are how a caller builds the batch.
- **Alternatives considered:** *Keep the batch-taking `WriteParquetRowGroupAsync(ParquetWriter,
  {T}ColumnarBatch)` public for multi-row-group columnar writes* — rejected for 0.1: no caller in
  the repository or its docs relies on it, and an intent-level multi-batch writer (e.g. over
  `IAsyncEnumerable<{T}ColumnarBatch>`) can be added additively later without re-exposing a
  Parquet.Net type. *Mark the members `[EditorBrowsable(Never)]` instead* — rejected: that hides
  them from IntelliSense but still freezes them into the contract. *Grant `InternalsVisibleTo` to
  tests/benchmarks* — unnecessary: every test, benchmark and sample project runs the generator
  itself, so the models and their callers are always one assembly.
- **Note:** pre-1.0 break; `0.0.x` permits it without a major bump.

### 2026-09-22 — removed `ReadParquetAsync(Stream, ParquetSerializerOptions?, CancellationToken, Func<{T}RowGroupMetadata, bool>?)` and `ReadParquetAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(source).ToListAsync(ct)`, with `.WithOptions(options)` and `.Where(predicate)` as members. The builder's stream and memory terminals already delegated here; the bodies stay as the `internal` `ReadListCoreAsync`, outside the contract.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/48](../48-FLAT-READ-REMOVAL-480.md), which supersedes docs/41. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (docs/41) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — removed `ReadParquetArrayAsync(Stream, ParquetSerializerOptions?, CancellationToken, Func<{T}RowGroupMetadata, bool>?)` and `ReadParquetArrayAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(source).ToArrayAsync(ct)`. The bodies stay as the `internal` `ReadArrayCoreAsync`.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/48](../48-FLAT-READ-REMOVAL-480.md), which supersedes docs/41. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (docs/41) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — removed `ReadParquetStreamAsync(Stream, ParquetSerializerOptions?, CancellationToken, Func<{T}RowGroupMetadata, bool>?)` and `ReadParquetStreamAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken, Func<{T}RowGroupMetadata, bool>?)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(source).AsAsyncEnumerable(ct)`; the name also carried defect 1 of docs/19 ("Stream" denoting both source and shape). The bodies stay as the `internal` `ReadEnumerableCoreAsync`.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/48](../48-FLAT-READ-REMOVAL-480.md), which supersedes docs/41. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (docs/41) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — removed `ReadParquetBatchesAsync(Stream, ParquetSerializerOptions?, CancellationToken)` and `ReadParquetBatchesAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(source).Batches(ct)`, unchanged (batch read ownership is #369). Flat models only. The bodies stay as the `internal` `ReadBatchesCoreAsync`.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/48](../48-FLAT-READ-REMOVAL-480.md), which supersedes docs/41. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (docs/41) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — removed `ReadParquetParallelAsync(Stream, ParquetSerializerOptions?, CancellationToken)` and `ReadParquetParallelAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(bytes).Parallel().ToListAsync(ct)`. The memory body stays as the `internal` `ReadParallelListCoreAsync`. The `Stream` overload is deleted with its body: it read sequentially (defect 5 of docs/19), nothing delegated to it, and the builder already states the same fact by offering no `Parallel()` on the stream source.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/48](../48-FLAT-READ-REMOVAL-480.md), which supersedes docs/41. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (docs/41) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — removed `ReadParquetParallelArrayAsync(Stream, ParquetSerializerOptions?, CancellationToken)` and `ReadParquetParallelArrayAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(bytes).Parallel().ToArrayAsync(ct)`. The memory body stays as the `internal` `ReadParallelArrayCoreAsync`; the sequential `Stream` overload is deleted, as for `ReadParquetParallelAsync`.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/48](../48-FLAT-READ-REMOVAL-480.md), which supersedes docs/41. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (docs/41) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — `NullableColumnExtractor` and `VectorizedColumnTransforms` internalised (#461)

- **Surface:** package
- **Semver:** breaking-major
- **Issue:** [#461](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/461), part of
  [#477](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/477)
- **Change:** **removes** 24 lines from `src/Parquet.SourceGenerator.Attributes/PublicAPI.Unshipped.txt`
  — both type declarations, the 5 static members of `NullableColumnExtractor` and the 17 static
  members of `VectorizedColumnTransforms` (15 methods, 2 properties). Both types are now `internal`;
  the Attributes assembly grants `InternalsVisibleTo` to `Parquet.SourceGenerator.Tests` and
  `Parquet.SourceGenerator.Benchmarks`, which are their only callers.
- **Rationale:** neither type is called by generated code — no golden `.g.cs` and no emitter string
  names either — so they were spike helpers (#145, #210) riding in the shipped package with no
  consumer use case. Freezing them at `0.1.0` would have committed the package to a SIMD helper
  API nobody chose to publish. Never in a cut release's `PublicAPI.Shipped.txt`.
- **Alternatives considered:** deleting both types — rejected in #461 in favour of internalising,
  because their tests and the `NullBitmapExtractionBenchmark` probe keep measurement evidence that
  a later columnar write path may need. Keeping them public under an `Experimental` marker —
  rejected: an experimental public type is still a public type at the freeze.

### 2026-09-22 — generator assemblies carry no public API (#461)

- **Surface:** package
- **Semver:** internal
- **Issue:** [#461](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/461), part of
  [#477](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/477)
- **Change:** **removes** `src/Parquet.SourceGenerator/PublicAPI.Shipped.txt` (92 signatures) and
  `PublicAPI.Unshipped.txt` (79 signatures), 171 in all, together with that project's
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` reference. Every type in `Parquet.SourceGenerator`
  and `Parquet.SourceGenerator.Legacy` is now `internal`, including both `[Generator]` entry
  points. The binary-compat constructor overloads on `TargetClassModel` (3- and 5-argument) and
  `PropertyModel` (10- and 11-argument) are deleted; every call site already binds to the primary
  constructor through its optional parameters.
- **Rationale:** both packages set `IncludeBuildOutput=false` and ship the assembly only under
  `analyzers/dotnet/cs`, so no consumer can compile against it and none of those 171 signatures
  was reachable. Governing them as package API made every internal refactor of the parser and the
  models a ledger event, and the compat overloads existed only to satisfy that governance. Bucketed
  `internal` rather than `breaking-major` because no consumer can observe the change; the members'
  own `public` spellings inside now-`internal` types are not seams (docs/18 §What counts as a seam).
- **Alternatives considered:** keeping the `[Generator]` classes `public` — rejected: Roslyn
  instantiates generators by reflection and loads `internal` ones (verified by the sample and the
  package-consumption projects), so public-ness buys nothing. Keeping the `PublicAPI.*.txt` files
  and marking everything `internal` in them — rejected: there is nothing left for `RS0016` to guard.

### 2026-09-17 — dictionary and string payload safety limits (#307)

- **Surface:** unshipped
- **Semver:** additive-minor
- **Issue:** [#307](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/307)
- **Rationale:** Adds `MaxDictionaryEntries` and `MaxStringLengthBytes` to bound hostile dictionary
  pages and oversized string payloads before they can exhaust consumer memory.

### 2026-09-17 — generator feature-level configuration (#290)

- **Surface:** unshipped
- **Semver:** additive-minor
- **Issue:** [#290](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/290)
- **Rationale:** Adds named feature-level configuration and an assembly-level fallback so consumers
  can pin generated dialect behavior without changing the existing default.

### 2026-09-17 — analyzer-config generator configuration seam (#224)

- **Surface:** seam
- **Semver:** internal
- **Issue:** [#224](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/224)
- **Rationale:** Threads the value-equatable MSBuild configuration through both incremental
  generators into their emitters without exposing the configuration type to package consumers.

### 2026-09-16 — `ParquetSerializerOptions` page decompression limits (#315)

- **Surface:** unshipped
- **Semver:** additive-minor
- **Issue:** [#315](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/315)
- **Rationale:** Adds `MaxDecompressedPageSize` (default 67,108,864 bytes / 64 MiB) and
  `MaxDecompressionExpansionRatio` (default 500) to bound hostile compressed-page allocations and
  decompression amplification before Parquet.Net reads page payloads.
- **Alternatives considered:** Passing limits through `ParquetOptions` was rejected because
  Parquet.Net 6.1.0 exposes no decompression-limit hook and the legacy API has the same gap.
  Checking whole-column metadata was rejected because it would reject valid multi-page columns;
  the generated reader instead guards each page header at the upstream reader's seek boundary.

### 2026-09-14 — `ParquetSerializerOptions` defensive bounds and DoS mitigation limits (#287)

- **Surface:** unshipped
- **Semver:** additive-minor
- **Issue:** [#287](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/287)
- **Rationale:** Adds `MaxAllocationValues`, `MaxRowGroupCount`, and `MaxNestingDepth`
  configuration properties (get/set) to `ParquetSerializerOptions` to establish configurable
  defensive resource limits against untrusted and malformed Parquet inputs, protecting consumers
  from memory exhaustion (allocation bombs), unbounded row-group amplification, and recursive
  schema recursion bombs.


### 2026-09-12 — `SortedShipmentParquetExtensions` catalogued golden model (#264)

- **Surface:** emitted
- **Semver:** generated-shape
- **Issue:** [#264](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/264)
- **Rationale:** Adds **75** emitted members by cataloguing one new combined driver model
  (`SortedShipment`, a prunable and sorted-key model) alongside the existing golden models.
  Demonstrates that models carrying both `[ParquetSortKey]` and prunable columns receive both
  the predicate pushdown zone-map struct and the sorted lookup overloads over the shared statistics hook.


### 2026-09-11 — `PocoOrderParquetExtensions` catalogued golden model (#176 M3b-2)

- **Surface:** emitted
- **Semver:** generated-shape
- **Issue:** [#176](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/176)
- **Rationale:** Adds **47** emitted members by cataloguing one new driver model
  (`PocoOrder`, a `List<POCO>`/`POCO[]` shape) alongside the existing five — the same member
  families every catalogued model already carries (entry point, builders, flat reads, writer
  trio). No signature *shape* is new: the catalogue exists so drift in emitted surface is
  diffable, and this model's drift is exactly the stack that introduced it.

### 2026-09-10 — Read entry point and builder (#217)

- **Surface:** emitted
- **Semver:** generated-shape
- **Issue:** [#217](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/217),
  [#216](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/216)
- **Rationale:** Adds **104** emitted members across the five catalogued models: a `{T}Parquet`
  entry point and four builder structs per model. Reads previously encoded source, shape and
  execution into method names — twelve members for a four-axis grid, heading for forty-five once
  #146, #148 and #178 land. Each axis becomes a member instead, so a new axis adds members linearly
  rather than multiplying names. The full argument is in
  [docs/19](../19-PUBLIC-API-SURFACE.md) decision D2.

  Purely additive: no existing member changes or is removed. The flat `Read*` methods remain and are
  what the builders delegate to through the `0.1.0` compatibility window, per docs/19 decision D3.
  Remove them only after the later removal gate in [document 41](../41-FLAT-READ-FREEZE-SCOPE-262.md).

  The structs are type-state rather than one builder validating at runtime, so the grid's four empty
  cells are absent members rather than members that throw: no `Parallel()` on a stream source, no
  `Where()`/`Parallel()` on each other's results, no streaming or batch shape after `Parallel()`.
  `Parallel()` deliberately takes no degree argument — `MaxDegreeOfParallelism` is an option after
  #239, and an argument here would give the knob two homes again. #241 decides where it settles.

### 2026-09-10 — `ReadParquetParallelAsync(...)` / `ReadParquetParallelArrayAsync(...)`: `maxDegreeOfParallelism` parameter removed

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#218](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/218)
- **Change:** the `int maxDegreeOfParallelism = -1` parameter is **removed** from four signatures
  per model — `ReadParquetParallelAsync(Stream, …)`,
  `ReadParquetParallelAsync(ReadOnlyMemory<byte>, …)`,
  `ReadParquetParallelArrayAsync(Stream, …)` and
  `ReadParquetParallelArrayAsync(ReadOnlyMemory<byte>, …)`. 16 catalogue lines change across the
  four modern golden models; no line is added or deleted, because the members still exist with one
  fewer parameter. `ParquetSerializerOptions.MaxDegreeOfParallelism` is unchanged and is now the
  only home for the setting.
- **Rationale:** the option had two homes and the precedence between them was invisible from the
  signature. It was `mdop > 0 ? mdop : (options.MaxDegreeOfParallelism > 0 ? … : ProcessorCount)`,
  so the argument won when positive and was **silently discarded** when zero or negative — a caller
  passing `0` got neither an error nor their value. On the `Stream` overloads the parameter was
  inert entirely: that reader is sequential by construction. No caller loses expressiveness; the
  options property says everything the parameter said.
- **Alternatives considered:** *Keep the parameter and document the precedence in XML docs* —
  rejected: a documented precedence rule is only as good as the reader, and the package is `0.0.x`
  where the duplicate can simply be deleted. *Remove the options property instead and keep the
  parameter* — rejected: `ParquetSerializerOptions` is the surface every other setting already uses
  and the one an application can configure once and pass everywhere, and the parameter cannot be
  reached from `ReadParquetAsync` at all. *Keep it on the `ReadOnlyMemory<byte>` overloads only,
  where it does something* — rejected: two overloads of one method taking different parameter lists
  for the same concept is the discoverability defect #216 catalogues, not a fix for it.
- **Note:** pre-1.0 break. `0.0.x` permits it without a major bump; the bucket records that the
  call was made deliberately. The rule it applies is
  [19 - Public API Surface](../19-PUBLIC-API-SURFACE.md).

### 2026-09-10 — `WriteParquetBatchedAsync(...)` / `WriteParquetAsync(IAsyncEnumerable<T>, ...)`: `rowGroupSize` parameter removed

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#218](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/218)
- **Change:** the `int? rowGroupSize = null` parameter is **removed** from
  `WriteParquetBatchedAsync(this IEnumerable<T>, Stream, …)` and from
  `WriteParquetAsync(this IAsyncEnumerable<T>, Stream, …)`. 9 catalogue lines change — two per
  modern golden model plus `WriteParquetBatchedAsync` on the classic emitter's
  `LegacyRecordParquetLegacyExtensions`; again no line is added or deleted.
  `ParquetSerializerOptions.RowGroupSize` is unchanged and is now the only home for the setting.
- **Rationale:** same defect as the entry above. Resolution was `rowGroupSize ?? options.RowGroupSize`,
  so the parameter won whenever supplied — the opposite of the caller's likely reading of
  `WriteParquetBatchedAsync(stream, 1_000, myConfiguredOptions)`, where the explicitly configured
  options object looks like the more considered instruction. The `ArgumentOutOfRangeException` for
  a non-positive size now always names `options`, since that is the only source it can come from.
- **Alternatives considered:** *Keep the parameter on `WriteParquetBatchedAsync` only, since row
  group size is arguably that method's subject rather than its configuration* — rejected: the same
  argument applies to the `IAsyncEnumerable` overload, which would leave the pair inconsistent, and
  the parameter sat behind `stream` and in front of `options`, so it was already passed by name at
  every call site in this repository. `new ParquetSerializerOptions { RowGroupSize = 10_000 }` is
  the same shape of expression as `rowGroupSize: 10_000`. *Deprecate with `[Obsolete]` for one
  release* — rejected: the members are emitted into the consumer's own compilation, so an
  `[Obsolete]` overload is generated code the consumer cannot suppress per-call-site cleanly, and
  `0.0.x` has no deprecation window to honour.
- **Note:** pre-1.0 break, as above.

#### Ledger-format note, recorded rather than fudged

The contract's phrasing — "one entry per added or changed signature" — and
`scripts/CheckApiLedger.cs`'s counting of *added* catalogue lines both assume additions. This
change removes a parameter from 25 signatures across five models, which is **one decision**, not
25. Written literally it would be 25 near-identical entries whose repetition would bury the two
decisions actually taken. Two entries are written instead, one per option removed, each naming the
affected signatures and the line count. Note also that a pure removal adds no catalogue line, so
`CheckApiLedger.cs` would have demanded nothing at all had these signatures not also changed — the
CI half of the contract is blind to removals by construction, and only
`GoldenCodeGenRegressionTests` catches them. Both points are raised on
[#218](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/218) for
[#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230) to settle.

### 2026-09-10 — `0.0.x inherited surface`

- **Surface:** emitted, seam
- **Semver:** generated-shape
- **Issue:** [#215](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/215),
  [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230)
- **Rationale:** Retrospective seeding. Covers all **159** emitted public members catalogued across
  the five golden models by #215 — `OrderEventParquetExtensions` (55),
  `ScalarMetricParquetExtensions` (53), `NestedOrderParquetExtensions` (22),
  `ListOrderParquetExtensions` (22), `LegacyRecordParquetLegacyExtensions` (7) — and the **3**
  internal seams seeded into `src/api/seams.txt`:
  `CodeEmitter.EmitResolveFieldLine`, `CodeEmitter.EmitReadWithNullBypass` and
  `CodeEmitter.GetBranchlessNonNullExpression`. Writing 162 retroactive rationales would have
  produced 162 fabrications; recording one honest statement that the surface grew by accretion is
  worth more than 162 invented ones.
- **Alternatives considered:** One entry per existing member — rejected: the rationales would have
  been reconstructed after the fact and would read as approval that never happened, which is
  exactly the failure this ledger exists to prevent. Backdating the contract to the commits that
  introduced each member — rejected: the reviews those members actually received did not ask the
  questions this contract asks, so claiming they did would be false.
- **Note:** Pre-1.0 stance. `0.0.x` permits breaking changes without a major bump, so the bucket on
  an entry does not gate the release — it records that the call was made and by whom. At `0.1.0`
  (#230) this file becomes the freeze record.
