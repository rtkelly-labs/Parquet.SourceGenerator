# Changelog

All notable changes to **Parquet.SourceGenerator** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

Changes since `0.0.4`; this section becomes the next release entry when one is cut.

### Added
- **Compile-time type adapters** (`ParquetTypeAdapterAttribute`, `ParquetAdapterAttribute`,
  `docs/44-TYPE-ADAPTERS.md`). A domain type the generator has no mapping for is serialized
  through a static `ToStorage` / `FromStorage` pair to a surrogate it already maps — a scalar, or a
  struct/class planned as a Parquet group without `[ParquetSerializable]`. Adapters are registered
  explicitly (`[assembly: ParquetTypeAdapter(typeof(...))]` on the project or on an adapter
  package) or chosen per member (`[ParquetAdapter(typeof(...))]`, the only way to override a
  built-in mapping), resolved at compile time with deterministic precedence, and reached from
  generated code through an internal storage property on the partial type — two direct static
  calls, no reflection, no runtime registry, Native AOT verified. Nullability stays with the
  generator. New diagnostics: **PARQ016** (invalid adapter), **PARQ017** (ambiguous defaults),
  **PARQ018** (unrepresentable surrogate), **PARQ019** (built-in override ignored, warning).
  Scalar surrogates work on the legacy 4.x/5.x backend too; group surrogates need Parquet.Net 6.
- **`Parquet.SourceGenerator.NodaTime`** — a new, independently published package
  (`docs/45-NODATIME.md`). Referencing it makes `Instant`, `Duration`, `LocalDate`, `LocalTime`,
  `LocalDateTime`, `Offset`, `OffsetDateTime`, `OffsetDate`, `OffsetTime`, `ZonedDateTime`,
  `Interval`, `DateInterval`, `YearMonth`, `AnnualDate` and `Period` serializable with lossless
  defaults: nanosecond precision over the full NodaTime range, calendar and offset identity,
  unnormalized periods, and TZDB-validated zones that fail loudly on read if the installed zone
  rules disagree with the offset observed at write time. Explicit interop adapters map to native
  `DateTime` / `DateOnly` / `TimeOnly` / Unix-nanosecond columns and throw rather than truncate.
  Supports NodaTime 3.2.0 and later; tested against 3.3.4.
- **Code metrics baselines and a complexity ratchet** (`metrics/*.metrics.txt`,
  `docs/21-CODE-METRICS.md`). Roslyn's maintainability index, cyclomatic complexity, class
  coupling, inheritance depth and line counts are now recorded per namespace, type and member for
  the hand-written code under `src/`, checked in as a deterministic ordinal text artifact, and
  gated in CI on **drift** rather than on absolute values — the `*.api.txt` pattern applied to
  quality. `CA1502` / `CA1505` / `CA1506` are enabled for `src/` with thresholds pinned in
  `CodeMetricsConfig.txt`. Refresh with `UPDATE_GOLDEN_FILES=true dotnet run scripts/CodeMetrics.cs`
  or `/update-golden`. The recorded evidence: `CodeEmitter` is 2,536 source lines at cyclomatic
  complexity 144, and `TargetParser.CollectMembers` carries a cyclomatic complexity of **105** in
  one 463-line method — fifteen times the recommended maximum, and the worst maintainability index
  in the repository at 14.
- **Generated-code metrics** (`test/Parquet.SourceGenerator.Tests/GoldenFiles/*.metrics.txt`,
  `docs/22-GENERATED-CODE-METRICS.md`). The same computation turned on the code the generator
  *emits*: one baseline beside every `*.api.txt`, covering both emitters, refreshed by the same
  `UPDATE_GOLDEN_FILES=true` / `/update-golden` command as the golden files themselves, and gated
  on drift. Each carries a size-per-capability ratio — emitted executable lines per emitted public
  member — so growth in emitted volume can be argued against growth in emitted API. The
  Maintainability Index is **reported but not gated** for emitted code: it correlates with emitted
  method length at r = −0.95 and has already bottomed out at 0 on the worst method, so it detects
  nothing that `SLOC` does not. Measured: flat models cost ~10 executable lines per emitted member,
  row-level lists 33, and `ListOrderParquetExtensions.WriteParquetRowGroupAsync` is a single
  1,148-line method at cyclomatic complexity **97** — within 8% of the worst hand-written method in
  the repository. Compiling each golden file in order to measure it also found that the emitted
  writer does not compile for a nullable value-type compound member (#255).
- **Duplication measurement with a drift gate** (`metrics/duplication.txt`,
  `docs/23-DUPLICATION.md`, layer 3 of #251). Token-level clones across the hand-written sources —
  identifier-blind, literal-sensitive, 16-token windows extended along diagonal alignments, spans
  of 40+ tokens reported per method pair — checked in with the `*.api.txt` grammar and gated on
  drift in CI. Emitted code is excluded by design (repetition in generated output is the design,
  not a defect). The detector was calibrated against the repo's demonstrated failure before
  adoption: it names the historical `ResolveSchemaField` copies and all three read paths that
  broke on #196. Current state, reported as the number the refactor case rests on: **93 clusters,
  12,034 duplicated tokens**, worst pair `EmitReadArrayAsync` ↔ `EmitReadAsync` at 180 tokens.
- **Metrics oracle — `Metrics.exe` cross-checks the bespoke computation**
  (`.github/workflows/metrics-oracle.yml`, `scripts/MetricsOracleCompare.cs`,
  `docs/24-METRICS-ORACLE.md`, #254). A nightly `windows-latest` job runs Microsoft's own
  (Windows-only) metrics tool over both generator projects and compares it, type by type,
  against the layer-1 baselines: MI within ±2, CC/CL/SLOC exact. Assembly totals are
  reported but not gated — enumeration scope differs between the tools, and a tolerance
  there would paper over real drift. Any disagreement fails the job and opens or updates a
  `metrics-oracle` issue, because a red schedule gets muted and an issue gets acted on.
  The check `CodeMetrics.cs` could never run on itself.
- **Deterministic call-graph artifact with cycle, fan-out and layering gates**
  (`scripts/CallGraph.cs`, `graph/*.callgraph.txt`, `graph/callgraph.allowlist.txt`,
  `docs/callgraph.md`, `docs/callgraph-generated.md`, `docs/25-CALL-GRAPH.md`, #252).
  This repo's defects have been graph defects — #252 makes the graph an artifact: static
  edges per method as an ordinal baseline gated on drift, no multi-node cycle without a
  catalogued reason (today's entire cycle inventory is the #176 compound parser and the
  definition-ladder emitter — tree recursion, `SELF`/`SCC` lines with the argument written
  down), a fan-out ratchet (28 today), and a layering rule that would have caught the
  `ResolveSchemaField` divergence as it happened: components must not call back into the
  emitter hub. Measured shape: 211 nodes / 356 edges / depth 6. The honesty clause is part
  of the artifact: delegates and virtual dispatch are invisible to it, and the unresolved
  call-site count rides in the baseline header.
- **Mutation testing for the behavioural suite** (`stryker-config.json`,
  `.github/workflows/mutation.yml`, `scripts/MutationSummary.cs`,
  `docs/26-MUTATION-TESTING.md`, layer 4 of #251). Stryker.NET runs nightly and answers the
  question coverage cannot: would a test notice if a line were *wrong*? The defining decision
  is what it leaves out — the golden-file, API-baseline, metrics-baseline and IL-shape suites
  are excluded, because they assert the *text/shape* of emitted output and would "kill" every
  mutation trivially, reporting a flattering ~100% that proves nothing about semantic coverage.
  Round-trip, property-based (#198), diagnostic and compile-check tests stay in. It reports
  through a single PR refreshed nightly (the #189 lesson: a red schedule gets muted, a
  reviewable PR gets acted on), and the score is recomputed with an explicit denominator.
  **No threshold is set** — the first night establishes the baseline; a floor picked before the
  number is known is vacuous or permanently red.

### Changed
- **`CHANGELOG.md` is now the release authority (#248).** `scripts/ParseChangelog.cs` validates the
  changelog structure in CI and, in `--release` mode, is the only source of the release version and
  notes. `release.yml` lost its `version` input: a `prepare` job reads the first cut
  `## [x.y.z] - YYYY-MM-DD` section, refuses an already-taken `v<version>` tag, and hands the
  version to a `build` job (full verification battery) and a `publish` job (NuGet push plus a
  GitHub release whose body is the changelog section). A version that is not described in the
  changelog cannot be published.
- **`TargetParser.CollectMembers` decomposed (#263).** The generator's front door — every
  `[ParquetSerializable]` type in every consumer's compilation passes through it — measured
  cyclomatic complexity **105** across 463 lines with 11 parameters. It is now a `MemberScope` /
  `MemberSink` pipeline of small methods, one per attribute family, rule, and model shape:
  `CollectMember` itself is CC 15 and the largest fragment 21, `GetTargetModelCore` (CC 38, the
  other grandfathered method) is 16, and no method in the repository exceeds the pre-existing
  worst of 25. Both `[SuppressMessage]` grandfather clauses are deleted, the class-coupling
  method gate has eight points of headroom where it had zero, and the maintainability floor moved
  14 → 27. Behaviour-preserving by construction: every golden file and emitted-API baseline is
  byte-identical.

### Fixed
- **Coexistence of the two row-group pruning mechanisms is now pinned by a behavioural
  test** (`PredicatePushdownAndSortedLookupCoexistOnOneModel`, completes #264's coverage).
  `SortedEvent` carries both the predicate zone-map path and three sort-key binary-search
  paths; the test asserts full-scan, pushdown, and lookup all agree on one file, and that
  co-location does not disable either mechanism. The #281 merge landed the combined golden
  and the convergence-constraint comments but not this test — the correctness floor the
  actual footer-read convergence will need.

---

## [0.0.4] - 2026-09-11

This release introduces the generated read builder (`{T}Parquet.From(...)`) and keeps every
existing flat read method working as a forwarder. Both surfaces ship together while the compatibility
window remains open. `docs/19-PUBLIC-API-SURFACE.md` decision D3 retains the flat methods through
the `0.1.0` window; document 41 records the later removal gate. Callers can adopt the builder now;
the flat methods are not yet marked
`[Obsolete]` because the two surfaces are still being validated against each other.

### Added
- **Generated read builder (`{T}Parquet.From(...)`)**: reads are now expressed as a chain rather
  than a cross-product of method names — `PersonParquet.From(stream).ToListAsync(ct)`,
  `PersonParquet.From(bytes).Parallel().ToArrayAsync(ct)`,
  `PersonParquet.From(bytes).Where(m => m.Id.Min >= 100).ToListAsync(ct)`. The builder is
  **type-state**: a combination that cannot work is not a member you can call. `Parallel()` is
  absent on a `Stream` source, because a single `ParquetReader` seeks within its stream and
  concurrent row-group reads corrupt each other; `Where()` and `Parallel()` are mutually absent
  until a parallel reader accepts a predicate. `Where` also closes a gap in the flat methods,
  where pushdown existed on the `Stream` overloads but not the buffer ones. Rationale and the
  full axis grid are in `docs/19-PUBLIC-API-SURFACE.md` (decision D2).
- **Row-group predicate pushdown from footer statistics**: a predicate over a generated per-column
  `[Min, Max]` metadata view skips row groups that cannot contain a match, so they are never
  decompressed. This is the mechanism the builder's `Where()` exposes.
- **Struct-of-arrays (SoA) columnar batch reading**: a caller that wants columns rather than
  objects can read straight into contiguous per-column buffers and skip materialising a row type
  at all.
- **API change contract (`PARQAPI001` / `PARQAPI002`)**: three API surfaces are now governed, and
  nothing enters one without a catalogue line *and* a `docs/api/LEDGER.md` entry recording its
  semver bucket. The emitted consumer API is gated at **build** time against the `*.api.txt`
  baselines added by #215 (`PARQAPI001`); internal seams — members widened past `private` for
  cross-component reuse — are catalogued in the new `src/api/seams.txt` and gated by `PARQAPI002`;
  the shipped package API keeps its existing `RS0016` gate unchanged. Body, performance and comment
  changes alter the golden `.g.cs` but not the `.api.txt`, and do not trip anything. A
  `**Unapproved-by-design:**` ledger entry suppresses the build error for spikes and is rejected by
  CI on `main`. Both gates are analyzers in `tools/Parquet.SourceGenerator.ApiGates`, are never
  packed, and short-circuit unless handed their catalogue as an `AdditionalFile`, so they cannot
  run in a consumer's compilation. See `docs/18-API-CHANGE-CONTRACT.md`.
- **Direct columnar handoff (write)**: flat `[ParquetSerializable]` models now also emit a
  `{Type}ColumnarBatch` struct plus `WriteParquetRowGroupAsync(batch)`,
  `WriteParquetRowGroupColumnarAsync(rowCount, ...)` and a stream-level `batch.WriteParquetAsync`.
  A caller whose data is already in contiguous column buffers skips the row-to-column transpose and
  its `ArrayPool` rentals entirely — buffers reach Parquet.Net verbatim. Nullable value columns take
  packed values plus explicit definition levels. Measured at ~7% of end-to-end write time on a
  16-column schema, identical in Workstation and Server GC, with allocation unchanged; see
  `docs/12-BUFFER-REUSE-AND-EXTRACTION-STRATEGIES.md` §6. Models with struct, list or map members
  are unaffected and keep the row-oriented API only.
- **Apache Arrow `RecordBatch` ingestion (experimental, #177)**: when — and only when — the consumer
  compilation references Apache.Arrow, the generator emits an extra `{Namespace}.{Type}.Arrow.g.cs`
  per Arrow-representable `[ParquetSerializable]` type, adding
  `WriteParquetRowGroupAsync(ParquetWriter, RecordBatch, ParquetSerializerOptions?, CancellationToken)`
  to the same partial class. Neither shipped package takes an Apache.Arrow dependency; the gate is a
  single `bool` off `CompilationProvider`, so toggling the reference re-runs only the Arrow-gated
  output. Columns are matched by name and validated strictly (every offending field reported at
  once); fixed-width Arrow buffers are handed to the writer with no copy and no `ArrayPool` rental;
  nullable columns derive definition levels from the Arrow validity bitmap and produce byte-identical
  files to the POCO write path. Supported Apache.Arrow floor: `23.0.0`.
- **Sorted row-group pruning (experiment, issue #151)**: mark a column `[ParquetSortKey]` and the
  generator emits `ReadParquetBy<Column>Async` (point lookup) and `ReadParquet<Column>RangeAsync`
  (inclusive slice) for it. Both certify the column as sorted from the footer `[Min, Max]`
  statistics and binary search that metadata, so only the row groups that can contain the key are
  decompressed; overlapping or missing statistics fall back to a full scan and the answer is
  identical either way. Pass a `ParquetPruneStatistics` to see how many row groups were skipped.
  The marker is opt-in: a model with no `[ParquetSortKey]` emits exactly what it did before, with
  none of the lookup API. Marking a member the rules cannot support — nullable, `string`, a
  compound member, or a type with no Parquet statistics order — is reported as **PARQ014** with the
  reason rather than silently emitting nothing.
  Note the cost shape: certifying sortedness reads every row group's statistics, so the metadata
  phase is O(N) in row groups. Only decompression is logarithmic — that is where the speedup
  comes from, and it is why the win grows with row-group payload size rather than with row-group
  count alone.
- **Span-keyed string deduplication** (`DeduplicateStrings = true`): string columns are read
  through Parquet.Net's raw `ReadOnlyMemory<char>` surface and interned against a pooled
  open-addressed table keyed on `ReadOnlySpan<char>`, so a repeated value costs no `string`
  allocation at all. Hash matches are always confirmed with a full ordinal comparison. On the
  Adult Census dataset (32,561 rows, 9 categorical columns) this cuts managed read allocation
  from 20.38 MB to 8.75 MB. The `ReadOnlySpan<byte>` variant the design originally called for is
  not reachable — Parquet.Net 6.1.0 exposes no UTF-8 byte surface for string columns; see
  `UPSTREAM_DEPENDENCY_LIMITATIONS.md`.
- **Compound models (nested POCOs, lists and maps)**: a recursive model and parser replace the
  flat-only target model, and the v6 backend emits struct schemas with definition-ladder shredding
  and matching reconstruction on read, plus row-level lists and arrays of primitives. Two new
  diagnostics guard the shapes that cannot work: **PARQ012** for a compound member that closes a
  type cycle, and **PARQ013** for nesting deeper than the emitter will expand.
- **Format-level dictionary encoding and column encoding hints**, exposed so a model can ask for
  the Parquet encoding a column should use.
- **Conformance and interoperability suites**: generated and fixture files are validated against
  pinned Apache tooling, with bidirectional PyArrow and DuckDB tests, a producer/consumer version
  and schema-evolution matrix, a semantic compatibility oracle, and property-based
  supported-schema and corrupted-file coverage. The envelope these test is written down in the
  compatibility docs.

### Changed
- **Breaking (pre-1.0), #218 — every configuration option now has a single home.** The duplicated
  positional parameters are removed from the generated API; `ParquetSerializerOptions` is the only
  place either setting lives:
  - `maxDegreeOfParallelism` is gone from `ReadParquetParallelAsync` and
    `ReadParquetParallelArrayAsync` (both the `Stream` and the `ReadOnlyMemory<byte>` overload).
    Set `ParquetSerializerOptions.MaxDegreeOfParallelism` instead.
  - `rowGroupSize` is gone from `WriteParquetBatchedAsync` and from the `IAsyncEnumerable<T>`
    overload of `WriteParquetAsync`, on both the modern and the classic emitter. Set
    `ParquetSerializerOptions.RowGroupSize` instead.

  Migration is mechanical: `WriteParquetBatchedAsync(stream, rowGroupSize: 10_000)` becomes
  `WriteParquetBatchedAsync(stream, new ParquetSerializerOptions { RowGroupSize = 10_000 })`. Both
  parameters sat behind another optional parameter and so were already passed by name, and both
  previously took precedence over the options property — silently, since the signature said nothing
  about it. One behaviour change beyond the removal: a non-positive `maxDegreeOfParallelism` used to
  be discarded without error, so a caller who passed `0` got the options value or
  `Environment.ProcessorCount`; there is now no argument to discard. The rule this applies is
  recorded in `docs/19-PUBLIC-API-SURFACE.md`. `0.0.x` permits the break; the decision is recorded
  as `breaking-major` in `docs/api/LEDGER.md`.
- **Formatting tooling consolidated on CSharpier.** `dotnet format whitespace` is removed from CI:
  its Roslyn formatter disagrees with CSharpier on layout (case-body and pattern-arm indentation),
  and it policed nothing beyond `.cs` files anyway. `.editorconfig` now carries CSharpier's
  configuration (`max_line_length`, its non-configurable behaviors) and disables `IDE0055`, so no
  Roslyn-based formatter — build, IDE or CLI — competes with CSharpier over C# layout. `.gitattributes`
  pins `eol=lf` on checkout, replacing the whitespace formatter's line-ending role for non-C# files.
- Generated public API baselines are emitted as signature-only `*.api.txt` files beside the golden
  `.g.cs` files, so an API change is reviewable separately from a body change.

### Performance
- All-null nullable chunks are detected and bypassed instead of being decoded value by value.
- Write-side extraction loops drop their bounds checks via `MemoryMarshal` and `Unsafe.Add`.
- Null bitmap construction for nullable column extraction is branchless.

### Fixed
- The benchmark headline table is regenerated through a pull request rather than pushed directly
  to protected `main`, and is written without a UTF-8 BOM.
- CI builds pull requests that are not based on `main`.

### Known gaps
- Nested collections, `DateTimeOffset` and positional records are unsupported. They are now
  rejected at compile time (`PARQ006`/`PARQ008`) rather than failing at runtime.
- `ReadParquetParallelAsync(Stream)` reads row groups sequentially — a single `ParquetReader`
  seeks within its stream, so concurrent row-group reads would corrupt each other. It silently
  ignores `ParquetSerializerOptions.MaxDegreeOfParallelism`; pass a `ReadOnlyMemory<byte>` for the
  genuine parallel path. On the new builder this combination is simply unwriteable — `Parallel()`
  does not exist on a `Stream` source — so the flat method is the only place the lie remains.
- Nested and generic target types are rejected (`PARQ009`/`PARQ010`) rather than supported.
- .NET Framework needs the `Parquet.SourceGenerator.Legacy` package. The classic backend has no
  `ArrayPool` story — `DataColumn` allocates its own arrays — so it does not inherit the main
  package's allocation characteristics, and it offers no streaming or parallel reader.
- IronCompress ships no `win-x86` native binary, so 32-bit .NET Framework applications fail at
  runtime on any compressed write.
- See [docs/07-KNOWN-LIMITATIONS.md](docs/07-KNOWN-LIMITATIONS.md) for the full audit.

---

## [0.0.3] - 2026-09-05

> Backfilled retrospectively from git history (`v0.0.2..v0.0.3`, 55 commits). No changelog section
> was written when this version was published, so these notes were reconstructed after the fact
> from commit subjects and pull request descriptions rather than recorded at release time.

### Added
- **Parallel reader over `ReadOnlyMemory<byte>` (real concurrency)**: `ReadParquetParallelAsync`
  now decodes row groups concurrently — one `ParquetReader` over one stream per worker, groups
  claimed dynamically — with results in file order. Over an arbitrary `Stream` it stays
  sequential, because a stream cannot be shared between readers, and `maxDegreeOfParallelism` is
  not honoured there. Before this release the "parallel" reader did no parallel work.
- **Native `T[]` reader overloads and zero-copy `List<T>` deserialization**, with threshold-driven
  drop-down optimizations for small reads.
- **`TimeOnly` columns**, via a migration to Parquet.Net's `TimeDataField`.
- **Drop-in attribute interoperability**: `System.Text.Json` and Parquet.Net's own attributes are
  honoured, so an existing annotated model does not need a second set of attributes.
- **Diagnostic `PARQ011`**: a member type that Parquet.Net 6 supports but the 4.x/5.x
  `DataColumn` API cannot represent is reported by the classic backend, pointing at the package
  switch rather than at the model.
- **`InvalidDataException` with a descriptive message** when a required schema column is missing
  from the file being read.
- **Real-world benchmark datasets**, provenanced and stored via Git LFS, with reporting scripts.
- **Source-controlled golden generated code** plus an `/update-golden` workflow, so a change to
  emitted output is visible in review.
- **Fail-fast cancellation for parallel workers** through a linked `CancellationTokenSource`.

### Performance
- **SIMD hardware acceleration** for column transforms and timestamp conversions.
- **Zero-copy blittable struct array materialization** via `MemoryMarshal.Cast`, made memory-safe
  and covered by a full property test matrix.
- **Dictionary string deduplication with an L1 span cache** on the read path.
- **String and binary serialization no longer box**, with a universal zero-boxing IL bytecode
  assertion across all models to keep it that way.
- **Zero-copy nullable writes** through `ParquetRowGroupWriter.WriteAllPartsAsync`.
- **Eager progressive column buffer return** during row-group serialization.
- **Single-pass layout probe and lifted `ArrayPool` rentals** in the parallel worker.
- **`CollectionsMarshal.AsSpan`** for `List<T>` write extraction.
- **A dedicated sequential buffer reader**, removing recursive delegation from the emitted read
  path.

### Changed
- Parquet.Net bumped from 6.0.3 to 6.1.0; test SDK and runner dependencies bumped.
- `CodeEmitter` and `LegacyCodeEmitter` split into modular partial classes and then into
  composable emitter components, removing the shared-helper class entirely.
- Analyzer and style enforcement added: `PublicApiAnalyzers` guarding the public API surface,
  `Meziantou.Analyzer` for performance and correctness rules, `.editorconfig` code style and
  naming rules at warning level, and CSharpier for deterministic formatting.
- CI hardened: a code coverage gate with a sticky PR comment (raised to 85% line / 70% branch),
  an automated IL interrogation regression gate during `dotnet test`, all GitHub Actions pinned
  to 40-character commit SHAs, and tiered benchmarking for fast PR slices and deep profiling.
- Diagnostics tooling added for investigation rather than for consumers: an `ilspycmd`-based IL
  interrogation workflow and a `dotnet-dump` memory profiling and triage workflow.

### Fixed
- Release workflow hydrates Git LFS test datasets and verifies their provenance, so a release
  build no longer runs against LFS pointer files.
- The PR coverage sticky comment no longer fails on fork pull requests, where the token is
  read-only.
- `issue_comment` workflows authorize the commenting actor and sanitize their input.
- `gh pr` commands in CI specify `--repo`, so they work before the repository is checked out.
- Benchmark model column names align with the reflection serializer, the baseline is guarded in
  tests, and a 1.0x result is reported as parity rather than as a speedup.

---

## [0.0.2] - 2026-09-02

> Backfilled retrospectively from git history (`v0.0.1..v0.0.2`, 20 commits). No changelog section
> was written when this version was published, so these notes were reconstructed after the fact
> from commit subjects and pull request descriptions rather than recorded at release time.

### Added
- **`Parquet.SourceGenerator.Legacy`**: a second generator emitting against the Parquet.Net
  4.x/5.x `DataColumn` API, which is what restores .NET Framework 4.7.2 support. It accepts a
  narrower set of member types than the v6 backend. Introduced as `Parquet.SourceGenerator.V5`
  and renamed to `.Legacy` before release, because the API break is between v5 and v6 rather than
  between v4 and v5, so one backend covers both. The release workflow verifies the extra package.
- **`ReadParquetStreamAsync`**: an `IAsyncEnumerable<T>` streaming *reader*, complementing the
  streaming write path that shipped in `0.0.1`.
- **.NET 9 target framework support**, with package consumption tested against both .NET 8 and
  .NET 9.
- **Compiler diagnostics `PARQ006`–`PARQ010`**: unsupported member types, unassignable members,
  types with no parameterless constructor, and nested or generic target types. Shapes that
  previously emitted uncompilable code or failed at runtime — positional records, get-only
  members, unsupported types — now fail the build with a pointer to the declaration responsible.
- **`docs/BENCHMARKS.md`** as a standalone document, and a dedicated `PACKAGE_README.md` for
  NuGet packaging.

### Changed
- **Un-ordered columns default to declaration order** rather than to an arbitrary order, so a
  model without explicit column ordering produces a stable, predictable schema.
- **`ParquetSerializerOptions` reaches the reader.** All three readers assigned the options and
  then never used them; they are now threaded through to `ParquetReader.CreateAsync`. This is
  plumbing rather than a behaviour change today, since the options type carries only write-side
  settings, but a read-relevant option added later will not be silently dropped.
- **`rowGroupSize` no longer uses its default value as an "unset" sentinel.** Asking for exactly
  50,000 was indistinguishable from not asking, and whenever options carried a size it overrode
  the explicit method argument — the more specific value losing to the more general one. The
  parameter is now nullable, so "not supplied" is representable.
- Benchmarks standardized on a 100k item scale across all suites, with a concise headline table
  embedded in the READMEs and refreshed by CI.
- READMEs use plain Markdown rather than raw HTML so they render on NuGet, and the status badge
  and installation guide reflect the published packages.

### Fixed
- **The read path reallocated its result list once per row group.** `results` was pre-sized to the
  file's total row count and then had `Capacity` reassigned to the running total inside the
  row-group loop, so each row group allocated a *smaller* array and copied into it before the list
  grew back — O(groups × rows) of copying to arrive at the capacity it already had.
  Single-row-group files were unaffected, which is why it survived; the multi-row-group files
  `WriteParquetBatchedAsync` produces are the ones that paid.
- **The parallel reader could lose its pre-allocated destination array** while making stream reads
  thread-safe; the array is now preserved.
- Release workflow uses embedded PDB symbols and passes `--no-symbols` to `nuget push`, so
  publishing does not fail on the symbol package.
- Benchmarks use the `InProcess` execution toolchain, resolving reference-assembly build errors.

### Performance
- **`ReadParquetParallelAsync` materialises into a single pre-sized array** indexed by row-group
  offset, rather than concatenating per-group results. (The decode itself was still sequential at
  this release; genuine concurrency arrived in `0.0.3`.)

---

## [0.0.1] - 2026-08-05

Initial published release, alongside the `0.0.1-dev.1` and `0.0.1-dev.2` prereleases.

### Added
- **Roslyn incremental source generator**: compiles zero-reflection Parquet serializers and
  deserializers against Parquet.Net low-level primitives.
- **Native AOT support**, exercised on every CI run by publishing the AOT test project with
  `-r linux-x64` and executing the resulting native binary. `linux-x64` only, and note that
  Parquet.Net 6.0.3 emits its own trim (`IL2104`) and AOT-analysis (`IL3053`) warnings — so this
  covers the paths the test exercises rather than guaranteeing AOT safety in general.
- **`Guid` columns** written as native 16-byte values via pooled struct buffers rather than
  strings.
- **`IAsyncEnumerable<T>` streaming** directly into chunked row groups.
- **Microsecond `Int64` timestamps** via `[ParquetTimestamp(ParquetTimestampUnit.Microseconds)]`.
- **`ParquetSerializerOptions`** for `RowGroupSize`, `CompressionMethod` (`None`, `Snappy`, `Gzip`,
  `Lz4`, `Brotli`, `Zstd`) and `CompressionLevel` (`Optimal`, `Fastest`, `NoCompression`,
  `SmallestSize`; unset keeps Parquet.Net's default). `MaxDegreeOfParallelism` supplies the worker
  count for the buffer-based parallel read.
- **Compiler diagnostics `PARQ001`–`PARQ005`**: partial-type enforcement, duplicate column names,
  no serializable members, ignored non-public members, and invalid decimal precision/scale.
- **CI workflow** building, testing and packing the solution. Benchmarks run on demand.
- Generated logo and favicon set, with the design record.

### Fixed
The following landed after the options were documented but before the first publish, so no
released version ever carried them:

- `CompressionMethod` was accepted and discarded — no compression setting ever reached the writer.
- `[ParquetTimestamp(Microseconds)]` mapped to `DateTimeFormat.DateAndTime`, which Parquet.Net
  defines as *millisecond* precision, so microsecond columns were silently written coarser and the
  sub-millisecond component was lost. Now maps to `DateAndTimeMicros`.
- `ParquetTimestampUnit.Nanoseconds` and `ParquetSerializerOptions.UseMicrosecondTimestamps` are
  removed. Neither could work: Parquet.Net has no nanosecond format, and the schema is emitted at
  compile time so no runtime flag can change a column's encoding.
- Column names are escaped and hint names qualified by namespace in the emitted code.
- The NuGet packages were not usable as published; packaging is fixed and verified in CI, and the
  release workflow publishes the tagged version.
