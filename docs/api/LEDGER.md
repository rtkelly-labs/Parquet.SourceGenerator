# API Change Ledger

Every line added to a governed catalogue — an emitted-API `*.api.txt`, `src/api/seams.txt`, or a
`PublicAPI.Unshipped.txt` — needs an entry here classifying its semver impact. Newest first.

The rule, the three surfaces and the author process are in
[API Change Contract](../quality/api-change-contract.md). The buckets are `additive-minor`,
`breaking-major`, `internal` and `generated-shape`.

> **Everything dated before 2026-09-10 was catalogued retrospectively.** The single
> `0.0.x inherited surface` entry below covers the whole surface that existed when this contract
> was introduced. Those members were **not** reviewed under this contract, no per-member rationale
> was written for them, and none should be read as approved. That is the point of seeding it this
> way: the freeze diff for [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230)
> is then exactly the set of entries dated on or after 2026-09-10, which *were* reviewed.

<!-- Add new entries directly below this line, newest first. -->

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
    [Compatibility Matrix](../reference/compatibility.md) drop row-group write from the classic core surface.
- **Rationale:** the rule applied member by member was *does this describe user intent or an
  implementation strategy?* Each of these is the strategy the intent-level writers are built from:
  `items.WriteParquetAsync(stream, …)`, `asyncItems.WriteParquetAsync(stream, …)`,
  `items.WriteParquetBatchedAsync(stream, …)` and `{T}ColumnarBatch.WriteParquetAsync(stream, …)`
  all remain public and cover writing a collection, a stream of rows, and caller-owned column
  buffers. The positional `WriteParquetRowGroupColumnarAsync` in particular is defect 8 of
  [Public API Surface](../reference/api-surface.md): adding a property silently re-means every later argument.
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
  [docs/design/flat-read-removal.md](../design/flat-read-removal.md), which supersedes the #262 freeze. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (#262) — rejected by the #477
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
  [docs/design/flat-read-removal.md](../design/flat-read-removal.md), which supersedes the #262 freeze. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (#262) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — removed `ReadParquetStreamAsync(Stream, ParquetSerializerOptions?, CancellationToken, Func<{T}RowGroupMetadata, bool>?)` and `ReadParquetStreamAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken, Func<{T}RowGroupMetadata, bool>?)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(source).AsAsyncEnumerable(ct)`; the name also carried defect 1 of docs/reference/api-surface.md ("Stream" denoting both source and shape). The bodies stay as the `internal` `ReadEnumerableCoreAsync`.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/design/flat-read-removal.md](../design/flat-read-removal.md), which supersedes the #262 freeze. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (#262) — rejected by the #477
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
  [docs/design/flat-read-removal.md](../design/flat-read-removal.md), which supersedes the #262 freeze. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (#262) — rejected by the #477
  contract-narrowing direction; ship one `[Obsolete]` release first — rejected, `0.0.x` has no
  published consumers to warn; keep the members under the same names as `internal` — rejected, a
  consumer's own assembly could keep calling them and the removal would not be visible to it.

### 2026-09-22 — removed `ReadParquetParallelAsync(Stream, ParquetSerializerOptions?, CancellationToken)` and `ReadParquetParallelAsync(ReadOnlyMemory<byte>, ParquetSerializerOptions?, CancellationToken)`

- **Surface:** emitted
- **Semver:** breaking-major
- **Issue:** [#480](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/480)
- **Rationale:** Removed from every modern model; the builder already expresses this cell as
  `<Model>Parquet.From(bytes).Parallel().ToListAsync(ct)`. The memory body stays as the `internal` `ReadParallelListCoreAsync`. The `Stream` overload is deleted with its body: it read sequentially (defect 5 of docs/reference/api-surface.md), nothing delegated to it, and the builder already states the same fact by offering no `Parallel()` on the stream source.
  Keeping both surfaces into `0.1.0` would make the first stable contract the widest one; see
  [docs/design/flat-read-removal.md](../design/flat-read-removal.md), which supersedes the #262 freeze. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (#262) — rejected by the #477
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
  [docs/design/flat-read-removal.md](../design/flat-read-removal.md), which supersedes the #262 freeze. Measured across the six modern
  golden models, #480 removes 66 members and 222 parameter slots in total. The legacy emitter keeps
  its flat reads (#246 declared subset).
- **Alternatives considered:** keep as forwarders through `0.1.0` (#262) — rejected by the #477
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
  own `public` spellings inside now-`internal` types are not seams (docs/quality/api-change-contract.md §What counts as a seam).
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
  [docs/reference/api-surface.md](../reference/api-surface.md) decision D2.

  Purely additive: no existing member changes or is removed. The flat `Read*` methods remain and are
  what the builders delegate to through the `0.1.0` compatibility window, per docs/reference/api-surface.md decision D3.
  Remove them only after the later removal gate in [#262](../design/decisions.md#262--flat-read-freeze-superseded) (since superseded by #480).

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
  [Public API Surface](../reference/api-surface.md).

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
