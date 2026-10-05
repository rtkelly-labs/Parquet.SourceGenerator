# Legacy Backend & .NET Framework Support

`Parquet.SourceGenerator.Legacy` provides compile-time serialization for older targets, specifically **.NET Framework 4.7.2+**, **.NET Standard 2.0**, and **Parquet.Net 4.x/5.x** runtimes.

---

## 1. Overview & Parity Contract

The legacy backend exposes **the same generated API contract as the modern backend**, ensuring that multi-targeting projects (e.g. `net472;net8.0`) can compile the exact same calling code against both engines without `#if` directives. Missing members are restricted to platform-impossible capabilities, documented below.

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
| `IAsyncEnumerable<T>` streaming, `ReadOnlyMemory<byte>` source | The Parquet.Net 4.25.0 `.NETStandard2.0` nuspec group lists neither `Microsoft.Bcl.AsyncInterfaces` nor `System.Memory` (checked on nuget.org); `Microsoft.Bcl.AsyncInterfaces` only arrived transitively through `System.Text.Json` 8.0.4. The Attributes package now declares `Microsoft.Bcl.AsyncInterfaces` for netstandard2.0 itself (#598) and already declared `System.Memory`. |
| `Parallel()` over a buffer | `ParquetReader.RowGroupCount` plus `OpenRowGroupReader(int)`: one reader per worker over a shared buffer, the same shape as the modern parallel path. |
| `<Model>ColumnarBatch` write | `WriteColumnAsync(new DataColumn(field, array))`, at the cost of one copy per column into the array `DataColumn` requires. |
| `<Model>ParquetReader`, `<Model>RowGroupMetadata`, write entry points | Pure generated C#, with no Parquet.Net dependency beyond the column I/O underneath. |

The one genuine difference is **column I/O**. v4 reads and writes whole `DataColumn` arrays and
allocates them itself; v6 fills and drains caller-owned `Memory<T>` buffers. That is a performance
difference, and it is documented, not hidden. It is not an API difference. The limitation and the
upstream change that would remove it are recorded in
[Known Limitations](../reference/known-limitations.md#parquetnet-4250-column-io-allocates-whole-columns).

## The rule

1. **One surface.** For every capability both backends support, the legacy backend emits the same
   type names, member names, signatures and semantics as the modern backend. That includes the
   `NotSupportedException` combinations recorded under [Vision & Design Tenets](../vision.md §3).
2. **Differences are listed, not implied.** A member missing from the legacy surface must appear
   in the parity allowlist with a reason and a tracking issue. Each entry is either **temporary**
   (a gap to close; the allowlist can only shrink, and an entry that no longer differs fails the
   gate) or **permanent** (a recorded owner decision that the capability is modern-only). A new
   permanent entry needs its own decision record or an amendment to this one.
3. **One implementation of the surface.** The reader, metadata, pruning, write entry points and
   validation are emitted once and shared. Only column I/O has a per-backend implementation (#492).
4. **Performance is per backend.** Allocation and throughput differences are recorded in the
   benchmark tables and in [Compatibility Matrix](../reference/compatibility-matrix.md). They are not gated as parity
   failures.

## Initial allowlist

### Permanent (modern-only by decision)

| Capability | Why | Decision |
|:---|:---|:---|
| `<Model>ParquetReader.Batches()` / `ColumnBatch` | Batch reading is part of the **stable modern API** (net8+). It is the zero-POCO read path whose point is caller-visible pooled column buffers. The v4 `DataColumn` API allocates each column itself, so a legacy `Batches()` would have the same shape without the benefit. The legacy backend does not emit it at all. It is absent, not a member that throws, so multi-target callers get a compile-time error and guard it with `#if NET8_0_OR_GREATER` rather than a runtime failure. | Owner decision, 2026-09-24 (#490) |

### Temporary (gaps to close)

| Capability | Why the legacy backend lacks it today | Tracking |
|:---|:---|:---|
| Nested types (structs, lists, maps) | The legacy emitter is flat-only. Parquet.Net 4.x can represent repeated and group columns, so this is emitter work, not a platform limit. | #176, [Nested Types Guide](./nested-types.md) |
| `ReadOnlyMemory<byte>` / `ReadOnlyMemory<char>` members (PARQ011) | The v4 `DataColumn` API has no `ReadOnlyMemory` column representation, so the classic parser rejects these today. They can be mapped onto `byte[]` / `string` columns at one copy per value on write and read. That is emitter work, recorded in [Known Limitations](../reference/known-limitations.md). | #494 |
| Arrow `RecordBatch` bridge | Not yet built for v4. Apache.Arrow supports `netstandard2.0`, so it is possible. | #490 |

### Legacy-only members

The legacy flat `ReadParquet*Async` methods have no modern counterpart: the modern emitter removed
them in favour of the builder (issue #480), and keeps them on the
legacy emitter only until a legacy replacement exists. The legacy builder (#494) is that
replacement, so the flat methods are a **temporary** legacy-only entry: they are carried until #494
lands and are removed with it, not kept as a permanent second read surface.

Everything else in the modern surface is in scope for parity: the reader and its options, buffer
and stream sources, `ToArrayAsync`, `AsAsyncEnumerable`, `Where` and row-group metadata,
`Parallel()`, `<Model>ColumnarBatch`, and the `IReadOnlyCollection<T>` / `IAsyncEnumerable<T>` /
batched writes.

## Enforcement

- **API parity gate (#493).** The same model is generated through both backends. After normalising
  the extension class name, the `.api.txt` lines are diffed, and any difference not in the
  allowlist fails. The allowlist file marks each entry temporary or permanent. Only temporary
  entries fail when they stop differing.
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
