# Flat-read removal before 0.1 (#480)

> **Status:** decided and implemented. **Supersedes** [document 41](41-FLAT-READ-FREEZE-SCOPE-262.md)
> (#262, closed by PR #344). Document 41 is left unedited as the historical record of the earlier
> decision; this document is the one in force.

## Decision

The modern emitter (`Parquet.SourceGenerator`) no longer emits the flat read methods. The generated
builder, `<Model>Parquet.From(...)`, is the **only** public read surface of the modern backend.

Removed from every modern model, in both the `Stream` and the `ReadOnlyMemory<byte>` overload:

| Removed member | Builder equivalent |
|:---|:---|
| `ReadParquetAsync` | `.ToListAsync()` |
| `ReadParquetArrayAsync` | `.ToArrayAsync()` |
| `ReadParquetStreamAsync` | `.AsAsyncEnumerable()` |
| `ReadParquetBatchesAsync` | `.Batches()` |
| `ReadParquetParallelAsync` | `.Parallel().ToListAsync()` (buffer source only) |
| `ReadParquetParallelArrayAsync` | `.Parallel().ToArrayAsync()` (buffer source only) |
| the `predicate` parameter on four of them | `.Where(predicate)` |

The legacy emitter (`Parquet.SourceGenerator.Legacy`) is **not** changed. It has no builder, and under
the declared-subset policy of #246 ([document 14](14-COMPATIBILITY-MATRIX.md#backend-api-policy-for-01))
its core surface is schema, flat read (`ReadParquetAsync`, `ReadParquetArrayAsync`), flat write,
batched write and row-group write. `BackendCompatibilityPolicyTests` pins both halves: the classic
baselines must still contain the flat reads, and the modern baselines must contain none
(`ModernBaselinesExposeNoFlatReadMethods`).

There is **no `[Obsolete]` release** first. Document 41 argued an obsolete release is only useful when
callers have a release in which to move. `0.0.x` has not been published to consumers who would need
one, so an obsolete window would warn nobody and would put the widest surface into the release
history for no benefit.

## Why document 41 no longer holds

Document 41 kept the flat methods through `0.1.0` so the first frozen release would not be the first
real-world migration point, and so reads and writes would not become asymmetric.

The 0.1 direction in #477 changed that premise. `0.1.0` is a contract-narrowing milestone: breaking
changes that remove a duplicated concept are acceptable *before* it, and keeping two complete read
surfaces into it would make the first stable contract the widest one the project will ever have.
Removing a member after `0.1.0` costs a major version; removing it now costs a changelog entry.

Document 41's removal gate is answered as follows:

| Gate in document 41 | Answer |
|:---|:---|
| #244 must prove parameter-level shrinkage | Measured below from the `.api.shape.txt` baselines: 222 fewer parameter slots across the six modern golden models. |
| #219 must settle write symmetry or its explicit deferral | Explicitly deferred. The write side keeps its collection-based entry points; a symmetric write builder remains post-freeze work under #219. The asymmetry is accepted: writes are discovered from the collection (`items.WriteParquetAsync`), reads from the builder. |
| The breaking surface must be recorded in the ledger | Six `breaking-major` entries dated 2026-09-22 in [`docs/api/LEDGER.md`](api/LEDGER.md). |
| Publish migration guidance | The mapping table in `CHANGELOG.md` (Unreleased, *Removed*), mirrored above. |
| The classic emitter keeps its flat methods until a legacy replacement exists | Kept, as above. |

## What happened to the implementations

The contract narrowed; the implementation breadth did not.

- The builder terminals delegated to the flat methods, so the method bodies stay. They are now
  `internal` and renamed: `ReadListCoreAsync`, `ReadArrayCoreAsync`, `ReadEnumerableCoreAsync`,
  `ReadBatchesCoreAsync`, `ReadParallelListCoreAsync` and `ReadParallelArrayCoreAsync`. The generated
  code is compiled into the consumer's own assembly, so `internal` is reachable from the consumer's
  code by construction — exactly as the builder structs' `internal` constructors already were. Per
  [document 17](17-GENERATED-API-BASELINES.md#the-grammar) rule 5 neither is part of the emitted
  contract, and neither appears in any `.api.txt`. The rename is what makes the removal real for a
  caller: a call to `OrderEventParquetExtensions.ReadParquetAsync(stream)` is a compile error, not a
  call that silently keeps working against an off-contract member.
- The two **`Stream` "parallel" overloads are deleted outright**, body included. They read row groups
  sequentially (a single `ParquetReader` seeks within its stream; defect 5 in
  [document 19](19-PUBLIC-API-SURFACE.md)), nothing in the builder delegated to them, and the builder
  already expresses the same fact as the absence of `Parallel()` on the stream source. Their only
  distinguishing behaviour — materialising into a pre-sized array — is what the stream
  `ToArrayAsync()` path already does.
- `.Batches()` is unchanged. Ownership of the batch read shape sits with #369; this change only stops
  exposing the flat `ReadParquetBatchesAsync` that `.Batches()` forwarded to.
- The sorted-key lookups (`ReadParquetBy{Key}Async`, `ReadParquet{Key}RangeAsync`, #151) are not flat
  read-grid cells and are outside #480's scope; they are unchanged.

## Measured shrinkage

From the `*.api.shape.txt` baselines, then checked in under `test/Parquet.SourceGenerator.Tests/GoldenFiles/`
([document 19](19-PUBLIC-API-SURFACE.md) decision D5):

| Golden model | Members before | Members after | Δ | Parameters before | Parameters after | Δ |
|:---|---:|---:|---:|---:|---:|---:|
| `OrderEventParquetExtensions` | 82 | 70 | −12 | 104 | 64 | −40 |
| `ScalarMetricParquetExtensions` | 80 | 68 | −12 | 103 | 63 | −40 |
| `SortedShipmentParquetExtensions` | 74 | 62 | −12 | 119 | 79 | −40 |
| `NestedOrderParquetExtensions` | 47 | 37 | −10 | 72 | 38 | −34 |
| `ListOrderParquetExtensions` | 47 | 37 | −10 | 72 | 38 | −34 |
| `PocoOrderParquetExtensions` | 47 | 37 | −10 | 72 | 38 | −34 |
| **Modern total** | **377** | **311** | **−66** | **542** | **320** | **−222** |
| `LegacyRecordParquetLegacyExtensions` (legacy, unchanged) | 7 | 7 | 0 | 17 | 17 | 0 |

Flat models lose twelve members (six methods × two sources); compound models lose ten, because they
never had the columnar-batch pair. The parameter reduction is larger than the member reduction in
proportion (−41% against −18%) because every removed member carried an `options`, a
`cancellationToken` and, on four cells, a `predicate` that the builder expresses once, as a member.

## Migration

Every flat call maps 1:1 onto a builder chain. The builder rejects a `null` `WithOptions(...)`
argument, so a call that passed `options: null` (or relied on the default) simply omits it.

```csharp
// Before                                                  // After
await OrderEventParquetExtensions.ReadParquetAsync(s);     await OrderEventParquet.From(s).ToListAsync();
await OrderEventParquetExtensions.ReadParquetArrayAsync(   await OrderEventParquet.From(s)
    s, options, ct);                                           .WithOptions(options).ToArrayAsync(ct);
OrderEventParquetExtensions.ReadParquetStreamAsync(        OrderEventParquet.From(bytes)
    bytes, predicate: p);                                      .Where(p).AsAsyncEnumerable();
await OrderEventParquetExtensions                          await OrderEventParquet.From(bytes)
    .ReadParquetParallelAsync(bytes, options);                 .WithOptions(options).Parallel().ToListAsync();
```

`ReadParquetParallelAsync(Stream)` / `ReadParquetParallelArrayAsync(Stream)` have no parallel
equivalent because they were never parallel: use `From(stream).ToListAsync()` /
`ToArrayAsync()` for the same (sequential) behaviour, or buffer the file and use
`From(bytes).Parallel()` for genuine decode parallelism.

In this repository every call site in the tests, benchmarks, samples, the AOT test and the package
consumption projects was migrated by those rules. Where a test used the stream "parallel" overload to
exercise *the parallel reader*, it now buffers and calls `From(bytes).Parallel()`, which exercises the
reader that actually runs concurrently. Two tests existed only for the deleted stream overloads — the
stream half of `ParallelToArrayAsyncReturnsNativeArray` and a null-stream check on the stream
parallel overload — and were removed; the null-stream check for `From(Stream)` remains. The one
benchmark that measured the deleted stream "parallel" path (`SourceGeneratorReadParallelAsync`) was
removed; the stream array path it duplicated is still measured by `SourceGeneratorReadArrayAsync`.
