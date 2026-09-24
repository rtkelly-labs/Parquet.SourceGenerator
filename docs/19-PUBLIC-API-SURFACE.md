# 19 - Public API Surface & Naming Grammar

> **Status**: decision record for issue #216, part of the `0.1.0` API freeze (#230).
> The surface described here is the one emitted by `Parquet.SourceGenerator` for a flat model;
> it is reproduced verbatim in `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.api.txt`.
> The read builder is the only modern read surface (#480, [document 48](48-FLAT-READ-REMOVAL-480.md));
> a symmetric write builder remains a post-freeze proposal tracked by #219, and the current write
> entry points remain in the compatibility window. The grid and defects below describe the flat read
> methods as they were when this audit was written; they are the reason those methods were removed.

## Why this document exists

The generated surface grew by accretion. Each feature was reasonable on its own and attached
itself to the existing methods in a slightly different way, and because the surface is emitted
into the consumer's compilation, nothing in the repository showed the cumulative result. Golden
files carry full method bodies, so an API addition and a codegen tweak produce diffs of the same
shape; no .NET API-diff tool can see generated code at all.

Issue #215 closed that gap with signature-only baselines. This document is what the first one
showed.

---

## The grid

Reads are a cross-product of four axes. Only the first three are expressed in method names; the
fourth is expressed as a parameter, on some cells and not others.

| Axis | Values | Expressed as |
|:---|:---|:---|
| Source | `Stream`, `ReadOnlyMemory<byte>` | overload |
| Shape | `List<T>`, `T[]`, `IAsyncEnumerable<T>`, `ColumnBatch` | method name |
| Execution | sequential, parallel | method name |
| Pushdown | none, `predicate` | optional parameter |

2 x 4 x 2 = 16 cells. Twelve exist:

| Shape | Stream, sequential | Stream, parallel | Memory, sequential | Memory, parallel |
|:---|:---|:---|:---|:---|
| `List<T>` | `ReadParquetAsync` **+p** | `ReadParquetParallelAsync` | `ReadParquetAsync` | `ReadParquetParallelAsync` |
| `T[]` | `ReadParquetArrayAsync` **+p** | `ReadParquetParallelArrayAsync` | `ReadParquetArrayAsync` | `ReadParquetParallelArrayAsync` |
| `IAsyncEnumerable<T>` | `ReadParquetStreamAsync` **+p** | — | `ReadParquetStreamAsync` **+p** | — |
| `ColumnBatch` | `ReadParquetBatchesAsync` | — | `ReadParquetBatchesAsync` | — |

**+p** marks a cell that accepts `predicate`. Four of twelve do.

Writes add a fifth shape axis of their own — `IReadOnlyCollection<T>`, `IEnumerable<T>`,
`IAsyncEnumerable<T>`, `{T}ColumnarBatch`, and a raw per-column form — across
`WriteParquetAsync`, `WriteParquetBatchedAsync`, `WriteParquetRowGroupAsync` and
`WriteParquetRowGroupColumnarAsync`. Since #481 the two `ParquetWriter`-taking row-group writers
(and the `AsColumnarText` / `AsColumnarBinary` helpers) are `internal`: they describe an
implementation strategy, and `WriteParquetAsync` over a collection, an `IAsyncEnumerable<T>` or a
`{T}ColumnarBatch` expresses the same intents publicly. Defect 8 below therefore no longer
reaches consumers of a model assembly.

With #146 (prefetch), #148 (file path / MMF) and #178 (Arrow export) still open, the read grid is
on track for 3 x 5 x 3 = 45 cells before feature flags (#225) multiply it again.

---

## Defects

These are stated as defects rather than history. None of them was a mistake at the time; all of
them are consequences of naming a cross-product.

### 1. "Stream" denotes two different things

In `ReadParquetStreamAsync` the word is the *shape* — `IAsyncEnumerable<T>`. But the same method
takes a `Stream` *source*, and the sibling overload reads:

```csharp
ReadParquetStreamAsync(ReadOnlyMemory<byte> parquetBytes, ...)
```

which is a contradiction on its face.

### 2. The naming grammar is inconsistent across axes

Sequential marks shape as a **suffix** and omits execution. Parallel marks execution as an
**infix** and shape as a suffix. `List<T>` is the unmarked default for no documented reason:

```
ReadParquetAsync              -> sequential, List<T>
ReadParquetArrayAsync         -> sequential, T[]
ReadParquetParallelAsync      -> parallel,   List<T>
ReadParquetParallelArrayAsync -> parallel,   T[]
```

There is no rule from which a reader could predict the name of the parallel `IAsyncEnumerable`
cell — which is just as well, because it does not exist, and nothing says why.

### 3. Pushdown is populated arbitrarily

`predicate` is on `Stream`+`List`, `Stream`+`T[]`, `Stream`+`IAsyncEnumerable` and
`Memory`+`IAsyncEnumerable`. It is absent from `Memory`+`List` and `Memory`+`T[]`, and from every
parallel cell. No rule explains that distribution — a caller who buffers a file and wants both
pruning and a `List<T>` finds the combination missing for no reason they can see.

### 4. `predicate` sits after `cancellationToken`

```csharp
ReadParquetAsync(Stream stream,
                 ParquetSerializerOptions? options = null,
                 CancellationToken cancellationToken = default,
                 Func<{T}RowGroupMetadata, bool>? predicate = null)
```

A cancellation token conventionally comes last. This ordering is what you get from appending a
parameter to a settled signature, and it is now public.

### 5. `Parallel` over a `Stream` is sequential, and ignores its own argument

`ReadParquetParallelAsync(Stream, ...)` reads row groups **sequentially**.
A single `ParquetReader` seeks within its stream, so concurrent row-group reads would corrupt one
another. The generated XML documentation says so honestly — but the method is still named
`Parallel`. Until #239 it also accepted a `maxDegreeOfParallelism` argument and discarded it;
that argument is gone, but the name still promises concurrency the method cannot deliver, and
`ParquetSerializerOptions.MaxDegreeOfParallelism` is still read on a path that ignores it.

A name that promises what the method cannot deliver, plus a knob it ignores, is the single
clearest argument in this document for expressing execution as a member rather than as a name.

### 6. Reads and writes are discovered differently

Writes are extension methods on the collection, so IntelliSense offers them from `items.`. Reads
are statics on `{T}ParquetExtensions` — a generated type name the caller must already know, on a
class named `...Extensions` that mostly does not contain extension methods.

### 7. Two batch types, differently shaped and differently named

| | Read side (#208) | Write side (#212) |
|:---|:---|:---|
| Name | `ColumnBatch` | `{T}ColumnarBatch` |
| Columns | `ReadOnlySpan<T>` properties | `ReadOnlyMemory<T>` fields |
| Nulls | encoded in the column type | separate `…DefinitionLevels` arrays |

One is model-prefixed, one is not. Two names, two memory abstractions and two null strategies for
what a user reads as one concept.

### 8. The raw columnar write form is positional, per column

`WriteParquetRowGroupColumnarAsync` takes one parameter per column, in schema order — eleven
parameters for a nine-property model, since nullable columns contribute a definition-level array.
Adding a property to the model silently changes the meaning of every argument after it.

---

## Decisions

### D1 — Options versus parameters *(settled by #239, with one amendment open)*

- **Options carries what shapes the output file or the codec**: row group size, compression,
  encoding hints. These apply to write paths that take no positional arguments, so options is the
  only place they can live.
- **A parameter carries what selects an execution strategy**, and belongs on the member offering
  that strategy. An option only one member reads is a duplicate of that member's argument.

**What shipped, in #239, is the simpler form of this rule:** `ParquetSerializerOptions` is the
single home for configuration, and *both* duplicated positional parameters were deleted.
`RowGroupSize` and `MaxDegreeOfParallelism` both live in options.

This document originally proposed splitting them — options for what shapes the file, a parameter for
what selects an execution strategy — on the grounds that `MaxDegreeOfParallelism` has exactly one
reader in the entire generator, while `RowGroupSize` is read by `WriteParquetAsync(IReadOnlyCollection)`,
which takes no positional arguments. One rule applied uniformly is easier to teach than two, and that
is what landed.

The split argument is not discarded, only deferred, because it predicts a real collision:
**#217's builder spells parallelism `.Parallel(n)`**, and if the option survives alongside it the knob
has two homes again — the condition #218 existed to remove, arriving from the other direction.
Tracked as **#241**, blocked on #217, to be recorded here as an amendment to D1.

**Amendment to D1 (settled per #241):**
Parallelism configuration settles on **Option B**: `.Parallel()` on the builder takes no degree argument
and reads `ParquetSerializerOptions.MaxDegreeOfParallelism` (configured via `.WithOptions(...)`).
This preserves the universal single-home principle established in #239 and prevents duplicate knobs.


### D2 — Axes become members, not name segments *(#217)*

A generated entry point returning a builder struct:

```csharp
await PersonParquet.From(stream).Parallel(4).ToArrayAsync(ct);
await PersonParquet.From(bytes).Where(m => m.OrderKey.Min >= 1000).ToListAsync(ct);
await foreach (var batch in PersonParquet.From(path).Batches(ct)) { /* ... */ }
```

Three sources + three executions + four shapes + pushdown is **eleven members**, not 45 names, and
a new axis adds members linearly instead of multiplying names.

This resolves defects 1, 2, 3, 4 and 6 structurally rather than by renaming: there is no name to
overload, no infix-versus-suffix question, no cell that can be missing without someone choosing to
omit a member, and no appended parameter. Defect 5 becomes expressible — `.Parallel(4)` over a
source that cannot parallelise is a diagnostic or a documented degradation, not a lie in a name.

Defects 7 and 8 are **not** resolved by the builder and are tracked separately: unifying the batch
types belongs with #220's column catalog, and the positional columnar write form belongs with #219.

### D3 — Fate of the existing flat methods

**Removed from the modern emitter before `0.1.0` (#480).** The builder is the only modern read
surface; every flat `ReadParquet*Async` method and its `predicate` parameter is gone from the emitted
contract, and the migration is a 1:1 mapping onto a builder chain (`CHANGELOG.md`, Unreleased). The
implementations the builder delegates to remain as `internal` `Read*CoreAsync` members outside the
contract; the stream "parallel" overloads, which nothing delegated to, were deleted. No `[Obsolete]`
release preceded the removal, because `0.0.x` has no published consumers to warn. The legacy emitter
has no builder and keeps its flat reads as its declared subset (#246). Measured effect: 66 fewer
emitted members and 222 fewer parameter slots across the six modern golden models.

The full decision, including how document 41's removal gate was answered, is
[document 48](48-FLAT-READ-REMOVAL-480.md). It supersedes
the #262 freeze ([DECISIONS #262](DECISIONS.md#262--flat-read-freeze-superseded)), which kept
the methods through the `0.1.0` window.

### D4 — The decision is kept honest by the baselines

Every claim in this document is checkable against
`test/Parquet.SourceGenerator.Tests/GoldenFiles/*.api.txt`, which CI regenerates and diffs. A
future feature that attaches itself unevenly — as pushdown did — shows up as a baseline diff in
the pull request that does it.

### D5 — Parameter-level shrinkage is measured by callable parameter slots (#244)

The member count is necessary but insufficient: changing a signature removes no `.api.txt` line,
even when it removes a duplicate configuration entry point from the API a caller must understand.
The chosen companion metric is **`PARAMETERS`**, the total number of parameter slots across every
externally reachable method, constructor, operator, conversion, indexer and delegate in one
generated file. Each overload contributes its own slots; types, fields and properties contribute
none. A signature change can therefore leave `MEMBERS` unchanged while changing `PARAMETERS` from,
for example, `2` to `1`.

Each `Name.g.cs` golden file now has a generated `Name.api.shape.txt` companion:

```
# generated API shape summary
MEMBERS=82 PARAMETERS=111
```

`GeneratedApiBaseline.CreateShapeSummary` derives both values from the same emitted source string
used to write and verify `Name.g.cs` and `Name.api.txt`. The summary is deliberately separate from
the signature grammar: it is a measurement of the API shape, not another catalogue of public
members. Ordering and numeric formatting are invariant and the golden test regenerates it under
`UPDATE_GOLDEN_FILES=true`; a hand-edited count fails the normal golden suite.

This is a burden metric, not a claim that every parameter has equal user value. #217's shrinkage
claim is now numeric in the review diff: the relevant `PARAMETERS=` line moves alongside the
`.api.txt` signature diff, while `MEMBERS=` remains available to show whether the change was an
addition, removal or signature consolidation.

---

## A note on this document's own number

`docs/19` was reserved as a stub by #239, explicitly "pending #216, which expands it into the full
surface audit". This is that expansion, and it keeps the stub's rule intact as D1.

Getting here took three numbers: drafted as `16-`, renumbered to `17-` because
`16-VERSION-AND-SCHEMA-EVOLUTION.md` already held it, then to `19-` because #235 and #236 took 17 and
18 while it sat in review. A flat numbered sequence is a shared mutable namespace with no locking —
the concrete case for the Guides / Reference / Internals split in #228.

## Related

- #215 — signature-only baselines (the instrument)
- #217 — the builder (D2)
- #235 / `docs/17` — the signature baselines every claim here is checkable against
- #236 / `docs/18` — the API change contract that now governs additions to this surface
- #218 / #239 — the options rule (D1); #241 is its open amendment
- `docs/20` — unified pushdown, which continues this audit into the read-strategy surface
- #219 — write builder, and defect 8
- #220 — column catalog, and defect 7
- #225 — feature profiles, which multiply whatever surface exists when they land
- #230 — `0.1.0` freeze tracking
