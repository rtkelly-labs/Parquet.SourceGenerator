# 49 - Legacy Backend Parity (#490)

> **Status:** Decision adopted; tracked by
> [#490](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/490).
> **Supersedes** the declared-subset policy (B) from
> [#246](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/246), which
> [14](./14-COMPATIBILITY-MATRIX.md#backend-api-policy-for-01) and
> [47 §5.4](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md#54-legacy-backend) record. Neither #246 nor any
> earlier decision record is edited; they stay as the history of why the subset existed.

## Decision

The legacy backend (Parquet.Net 4.x/5.x, the `DataColumn` API) exposes **the same generated API
as the modern backend**. A member is absent from the legacy surface only when the platform cannot
support it, and every such absence is listed in a checked-in allowlist with a reason and a tracking
issue.

The purpose is a single calling contract. A project that multi-targets `net472;net8.0` references
Parquet.Net 4.25 on `net472` and 6.x on `net8.0`, and compiles **the same source** against both.
Today it cannot, because the two backends emit different type names, different write signatures
and different read surfaces.

## Why the subset policy no longer holds

Policy (B) assumed the gap was set by the platform: that the v4 API could not support the modern
builder, filtering, parallel, streaming or columnar members. Checked against the Parquet.Net
4.25.0 `netstandard2.0` assembly, that assumption is wrong:

| Modern capability | What Parquet.Net 4.25 offers |
|:---|:---|
| `Where` / row-group pruning | `ParquetRowGroupReader.GetStatistics(DataField)` returns `DataColumnStatistics` with `MinValue`, `MaxValue` and `NullCount`. The values are `object`, so the cost is one unbox per column per row group, not per row. |
| `IAsyncEnumerable<T>` streaming, `ReadOnlyMemory<byte>` source | Parquet.Net 4.25 itself references `Microsoft.Bcl.AsyncInterfaces` and `System.Memory`, so a `net472` consumer already has both types. |
| `Parallel()` over a buffer | `ParquetReader.RowGroupCount` plus `OpenRowGroupReader(int)`: one reader per worker over a shared buffer, the same shape as the modern parallel path. |
| `<Model>ColumnarBatch` write | `WriteColumnAsync(new DataColumn(field, array))`, at the cost of one copy per column into the array `DataColumn` requires. |
| `<Model>ParquetReader`, `<Model>RowGroupMetadata`, write entry points | Pure generated C#, with no Parquet.Net dependency beyond the column I/O underneath. |

The one genuine difference is **column I/O**. v4 reads and writes whole `DataColumn` arrays and
allocates them itself; v6 fills and drains caller-owned `Memory<T>` buffers. That is a performance
difference, and it is documented, not hidden. It is not an API difference.

## The rule

1. **One surface.** For every capability both backends support, the legacy backend emits the same
   type names, member names, signatures and semantics as the modern backend. That includes the
   `NotSupportedException` combinations recorded under [47 §4.2](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md#42-generated-read-api).
2. **Differences are listed, not implied.** A member missing from the legacy surface must appear
   in the parity allowlist with a reason and a tracking issue. The allowlist can only shrink: an
   entry that no longer differs fails the gate.
3. **One implementation of the surface.** The reader, metadata, pruning, write entry points and
   validation are emitted once and shared. Only column I/O has a per-backend implementation (#492).
4. **Performance is per backend.** Allocation and throughput differences are recorded in the
   benchmark tables and in [14](./14-COMPATIBILITY-MATRIX.md). They are not gated as parity
   failures.

## Initial allowlist

| Capability | Why the legacy backend lacks it today | Tracking |
|:---|:---|:---|
| Nested types (structs, lists, maps) | The legacy emitter is flat-only. Parquet.Net 4.x can represent repeated and group columns, so this is emitter work, not a platform limit. | #176, [42](./42-NESTED-BACKEND-SCOPE-176.md) |
| `Batches()` / `ColumnBatch` | The ownership shape is undecided on the modern backend too. Porting it before the decision would freeze it twice. | #369 |
| Arrow `RecordBatch` bridge | Not yet built for v4. Apache.Arrow supports `netstandard2.0`, so it is possible. | #490 |

Everything else in the modern surface is in scope for parity: the reader and its options, buffer
and stream sources, `ToArrayAsync`, `AsAsyncEnumerable`, `Where` and row-group metadata,
`Parallel()`, `<Model>ColumnarBatch`, and the `IReadOnlyCollection<T>` / `IAsyncEnumerable<T>` /
batched writes.

## Enforcement

- **API parity gate (#493).** The same model is generated through both backends. After normalising
  the extension class name, the `.api.txt` lines are diffed, and any difference not in the
  allowlist fails.
- **Shared behavioural suite (#493).** The round-trip, reader and pruning tests run against both
  backends from one test source.
- **Multi-target consumer (#493).** One test project with `TargetFrameworks` of `net472;net8.0`
  builds the same calling source against both backends.

`BackendCompatibilityPolicyTests` currently asserts the opposite: that no modern-only member
appears in a legacy baseline. It is retired or inverted in the same PR that introduces the parity
gate. Until then it continues to describe the current state accurately.

## End state: one package

The generator selects the column-I/O backend from the Parquet.Net version the compilation
references: 6.x uses the V6 buffer API, 4.x/5.x uses the V4 `DataColumn` API. There is one
analyzer assembly and one package reference (#496). The conditional Arrow bridge, which is emitted
only when the compilation references Apache.Arrow, is the precedent for reference-driven emission.

`Parquet.SourceGenerator.Legacy` becomes either a thin compatibility package or deprecated. #496
records which, with a migration note.

## Sequencing

1. This record (#491).
2. Split the generated surface from per-backend column I/O (#492). Modern goldens stay unchanged.
3. Parity gate and multi-target consumer (#493), with today's gap as allowlist entries.
4. Legacy surface alignment, reader, pruning and parallel (#494), then columnar and
   `IAsyncEnumerable` writes (#495). Each step shrinks the allowlist.
5. One package (#496).

Steps 2 onward start from the final modern read surface ([#478/#479](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/478)),
so the legacy backend ports it once.
