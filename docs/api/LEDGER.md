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
  what the builders delegate to for one release, per docs/19 decision D3, which is when this
  addition is repaid — the twelve flat read members are removed at the `0.1.0` freeze.

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
