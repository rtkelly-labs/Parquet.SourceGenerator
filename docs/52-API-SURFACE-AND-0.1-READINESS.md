# 52 - API Surface Map and 0.1 Readiness Assessment (#477)

> **Status:** Analysis, not a decision record. Written against `main @ 2cc1508` (2026-10-01) plus
> the open draft PRs [#489](https://github.com/rtkelly13/Parquet.SourceGenerator/pull/489) (single
> reader) and [#576](https://github.com/rtkelly13/Parquet.SourceGenerator/pull/576) (unified
> `<Model>Batch`, stacked on #489). Every count below was read from the repository or from the
> `derived-outputs` artifact of the latest successful `main` CI run (36911545026). Nothing was built
> locally. Judged against [47](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md) §2 (the member test) and §8
> (the release gate).

## Verdict

**Ready after 10 specific changes.** The shipped package surface is already close to the §4.1
target. The emitted consumer surface is not: it is mid-contraction, and two of the three
contraction PRs are unmerged. The gate items that fail today are the README, the batch ownership
contract (#369), the `#459` budget gate, and the stability classification (#482). Section 4 has
the ordered list.

How the counts are defined: `MEMBERS` in `*.api.shape.txt` is the number of non-comment lines of
the model's `.api.txt`. That counts type declarations as well as members
([17](./17-GENERATED-API-BASELINES.md), `scripts/CodeMetrics.cs`). `PARAMETERS` sums the parameters
of every listed signature.

Count caveat: #489's description says OrderEvent goes `65/48 -> 48/34`; its later rebase comment
says `47/23`. #576's derived diff takes `48/34` as its base. This document uses the derived-comment
numbers and marks them with the PR.

---

## 1. Shipped package surface

### 1.1 Packages

| Package | Ships | Public API | Governed by |
|:---|:---|--:|:---|
| `Parquet.SourceGenerator.Attributes` | A library, TFMs `netstandard2.0;2.1;net8.0;net9.0` | 16 types | `PublicAPI.Shipped.txt` (44 entries), `PublicAPI.Unshipped.txt` (79 entries), `RS0016`, ledger |
| `Parquet.SourceGenerator` | Analyzer only (`IncludeBuildOutput=false`); depends on Attributes | 0 (every type `internal`, #461) | none needed; `src/api/seams.txt` (5 internal seams, `PARQAPI002`) |
| `Parquet.SourceGenerator.Legacy` | Analyzer only; depends on Attributes; compile-links the modern `Diagnostics/`, `Models/`, `Parser/` and `Emitter/Components/` sources | 0 | same seams file |

Entry counts exclude the `#nullable enable` line. There are no adapter, NodaTime, CLI or analyzer
packages on `main`. `Parquet.SourceGenerator.ApiGates` (`tools/`) is a build-time analyzer, never
packed. PR #476 (open, not draft) adds a `Parquet.SourceGenerator.NodaTime` package plus
`[ParquetTypeAdapter]` and `[ParquetAdapter]` attributes to the Attributes package. It is out of
this assessment's scope but affects the surface (see section 4.3).

The released tags are `v0.0.1` to `v0.0.3`. `Directory.Build.props` still says `0.0.1` and defers
to the tag. **9 of the 16 public types are in `Shipped.txt`; 7 have never shipped** (listed in 1.2),
so they can change without a break against any consumer.

### 1.2 Public types in `Parquet.SourceGenerator.Attributes`

| Type | Kind | Public members | Shipped? |
|:---|:---|--:|:---:|
| `ParquetSerializableAttribute` | attribute, class/struct | ctor | yes |
| `ParquetColumnAttribute` | attribute, property/field | ctors (2), `Name`, `Order`, `Encoding`, `Deduplicate` | `Name`/`Order` yes; `Encoding`/`Deduplicate` no |
| `ParquetIgnoreAttribute` | attribute | ctor | yes |
| `ParquetDecimalAttribute` | attribute | ctor(precision, scale), `Precision`, `Scale` | yes |
| `ParquetTimestampAttribute` | attribute | ctor(unit), `Unit` | yes |
| `ParquetSortKeyAttribute` | attribute | ctor | no |
| `ParquetGeneratorOptionsAttribute` | assembly attribute | `FeatureLevel` | no |
| `ParquetSerializerOptions` | sealed class | 15 properties + `Default` | core yes; 11 limits/hints no |
| `ParquetCompressionMethod` | enum | 6 values | yes |
| `ParquetCompressionLevel` | enum | 4 values | yes |
| `ParquetColumnEncoding` | enum | 4 values | no |
| `ParquetTimestampUnit` | enum | 2 values | yes |
| `ParquetGeneratorFeatureLevel` | enum | 3 values | no |
| `ParquetColumnStatistics<T>` | readonly struct | ctor (6 params), 5 value props (`Min`, `Max`, `NullCount`, `DistinctCount`, `HasMinMax`), `IsKnownNonNull`, 7 `MayContain*` methods, `Equals`, `==`, `!=` | no |
| `ParquetColumnStatistics` | static class | `FromRaw<T>(object?, object?, long?, long?)` | no |
| `ParquetPruneStatistics` | sealed class | 7 properties (6 get/set, `RowGroupsPruned` get-only), `Reset()`, ctor; 16 API lines | no |

Seven attributes in total, five enums, one options class and three statistics types. All attributes are `sealed`, non-multiple, inherited. Targets: `Class|Struct` for
`[ParquetSerializable]`; `Property|Field` for every column attribute; `Assembly` for
`[ParquetGeneratorOptions]`.

Internal and unreachable: `NullableColumnExtractor`, `VectorizedColumnTransforms`,
`IsExternalInit`. These were the #461 contraction.

### 1.3 `ParquetSerializerOptions` members and defaults

| Property | Default | Applies to |
|:---|:---|:---|
| `RowGroupSize` | `50_000` | writes from collections and `IEnumerable<T>`; a `<Model>Batch` write is one row group |
| `MaxDegreeOfParallelism` | `-1` | `.Parallel()` reads only |
| `CompressionMethod` | `Snappy` | writes |
| `CompressionLevel` | `null` (codec default) | writes |
| `DeduplicateStrings` | `false` | writes |
| `DictionaryEncodingThreshold` | `null` | writes |
| `DictionaryEncodingSampleSize` | `null` | writes |
| `ColumnEncodingHints` | empty ordinal dictionary, get-only | writes; "takes precedence over or supplements" `[ParquetColumn(Encoding)]` |
| `MaxAllocationValues` | `10_000_000` | reads |
| `MaxDictionaryEntries` | `1_000_000` | reads |
| `MaxStringLengthBytes` | `1_048_576` | reads |
| `MaxRowGroupCount` | `100_000` | reads |
| `MaxNestingDepth` | `64` | reads |
| `MaxDecompressedPageSize` | `67_108_864` | reads |
| `MaxDecompressionExpansionRatio` | `500` | reads |

Seven write knobs, one execution knob, seven hostile-input limits, in one mutable class. The type
does not say which half applies to a call, and `Default` returns a new instance on each access.

### 1.4 Diagnostics (`DiagnosticDescriptors.cs`: 16 IDs, 14 errors, 2 warnings)

| ID | Severity | Meaning |
|:---|:---:|:---|
| PARQ001 | Error | `[ParquetSerializable]` type is not `partial` |
| PARQ002 | Error | duplicate Parquet column name |
| PARQ003 | Warning | no serializable public properties |
| PARQ004 | Warning | non-public property with `[ParquetColumn]` ignored |
| PARQ005 | Error | invalid `[ParquetDecimal]` precision/scale |
| PARQ006 | Error | unsupported member type |
| PARQ007 | Error | member not assignable by the generated reader |
| PARQ008 | Error | no accessible parameterless constructor |
| PARQ009 | Error | nested type not accessible to generated code |
| PARQ010 | Error | generic type |
| PARQ011 | Error | type supported by Parquet.Net 6 but not the 4.x/5.x API (legacy backend) |
| PARQ012 | Error | cyclic compound type |
| PARQ013 | Error | compound nesting deeper than the supported maximum |
| PARQ014 | Error | `[ParquetSortKey]` member not eligible for row-group pruning |
| PARQ015 | Error | undefined `ParquetGeneratorFeatureLevel` value |
| PARQ016 | Error | two targets flatten to the same generated type name |

Both generators use the same descriptor file, so the IDs are shared. Findings:

- [13](./13-COMPILER-DIAGNOSTICS.md) catalogues 13 of the 16 IDs. **PARQ012, 013 and 014 have no
  entry.** [01](./01-VISION-AND-ARCHITECTURE.md) and [03](./03-INCREMENTAL-GENERATOR-PIPELINE.md)
  cite a range `PARQ001 - PARQ099` that does not exist.
- There is no `AnalyzerReleases.Shipped.md`. Users put these IDs in `NoWarn` and
  `.editorconfig`, and nothing governs their stability. The ledger covers API, not IDs.
- Open PR #476 assigns **PARQ016** to "invalid adapter". That collides with `main`'s PARQ016
  (`GeneratedNameCollision`). Whichever merges second has to renumber.
- `PARQAPI001` is retired; `PARQAPI002` (internal seams) is the only live repo gate ID. Neither is
  a consumer diagnostic.

### 1.5 MSBuild properties and switches

| Switch | Where | Effect |
|:---|:---|:---|
| `<ParquetGeneratorFeatureLevel>` | MSBuild, exposed as `CompilerVisibleProperty` by both packages' `build/*.props` | project-wide feature level |
| `[assembly: ParquetGeneratorOptions(FeatureLevel = ...)]` | source | same, lower precedence than MSBuild |
| Apache.Arrow reference | project reference | implicit switch: emits `{Type}.Arrow.g.cs` per Arrow-representable model |
| Parquet.Net version | project reference | chooses nothing today; the user picks the modern or `.Legacy` package |

`Level1Flat` is the only level that changes behaviour: `ParquetIncrementalGenerator.cs:157` maps
it to `CompoundKinds.None`. `Level2CompoundPreview` (the default) and `Level3ModernCSharp` select
identical `CompoundKinds.Struct | List`. `Level3ModernCSharp` is referenced only by its definition
and by the PARQ015 message. In effect there is one real switch, spelled as a three-value enum plus
an assembly attribute plus an MSBuild property, and the default is named "Preview".

---

## 2. Emitted consumer surface

The emitters write this into the consumer's compilation. It exists in no shipped assembly and is
reviewed through the derived `.api.txt` diff ([18](./18-API-CHANGE-CONTRACT.md)). Golden models:
`ListOrder` (list), `NestedOrder` (nested struct), `PocoOrder` (nested class), `OrderEvent` (flat,
nullable, all-leaf), `ScalarMetric` (numeric/nullable matrix), `SortedShipment` (sorted/pruned),
`LegacyRecord` (legacy backend).

### 2.1 Totals per golden model

| Model (shape) | `main` MEMBERS/PARAMS | After #489 | After #489 + #576 |
|:---|:---:|:---:|:---:|
| ListOrder, NestedOrder, PocoOrder (compound; no batches) | 35/31 | 19/18 | 19/18 (unchanged) |
| OrderEvent (flat, nullable) | 65/48 | 48/34 (47/23 per rebase note) | **37/35** |
| ScalarMetric (flat, numeric/nullable) | 65/48 | 48/34 | **39/36** |
| SortedShipment (sorted, pruned) | 58/64 | 41/50 | **34/50** |
| LegacyRecord (legacy backend) | 6/14 | 6/14 | 6/14 |

The 0.1 contraction tracker (#477) reports 377 to 290 members across the six modern models for
#486 to #488, and OrderEvent from 82 to 48 members across the whole programme. Each golden `.g.cs`
also shrinks by roughly 500 to 700 lines under #489 because the `List<T>` read bodies are no longer
emitted.

### 2.2 OrderEvent on `main`, by purpose (65 lines)

| Purpose | Members | What |
|:---|--:|:---|
| **Read** (builder) | 27 | `OrderEventParquet.From(Stream)`, `From(ReadOnlyMemory<byte>)`; four state types: `StreamSource` (7), `MemorySource` (8), `FilteredSource` (5), `ParallelSource` (4); terminals `ToListAsync`, `ToArrayAsync`, `AsAsyncEnumerable`, `Batches`, plus `WithOptions`, `Where`, `Parallel` |
| **Write** (rows) | 6 | `OrderEventParquetExtensions` class; three `WriteParquetAsync` overloads (`IReadOnlyCollection<T>`, `IAsyncEnumerable<T>`, `OrderEventColumnarBatch`); `WriteParquetBatchedAsync(IEnumerable<T>)`; `static readonly Schema` |
| **Write** (columnar input type) | 13 | `OrderEventColumnarBatch`: ctor with 11 parameters, 10 `ReadOnlyMemory` lanes (one a definition-levels lane), `RowCount` |
| **Batch read** (columnar) | 12 | nested `OrderEventParquetExtensions.ColumnBatch`: 9 `*Span` lanes, `RowCount`, `RowGroupIndex` |
| **Predicate and pruning** | 7 | `OrderEventRowGroupMetadata` (read-only): `RowGroupIndex`, `RowCount`, `HasStatistics`, three `ParquetColumnStatistics<T>` (Id, Name, Score). The other columns are not projected into metadata |
| **Arrow** | 0 | not in any golden model, see 2.5 |

Other models change this mix as follows.

- **Compound (`ListOrder`, `NestedOrder`, `PocoOrder`):** read builder and metadata for the first
  column only (`Id`); no `Batches()`, no `ColumnBatch`, no `ColumnarBatch`. Write is the two
  `WriteParquetAsync` overloads, `WriteParquetBatchedAsync`, `Schema`.
- **Sorted (`SortedShipment`):** adds four static reads, which no other model has.
  `ReadParquetBySequenceAsync`, `ReadParquetByShippedAtAsync`, `ReadParquetSequenceRangeAsync`,
  `ReadParquetShippedAtRangeAsync`. Each takes `Stream`, `ParquetPruneStatistics?` then
  `ParquetSerializerOptions?` then `CancellationToken`, and returns `Task<List<T>>`. They survived
  #480 (flat reads removed) and, as written in #489, will survive #479 (`ToListAsync` removed). That
  makes them the only `List<T>` read left, and the only reads that are `Stream`-only. PARAMETERS is
  `50` through both PRs.
- **Legacy (`LegacyRecord`):** one class, `LegacyRecordParquetLegacyExtensions`, with
  `ReadParquetAsync` (`List<T>`), `ReadParquetArrayAsync`, `WriteParquetAsync(IReadOnlyList<T>)`,
  `WriteParquetBatchedAsync(IEnumerable<T>)`, `Schema`. No `From`, no metadata, no
  `IAsyncEnumerable`, no batches, no Arrow.

### 2.3 Modern versus legacy

| Capability | Modern | Legacy | Source of truth |
|:---|:---|:---|:---|
| Entry point | `<Model>Parquet.From(...)` | flat static `ReadParquet*Async` | [14](./14-COMPATIBILITY-MATRIX.md) |
| Extensions class name | `<Model>ParquetExtensions` | `<Model>ParquetLegacyExtensions` | derived `.api.txt` |
| Collection write parameter | `IReadOnlyCollection<T>` | `IReadOnlyList<T>` | derived `.api.txt` |
| `IAsyncEnumerable<T>` read/write | yes | no | |
| Row-group pruning, `Parallel()` | yes | no | #494 |
| Columnar write, batch read | yes | no (batch read permanent, #490 allowlist) | [49](./49-LEGACY-PARITY-490.md) |
| Arrow | when Apache.Arrow referenced | no (temporary allowlist entry, #490) | [49](./49-LEGACY-PARITY-490.md) |
| Nested types | yes | no (`flat only`) | [42](./42-NESTED-BACKEND-SCOPE-176.md) |

[49](./49-LEGACY-PARITY-490.md) rule 1 says the two backends should emit "the same type names,
member names, signatures". Today they share none of those for reads and diverge on the write
parameter type. [14](./14-COMPATIBILITY-MATRIX.md) and [47](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md)
§5.4 still describe the superseded subset policy. 49 does not say parity is a precondition for
`0.1`; #490's title is `chore(0.1): one generated API across backends`.

### 2.4 After #489 and #576

| Concern | Before | After |
|:---|:---|:---|
| Read entry | `From` returns one of two source types; `Where` and `Parallel` return two more | `From` returns one `readonly struct <Model>ParquetReader` with `WithOptions`, `Where`, `Parallel`, `ToArrayAsync`, `AsAsyncEnumerable`, `AsBatches` |
| Materialised terminals | `ToListAsync`, `ToArrayAsync` | `ToArrayAsync` only (249 call sites migrated in #489) |
| Illegal combinations | unrepresentable by type | throw `NotSupportedException`: `From(Stream).Parallel()`, `.Where(..).Parallel()`, `.Parallel().Where(..)`, `.Parallel()` with `AsAsyncEnumerable` or batches, `.Where(..).AsBatches()`, `.Where(a).Where(b)`. Owner accepted these on #489 |
| Batch types | `<Model>ColumnarBatch` (write, `ReadOnlyMemory` lanes) and `...Extensions.ColumnBatch` (read, `ReadOnlySpan` lanes, differently shaped) | one `readonly struct <Model>Batch` |
| Nullable lanes | write: packed values plus `...DefinitionLevels`; read: expanded `ReadOnlySpan<T?>` | both: packed values plus `...DefinitionLevels` (layout A); opt-in `Fill<Column>Nullable(Span<T?>)` |
| Batch lifetime | copyable struct over pooled arrays, nothing stops escape (#369) | lease: a kept batch throws `ObjectDisposedException`; copied `ReadOnlyMemory<T>` is still unprotected |
| `RowGroupIndex` on batch | present | removed |
| `Batches` terminal | `Batches()` | `AsBatches()` |

Not changed by either PR: `WriteParquetBatchedAsync` (#512, undecided), the sorted-key reads,
`Schema`, `ParquetPruneStatistics`, the Arrow overload, the options class, legacy.

### 2.5 Arrow is invisible to the review diff

The conditional bridge emits one public method into the extensions class when the compilation
references Apache.Arrow ([14](./14-COMPATIBILITY-MATRIX.md#apache-arrow-recordbatch-ingestion-experimental-177)):

```csharp
public static async Task WriteParquetRowGroupAsync(
    ParquetWriter writer, RecordBatch batch,
    ParquetSerializerOptions? options = null, CancellationToken cancellationToken = default);
```

No golden model references Apache.Arrow (`grep -ci arrow` is `0` for all seven `.g.cs` files), so
this signature appears in no `.api.txt`, no shape count, and no derived-output PR comment. It is
the same shape (`ParquetWriter` parameter, row-group scope) that #481 made `internal` for every
other writer. The nested `ArrowValueMemoryManager<T>` is `private`. The rest of the 1,072-line
`ArrowBridgeEmitter` emits internals.

---

## 3. Complexity and the narrow-contract principle

### 3.1 Surface complexity

Concepts a user must name, for a flat nullable model.

| Concept | `main` | After #489 + #576 |
|:---|:---|:---|
| Model annotations | 6 attributes (`Serializable`, `Column`, `Ignore`, `Decimal`, `Timestamp`, `SortKey`) | same |
| Options | `ParquetSerializerOptions` (15 properties) | same |
| Read entry and state | `<Model>Parquet`, 4 source types | `<Model>Parquet`, 1 reader |
| Read terminals | 4 (`ToList`, `ToArray`, `AsAsyncEnumerable`, `Batches`) | 3 (`ToArray`, `AsAsyncEnumerable`, `AsBatches`) |
| Row-group filtering | `Where` + `<Model>RowGroupMetadata` + `ParquetColumnStatistics<T>`; and, on sorted models, 4 key reads + `ParquetPruneStatistics` | same |
| Write | `WriteParquetAsync` x3, `WriteParquetBatchedAsync` | same (4 overloads) |
| Columnar | `ColumnarBatch` (write) and `ColumnBatch` (read) | `<Model>Batch` |
| Schema | `Schema` field, type `Parquet.Schema.ParquetSchema` | same |
| Total distinct names | about 22 | about 15 |

Ways to do the same thing:

- **Read all rows:** `ToListAsync`, `ToArrayAsync` (gone after #489); with `From(Stream)` or
  `From(bytes)`; sequential or `.Parallel()` (buffer only).
- **Read some rows:** `.Where(predicate)` over metadata, or on a sorted model the `ReadParquetBy*`
  family. Two grammars for one intent, with different parameter order, stream-only, `List<T>`.
- **Write rows:** `WriteParquetAsync(IReadOnlyCollection)`, `WriteParquetBatchedAsync(IEnumerable)`,
  `WriteParquetAsync(IAsyncEnumerable)`. Chunking is already controlled by `RowGroupSize` (#512).
- **Choose a column encoding:** `[ParquetColumn(Encoding)]` or `ColumnEncodingHints["name"]`. The
  options doc comment says the hint wins; the attribute doc does not say so.
- **Deduplicate strings:** `[ParquetColumn(Deduplicate)]` or `ParquetSerializerOptions.DeduplicateStrings`.
- **Set the feature level:** MSBuild property or assembly attribute (precedence documented in
  [29](./29-FEATURE-LEVELS.md)).

Option interplay that the types hide: `MaxDegreeOfParallelism` only matters after `.Parallel()`;
`RowGroupSize` is ignored by a `<Model>Batch` write; the seven `Max*` limits are read-only;
`ColumnEncodingHints` and `Deduplicate*` are write-only.

### 3.2 Implementation complexity

From the `derived-outputs` artifact (`code-metrics.md`, `duplication.md`, `callgraph-summary.md`)
and `wc -l` over `src/`.

| Measure | Value |
|:---|:---|
| Hand-written C# under `src/` | 16,056 lines |
| Largest files | `CodeEmitter.cs` 2,738; `TargetParser.cs` 1,962; `ArrowBridgeEmitter.cs` 1,072; `LegacyCodeEmitter.cs` 910; `CompoundMapping.cs` 844 |
| `CodeEmitter` (the hub) | CC 139, class coupling 33, SLOC 2,729, MI 44 |
| `TargetParser` | CC 242, class coupling 69 (gate 95), SLOC 1,941 |
| Worst method CC | 25 (`ArrowMappingComponent.TryMap`), exactly at the `CA1502` gate |
| Duplication | 82 clusters, 11,362 duplicated tokens; top: `EmitReadArrayAsync` against `EmitReadAsync` (span 217), `Legacy Initialize` against modern `Initialize` (174), `LegacyCodeEmitter.EmitSource` against `CodeEmitter.EmitSource` (128), `ReadBuilderComponent.EmitMemorySource` against its sibling (108) |
| Call graph | modern 264 nodes, 439 edges, 87% call sites unresolved, max fan-out 28; legacy 112 nodes, 137 edges, max fan-out 13 |
| Emitted per model | 1,019 (legacy) to 3,239 SLOC; ELOC 1,027 to 1,493 for modern models; 18,857 emitted lines across the seven goldens |
| ELOC per public member | 16 to 43 for modern models; 49.8 for legacy |

The 87% unresolved call-site rate is the cost of emitting through string builders: the graph shows
the emitter's shape but cannot follow the text it writes. Two emitters (modern, legacy) with 128-,
174- and 217-token duplicate spans mean most defects are fixed twice. Surface contraction does not
shrink this: #489 deletes read bodies and so reduces duplication (the derived comment shows
`duplication.md` -9/+1), but `CodeEmitter` remains one file with 33 coupled types. [49](./49-LEGACY-PARITY-490.md)
step 2 (#492) is the structural fix; it is not on the `0.1` gate.

### 3.3 Judgement against "Broad implementation, narrow contract, strong tests, explicit safety boundaries"

**Broad implementation.** Met. Compound types, Arrow ingestion, SIMD paths, sorted pruning, two
backends. Nothing here argues for removing capability.

**Narrow contract.** Not yet. Where the surface is larger than needed:

| # | Finding | Evidence | Member test (§2) |
|:--|:---|:---|:---|
| 1 | Sorted-key read family duplicates `Where` | 4 members on `SortedShipment`, stream-only, `List<T>`, argument order differs; doc 20 already says `ParquetPruneStatistics` "is superseded" | strategy, not intent |
| 2 | `ParquetPruneStatistics` is public with 7 properties, 6 settable | only generated code writes them; 16 API lines | instrumentation (tier 2 in §3) |
| 3 | `ParquetColumnStatistics<T>` ctor and `ParquetColumnStatistics.FromRaw` are public only because generated code in the consumer assembly calls them | `FromRaw` appears in the emitted metadata constructor (in the derived `OrderEventParquetExtensions.g.cs`) | plumbing; same pattern as #459 |
| 4 | `Schema` is a public static readonly **field** of a backend type | type is `Parquet.Schema.ParquetSchema`, whose assembly differs per backend | exposes the Parquet.Net version; not user intent |
| 5 | Three-level feature enum with one effective switch | section 1.5 | `Level3ModernCSharp` is dead public API |
| 6 | `WriteParquetBatchedAsync` | #512; "batch" now also means columnar chunk | naming collision |
| 7 | `<Model>Batch` exposes definition levels as the public nullable representation | #576 layout A | §2 names definition levels as implementation strategy. This is a conscious trade: packing avoids a copy. Record it as a decision, not a leak |
| 8 | Arrow public method takes `ParquetWriter` | section 2.5 | the #481 pattern |

Where the surface is inconsistent:

| Axis | Inconsistency |
|:---|:---|
| Naming of generated types | `<Model>Parquet`, `<Model>ParquetExtensions`, `<Model>ParquetReader` carry `Parquet`; `<Model>Batch`, `<Model>RowGroupMetadata` do not; legacy is `...ParquetLegacyExtensions` |
| Terminal verbs | `ToArrayAsync`, `AsAsyncEnumerable`, `AsBatches` (to/as); writes `WriteParquetAsync` with a `Parquet` infix the reader lacks |
| Read versus write batches (resolved by #576) | `ColumnBatch` (spans, `T?`) versus `ColumnarBatch` (memory, packed) today |
| Collection parameter | modern `IReadOnlyCollection<T>`, legacy `IReadOnlyList<T>` |
| Read paths | modern builder; legacy flat methods; sorted-key static methods |
| Option placement | `options` then token everywhere, except sorted-key reads: `pruneStatistics`, `options`, token |
| Nullability on the wire | `main`: `PayloadSpan` is `ReadOnlySpan<byte[]>` on read, `ReadOnlyMemory<ReadOnlyMemory<byte>?>` on write |

**Accidentally public** (emitted members that should not be): `ParquetColumnStatistics` ctor and
`FromRaw`, `ParquetPruneStatistics` setters and `Reset`, `Schema`, the Arrow row-group writer. Slot
indices (#459) are fixed: `RowGroupMetadata`'s constructor is internal and no `column_N` name
appears in any `.api.txt`. The budget half of #459 is not done.

**Strong tests and safety boundaries.** Out of scope here beyond the gate table below. Coverage on
the #489 run: `Parquet.SourceGenerator` 92.7% lines, Attributes 97.2%, Legacy 76.1% (sub-target).

### 3.4 Concrete trims

| Trim | Effect | Cost |
|:---|:---|:---|
| Make `ParquetPruneStatistics` and the four sorted-key reads internal; offer the same intent as `.Where(m => m.Sequence.MayContain(k))` | -16 package API lines; -4 emitted members and -22 parameters on sorted models | loses the "sorted" binary-search fast path unless the reader applies it inside `Where` |
| Emit `ParquetColumnStatistics.FromRaw` and the stats ctor as an `internal` helper per assembly, or mark `[EditorBrowsable(Never)]` and record in the ledger | removes a public type and 2 public members | one more emitted type per consumer, or a documented wart |
| Replace the `Schema` field with an internal accessor; if consumers need it, expose a method returning the schema over an interface | removes the Parquet.Net type from the contract | interop tests use it today |
| Delete `ParquetGeneratorFeatureLevel` and `ParquetGeneratorOptionsAttribute`; keep one MSBuild bool, for example `ParquetGeneratorFlatOnly`, or keep `Level1Flat` and delete `Level2`/`Level3` | -2 types, -8 API lines | #290 shipped intent; unshipped API so no break |
| Fold `WriteParquetBatchedAsync` into `WriteParquetAsync(IEnumerable<T>)` (#512 option 1) | -1 overload per model | must keep the single-row-group fast path when `RowGroupSize` is unset |
| Align legacy `IReadOnlyList<T>` with modern `IReadOnlyCollection<T>` | removes a diverging signature | none: the parameter type widens, so existing callers still compile |
| Document or enforce attribute-versus-option precedence for `Encoding` and `Deduplicate` | removes a doubt, not a member | none |

---

## 4. 0.1 baseline readiness

### 4.1 Gate items from [47](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md) §8

| Gate item | State | Evidence |
|:---|:---:|:---|
| Critical/high correctness and security addressed or scoped out | open | #369 (batch ownership) open even with #576's lease; #381 untouched; the correctness/security list in #477 is unchecked |
| Coverage, golden, IL, AOT, perf gates non-vacuous | partial | derived outputs and CI gate matrix ([51](./51-CI-GATE-MATRIX.md)) exist; #469 (benchmark gate) is still a draft |
| Public API contraction (#459, #461, #478-#481) | **partial** | #461 and #481 done; #480 done; #459 slot indices done, **budget gate missing**; #478 and #479 are in draft #489 |
| Every remaining public capability has a stability classification (#482) | **open** | no classification exists; `Unshipped.txt` lists 7 unshipped types |
| Modern and legacy supported subsets documented | partial | [49](./49-LEGACY-PARITY-490.md) documents the target, not today's output; [14](./14-COMPATIBILITY-MATRIX.md) and 47 §5.4 still describe the superseded subset |
| Arrow ingestion status reflects what shipped | open | the Arrow method is in no review diff (2.5) |
| Nested-support scope matches the tested backend matrix | met | [42](./42-NESTED-BACKEND-SCOPE-176.md); legacy flat-only |
| README examples use only the stable surface | **fails** | `README.md` lines 139, 164, 172, 181, 212, 219 and `PACKAGE_README.md` lines 65 to 77 use `ToListAsync`, `Batches()`, `UserEventColumnarBatch` |

### 4.2 Freeze and ledger process

[18](./18-API-CHANGE-CONTRACT.md) and `docs/api/LEDGER.md` govern two surfaces by build error
(`RS0016`, `PARQAPI002`) and review the third by derived diff. The ledger has 22 entries; entries
dated before 2026-09-10 are one retrospective "inherited" entry, so everything after is reviewed.
The process works. Three gaps:

1. **The emitted surface has no gate.** `PARQAPI001` was retired when baselines stopped being
   committed. The only control is a reviewer reading a sticky comment. Nothing fails if the
   emitted API grows (#459 budget).
2. **Arrow is outside the review** (2.5).
3. **`Unshipped.txt` holds 79 entries.** At `0.1` they all become shipped. Any classification
   must happen before that move, because `Shipped.txt` entries are `breaking-major` to touch.

### 4.3 Pending breaking changes

| Item | State | Surface effect |
|:---|:---|:---|
| #489 (#478, #479) | draft, owner accepted semantics | removes 4 source types, `ToListAsync`; adds the reader |
| #576 (#507, #508, #550) | draft, stacked on #489; benchmark (`ColumnarHandoffBenchmark`) not run | `Batches` to `AsBatches`, one `<Model>Batch`, layout A, lease |
| #512 | open, needs owner decision | `WriteParquetBatchedAsync` |
| #510 | open, feature | `Where(..).AsBatches()` pruning would loosen a throw (non-breaking) |
| #509 | open, feature | projections as base classes; no new API |
| #494, #495 | open | legacy reads, pruning, parallel, columnar, async writes; renames the legacy surface (breaking for legacy users) |
| #496 | open | one package; `.Legacy` becomes thin or deprecated |
| #476 | open PR, not draft | adds attributes, a package, PARQ016-PARQ019 (PARQ016 collides) |
| #482 | open | classification; may remove or hide types |
| #490 | open | tracker for legacy parity |

Open issues labelled `api-surface`: 15 (#550, #512, #510, #509, #508, #507, #505, #495, #494, #490,
#482, #479, #478, #477, #459).

### 4.4 Stability classification

[47](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md) §5.3 calls for stable / optional / preview. The owner
has decided no preview tier is needed, so every "preview" row in the appendix resolves before
baseline to either *stable* (accept the burden) or *internal* (cut). The appendix lists the
proposed outcome and the reason.

### 4.5 Verdict and ordered changes

**Ready after the 10 changes below.** The attribute and options surface needs no change beyond
classification. The risk is the generated surface and its process, not the package.

1. **Merge #489, then #576** (retarget, rebase, run `ColumnarHandoffBenchmark`). Reaches 37, 39 and
   34 members on the flat and sorted goldens and removes the dual batch types.
2. **Settle batch ownership (#369) as a gate decision.** [47](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md)
   §5.1 makes `Batches()` stable. A lease that throws on a kept batch is a mechanical safety, but a
   copied `ReadOnlyMemory<T>` is still unprotected. Either accept that residue in the contract
   text, or make `AsBatches()` internal until an owned model lands.
3. **Make the sorted-key reads and `ParquetPruneStatistics` internal**, or fold them into the
   reader. They are the last `List<T>` and stream-only reads and duplicate `Where`.
4. **Hide `Schema`** (field of a backend type) or replace it with an interface-returning member.
5. **Collapse feature levels.** `Level3ModernCSharp` has no effect and the default is named
   "Preview". Remove the enum and attribute, keep one MSBuild switch. Free to do: unshipped.
6. **Decide #512.** Recommend folding `WriteParquetBatchedAsync` into `WriteParquetAsync`
   (option 1), so no write member uses "batch" for a row chunk.
7. **Put the Arrow surface under review.** Add an Arrow-referencing golden model to
   `scripts/DerivedOutputs.cs` so the public overload shows in the diff, then decide: internal, or
   documented stable that takes `ParquetWriter`.
8. **Add the #459 budget gate.** Proposed budgets from the post-merge numbers: flat or sorted
   model at most 40 members; compound at most 20; generated parameters at most 36 for flat. Fail on
   increase without a ledger entry.
9. **Fix the diagnostics record.** Add PARQ012 to PARQ014 to [13](./13-COMPILER-DIAGNOSTICS.md),
   delete the `PARQ001 - PARQ099` range in 01 and 03, add `AnalyzerReleases.Shipped.md`, and settle
   the PARQ016 collision with #476.
10. **Bring the docs and README to the surface.** README and PACKAGE_README to `ToArrayAsync`,
    `AsBatches`, `<Model>Batch`; reconcile 14 and 47 §5.4 with 49; then move `Unshipped.txt` to
    `Shipped.txt` as the explicit baseline step.

**Legacy is the decision outside this list.** 49 sets parity as the target and 47 §8 only asks that
supported subsets be documented, so legacy is not a precondition. If `0.1` ships with legacy as it
is, #494 and #495 will break legacy users (rename, new entry point) within `0.x`, which the pre-1.0
stance allows. State that in the release notes; do not describe legacy as stable.

**What to cut or rename if only a few changes happen:** cut 3, 4 and 5 (they delete public API
that nothing needs); rename `WriteParquetBatchedAsync` (6). Everything else is process.

---

## Appendix: surface table

Tier: **stable** (in the 0.1 contract), **preview** (resolve to stable or internal before baseline,
since no preview tier exists), **internal** (stop being public). The Entry column names the member
or type; `<M>` is the model name.

### A. Package: `Parquet.SourceGenerator.Attributes`

| Type or member | Tier | Rationale |
|:---|:---:|:---|
| `ParquetSerializableAttribute` | stable | the one required annotation |
| `ParquetColumnAttribute` `Name`, `Order` | stable | shipped, user intent |
| `ParquetColumnAttribute` `Encoding`, `Deduplicate` | stable | user intent; document precedence against the options |
| `ParquetIgnoreAttribute` | stable | shipped, user intent |
| `ParquetDecimalAttribute` | stable | shipped |
| `ParquetTimestampAttribute`, `ParquetTimestampUnit` | stable | shipped; the only way to pick a timestamp unit |
| `ParquetSortKeyAttribute` | preview | pairs with the sorted-key family; stable only if that family survives (trim 3) |
| `ParquetSerializerOptions` write members (7) | stable | `RowGroupSize`, compression, encoding, dictionary |
| `ParquetSerializerOptions.MaxDegreeOfParallelism` | stable | the single home for parallelism |
| `ParquetSerializerOptions` `Max*` limits (7) | stable | safety boundary, [47](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md) §5.2 |
| `ParquetCompressionMethod`, `ParquetCompressionLevel`, `ParquetColumnEncoding` | stable | required by the options |
| `ParquetGeneratorFeatureLevel`, `ParquetGeneratorOptionsAttribute` | internal | one effective switch; keep the MSBuild property |
| `ParquetColumnStatistics<T>` query surface (`Min`, `Max`, `NullCount`, `DistinctCount`, `HasMinMax`, `IsKnownNonNull`, 7 `MayContain*`) | stable | row-group filtering needs it |
| `ParquetColumnStatistics<T>` ctor, `Equals`, `==`, `!=` | internal | plumbing for generated code, or `EditorBrowsable(Never)` |
| `ParquetColumnStatistics.FromRaw<T>` | internal | called only by emitted metadata |
| `ParquetPruneStatistics` | internal | instrumentation; superseded per doc 20 |

### B. Emitted per model (modern)

| Member | Tier | Rationale |
|:---|:---:|:---|
| `<M>Parquet.From(Stream)`, `From(ReadOnlyMemory<byte>)` | stable | the read entry |
| `<M>ParquetReader` (struct) | stable | single reader type, 47 §4.2 |
| `.WithOptions`, `.Where`, `.Parallel` | stable | reader state; throw combinations are the contract |
| `.ToArrayAsync`, `.AsAsyncEnumerable` | stable | the two row terminals |
| `.AsBatches()` | preview | stable by #490 decision, but #369 residue; resolve per change 2 |
| `<M>Batch` (ctor, lanes, `RowCount`, `Fill<C>Nullable`) | preview | write input is user intent; the definition-levels representation and borrowed lifetime are open |
| `<M>RowGroupMetadata` (read-only) | stable | the predicate's argument; ctor internal since #459 |
| `WriteParquetAsync(IReadOnlyCollection<T>)` | stable | core write |
| `WriteParquetAsync(IAsyncEnumerable<T>)` | stable | core write |
| `WriteParquetAsync(<M>Batch)` | stable | columnar write |
| `WriteParquetBatchedAsync(IEnumerable<T>)` | internal | fold into `WriteParquetAsync` (#512) |
| `Schema` | internal | exposes a backend type |
| `ReadParquetBy{Key}Async`, `ReadParquet{Key}RangeAsync` | internal | duplicates `Where`; stream-only; `List<T>` |
| `WriteParquetRowGroupAsync(ParquetWriter, RecordBatch, ...)` (Arrow) | preview | resolve per change 7 |

### C. Emitted for the legacy backend

| Member | Tier | Rationale |
|:---|:---:|:---|
| `ReadParquetAsync`, `ReadParquetArrayAsync` (flat) | preview | temporary until #494; removed with it |
| `WriteParquetAsync(IReadOnlyList<T>)` | preview | align to `IReadOnlyCollection<T>` with modern |
| `WriteParquetBatchedAsync` | internal | same as modern |
| `Schema` | internal | same as modern |
| `<M>ParquetLegacyExtensions` (type name) | preview | name changes under #494/#496 |

### D. Diagnostics and switches

| Item | Tier | Rationale |
|:---|:---:|:---|
| PARQ001 to PARQ011, PARQ016 | stable | users `NoWarn` them; add `AnalyzerReleases.Shipped.md` |
| PARQ012 to PARQ014 | stable | document in 13 first |
| PARQ015 | internal | disappears with the feature-level enum |
| `<ParquetGeneratorFeatureLevel>` MSBuild property | preview | keep one switch; rename off "Preview" |
| `PARQAPI002` / seams.txt | internal | repository gate, not consumer-facing |
