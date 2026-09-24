# 07 - Known Limitations

What the generator does **not** do today, and the closed audit that shaped it. Unlike the design
documents, this one records observed behaviour. Dependency-side gaps (things only an upstream release
can fix) live in [`UPSTREAM_DEPENDENCY_LIMITATIONS.md`](../UPSTREAM_DEPENDENCY_LIMITATIONS.md);
deferred features and their gates live in [DECISIONS](./DECISIONS.md).

## 1. Current limitations

| Area | Limitation |
|:---|:---|
| Positional records, get-only members | Not materialised: no constructor-based materialisation. Rejected with `PARQ007`/`PARQ008`. |
| Nested (containing) and generic types | Rejected with `PARQ009`/`PARQ010`. A generic schema cannot be one `static readonly` field. |
| Compound shapes | Modern backend: tested struct/list shapes only. Classic backend: flat only. Maps unsupported. See [DECISIONS #176](./DECISIONS.md#176--nested-types-by-backend). |
| Unrepresentable types | `char`, `DateTimeOffset`, arrays other than `byte[]`, `BigInteger`, arbitrary collections → `PARQ006`. Store `DateTimeOffset` as a `DateTime` plus an offset column. |
| Parallel read | Only the `ReadOnlyMemory<byte>` source reads row groups in parallel. A `Stream` source is sequential: one reader cannot be shared across workers. |
| Classic backend (`.V5`, Parquet.Net 4.x/5.x) | Write, batched write and read only — no streaming or parallel reader. `DataColumn` allocates its own arrays, so the main package's allocation figures do not apply. `ReadOnlyMemory<byte>`, `ReadOnlyMemory<char>` and `BigDecimal` → `PARQ011`. |
| Classic `MaxStringLengthBytes` | Validated *after* `ReadColumnAsync` returns (4.25 has no bounded string read), so it is a deterministic limit, not a guarantee the oversized string was never materialised. |
| net472 consumers | Need `<LangVersion>latest</LangVersion>` (net472 defaults to 7.3). CI compiles net472 but does not execute it. IronCompress ships no `win-x86` binary, so 32-bit .NET Framework fails on compressed writes. |
| Native AOT | CI publishes and runs the AOT binary on `linux-x64` only; the AOT matrix covers flat models. Parquet.Net itself still emits trim/AOT warnings — see [10](./10-NATIVE-AOT-GUIDE.md). |
| Nullability behaviour | Under `#nullable enable`, `string` is a *required* column: writing a null throws from Parquet.Net. Declare `string?` for an optional column. |

## 2. Closed audit

The original audit (2026-09) found the items below by reading `TargetParser`, `CodeEmitter`, the
emitted code and the published Parquet.Net packages. All are closed; the full write-up of each is in
this file's git history.

### Parquet.Net versions and .NET Framework

| # | Finding | Resolution |
|:---|:---|:---|
| 1.1 | A Parquet.Net 5.x variant would not restore net472 — only 4.25.0 and earlier ship `netstandard2.0`. | Classic backend targets 4.25.0. |
| 1.2 | The API break is v5→v6 (`DataColumn` → `Memory<T>`), not v4→v5, so a classic backend is a second emitter, not a retarget. | Shipped as `Parquet.SourceGenerator.V5` (covers v4 and v5) with its own emitter over the shared parser. |
| 1.3 | net472 needs polyfills, IronCompress lacks `win-x86`, and net472 defaults to C# 7.3. | Classic emitter avoids `IAsyncEnumerable`; `win-x86` and `LangVersion` recorded as known gaps (§1). |
| 1.4 | `IsExternalInit` polyfill guarded on netstandard2.0 only. | Guard covers netstandard2.1 too. |

### Correctness

| # | Finding | Resolution |
|:---|:---|:---|
| 2.1 | `ReadParquetParallelAsync` ran sequentially; its parallelism knob was inert. | Real parallelism on the buffer source: one reader and stream per worker, row groups claimed via `Interlocked` cursor, results written to disjoint ranges, buffer normalised to an array once. Tests assert order and equivalence, not speed-up. |
| 2.2 | `results.Capacity` reassigned per row group, reallocating O(groups × rows). | Removed. |
| 2.3 | Positional records emitted uncompilable code (CS7036). | `PARQ008`. |
| 2.4 | Get-only properties/readonly fields emitted CS0200. | `PARQ007`. |
| 2.5 | Inherited members silently dropped. | Base types walked, stopping at the first non-source base; overrides keep the base's column position. |
| 2.6 | Nested and generic types emitted unresolvable names and colliding hint names. | `PARQ009`, `PARQ010`. |
| 2.7 | `DateTimeOffset` mapped to `DateTime` and lost its offset. | Mapping removed → `PARQ006`. |
| 2.8 | Unsupported types failed at runtime. | `PARQ006`, allowlist mirrored from Parquet.Net's `SupportedTypes` so it can never reject a working build. `DateOnly` later gained an explicit mapping. |
| 2.9 | Every reference-type column was forced optional. | Nullable annotations are authoritative (behaviour change — see §1). |

### Options and API surface

| # | Finding | Resolution |
|:---|:---|:---|
| 3.1 | `ParquetSerializerOptions` ignored on every read path. | Options passed to every `ParquetReader.CreateAsync`. |
| 3.2 | Row-group size used a `50_000` magic sentinel. | Made nullable, then the parameters were deleted entirely (#218); options are the only source. |
| 3.3 | `ParquetSerializerOptions.Default` was a mutable singleton. | Returns a fresh instance per access (init-only would break netstandard object initialisers). |
| 3.4 | `SchemaName` was dead public API — `ParquetSchema` has no name. | Removed, with unused model state that was needlessly invalidating the incremental cache. |
| 3.5 | Memory read overload leaked a `MemoryStream`; other readers lacked buffer overloads. | Leak fixed; buffer sources for every reader. |
| 3.6 | `[ParquetColumn]` could not set `Order` without a name; named arguments were parsed only alongside a constructor argument. | Parameterless constructor; parser fixed. |
| 3.7 | Compression level not exposed. | Nullable `CompressionLevel`, so unset keeps Parquet.Net's default. |
| 4 | README, CHANGELOG, roadmap and design docs contradicted behaviour. | Reconciled. |

### Classic (v4/v5) backend

| # | Finding | Resolution |
|:---|:---|:---|
| 6.1 | `byte[]` columns emitted `new byte[][count]`. | Rank suffix after the length. |
| 6.2 | Nullable enums built a non-nullable array. | Value-type nullable columns use `T?[]`; reference types use the unannotated type. |
| 6.3 | Compression accepted and discarded (v4 keeps it on the writer). | Set on the writer; `SmallestSize` behind `#if NET6_0_OR_GREATER`, else `Optimal`. |
| 6.4 | Reader created without options. | Fixed, as 3.1. |
| 6.5 | net472 declared but never compiled in CI. | CI compiles it. |
| 6.6 | v6 type allowlist too wide for v4. | `ParquetApiLevel` threaded through the parser; `PARQ011`. |
| 6.7 | Row counts opened every row group; schema resolved per row group. | Metadata row counts; schema resolved once. |
| 6.8 | Hostile files could force huge pre-allocations, deep recursion or level-stream overruns (#287, #288). | `MaxAllocationValues` (10M), `MaxRowGroupCount` (100k), `MaxNestingDepth` (64); `checked` level arithmetic and cursor bounds throwing `InvalidDataException`. |

The benchmark re-run the audit asked for (after 2.1 and 2.2) landed on 2026-09-08 — see
[BENCHMARKS § Re-baseline](./BENCHMARKS.md#-re-baseline-2026-09-08-dev-host). Headline: reads are
decode-bound, not materialisation-bound.
