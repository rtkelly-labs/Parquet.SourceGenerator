# Changelog

All notable changes to **Parquet.SourceGenerator** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

Changes since `0.0.3`. Cutting a release is a PR: move this section's content under a new
`## [x.y.z] - YYYY-MM-DD` heading below and leave `[Unreleased]` behind, empty. `release.yml`
reads the version and the release notes from that heading — there is no version input — so
nothing can be published that this changelog does not describe.

> ⚠️ Audit before the next cut: the entries below predate the 0.0.2 and 0.0.3 releases, which
> were tagged before this changelog was reconciled, and may already be covered by them.

### Added
- **Roslyn incremental source generator**: compiles zero-reflection Parquet serializers and
  deserializers against Parquet.Net low-level primitives.
- **Native AOT support**, exercised on every CI run by publishing the AOT test project with
  `-r linux-x64` and executing the resulting native binary. `linux-x64` only, and note that
  Parquet.Net 6.0.3 emits its own trim (`IL2104`) and AOT-analysis (`IL3053`) warnings — so this
  covers the paths the test exercises rather than guaranteeing AOT safety in general.
- **`Guid` columns** written as native 16-byte values via pooled struct buffers rather than
  strings.
- **Parallel reader (`ReadParquetParallelAsync`)**: materialises into a single pre-sized array
  indexed by row-group offset. Over a `ReadOnlyMemory<byte>` it decodes row groups concurrently —
  one `ParquetReader` over one stream per worker, groups claimed dynamically — with results in file
  order. Over an arbitrary `Stream` it stays sequential, because a stream cannot be shared between
  readers, and `maxDegreeOfParallelism` is not honoured there.
- **`Parquet.SourceGenerator.V5`**: a second generator emitting against the Parquet.Net 4.x/5.x
  `DataColumn` API, which is what restores .NET Framework 4.7.2 support. It accepts a narrower set
  of member types than the v6 backend and reports the difference as `PARQ011`.
- **`IAsyncEnumerable<T>` streaming** directly into chunked row groups.
- **Microsecond `Int64` timestamps** via `[ParquetTimestamp(ParquetTimestampUnit.Microseconds)]`.
- **`ParquetSerializerOptions`** for `RowGroupSize`, `CompressionMethod` (`None`, `Snappy`, `Gzip`,
  `Lz4`, `Brotli`, `Zstd`) and `CompressionLevel` (`Optimal`, `Fastest`, `NoCompression`,
  `SmallestSize`; unset keeps Parquet.Net's default). `MaxDegreeOfParallelism` supplies the worker
  count for the buffer-based parallel read.
- **Compiler diagnostics `PARQ001`–`PARQ011`**: partial-type enforcement, duplicate column names,
  no serializable members, ignored non-public members, invalid decimal precision/scale, unsupported
  member types, unassignable members, types with no parameterless constructor, nested or generic
  target types, and member types the 4.x/5.x backend cannot represent.
- **CI workflow** building, testing and packing the solution. Benchmarks run on demand.

### Changed
- **Formatting tooling consolidated on CSharpier.** `dotnet format whitespace` is removed from CI:
  its Roslyn formatter disagrees with CSharpier on layout (case-body and pattern-arm indentation),
  and it policed nothing beyond `.cs` files anyway. `.editorconfig` now carries CSharpier's
  configuration (`max_line_length`, its non-configurable behaviors) and disables `IDE0055`, so no
  Roslyn-based formatter — build, IDE or CLI — competes with CSharpier over C# layout. `.gitattributes`
  pins `eol=lf` on checkout, replacing the whitespace formatter's line-ending role for non-C# files.

### Fixed before release
- `CompressionMethod` was accepted and discarded — no compression setting ever reached the writer.
- `[ParquetTimestamp(Microseconds)]` mapped to `DateTimeFormat.DateAndTime`, which Parquet.Net
  defines as *millisecond* precision, so microsecond columns were silently written coarser and the
  sub-millisecond component was lost. Now maps to `DateAndTimeMicros`.
- `ParquetTimestampUnit.Nanoseconds` and `ParquetSerializerOptions.UseMicrosecondTimestamps` are
  removed. Neither could work: Parquet.Net has no nanosecond format, and the schema is emitted at
  compile time so no runtime flag can change a column's encoding.

### Known gaps
- Nested collections, `DateTimeOffset` and positional records are unsupported. They are now
  rejected at compile time (`PARQ006`/`PARQ008`) rather than failing at runtime.
- `ReadParquetParallelAsync(Stream)` reads row groups sequentially and ignores
  `maxDegreeOfParallelism`; pass a `ReadOnlyMemory<byte>` for the parallel path.
- Nested and generic target types are rejected (`PARQ009`/`PARQ010`) rather than supported.
- .NET Framework needs the `Parquet.SourceGenerator.V5` package. The classic backend has no
  `ArrayPool` story — `DataColumn` allocates its own arrays — so it does not inherit the main
  package's allocation characteristics, and it offers no streaming or parallel reader.
- IronCompress ships no `win-x86` native binary, so 32-bit .NET Framework applications fail at
  runtime on any compressed write.
- See [docs/07-KNOWN-LIMITATIONS.md](docs/07-KNOWN-LIMITATIONS.md) for the full audit.

## [0.0.3] - 2026-09-05

### Added
- SIMD hardware acceleration for column transforms and timestamp conversions (#103).
- Zero-copy blittable struct array materialization via `MemoryMarshal.Cast` (#102), with
  memory-safe full property test matrix (#114).
- Dictionary string deduplication and L1 span cache (#101); eliminated string and binary
  serialization boxing allocations (#118).
- First-class `TimeOnly` column support via `TimeDataField` migration (#111).
- Drop-in interop: honor `System.Text.Json` and `Parquet.Net` attributes (#112).
- IL interrogation workflow (`ilspycmd` + `dotnet-inspect`) (#109) and `dotnet-dump` memory
  profiling/triage (#110); automated IL zero-boxing regression gate in `dotnet test` (#132).
- Eager progressive column buffer return during row group serialization (#133); zero-copy
  nullable writes via `WriteAllPartsAsync` (#113).
- Public API surface tracking/guarding with `PublicApiAnalyzers` (#94); `Meziantou.Analyzer`
  for performance and correctness (#93); CSharpier deterministic formatting (#95).
- Git LFS provenanced public benchmark datasets with real-world reporting (#96) and tiered
  PR-slice vs deep-profiling benchmarking (#74).

### Changed
- Raised code coverage quality gates to 85% line / 70% branch (#138) with PR sticky comment.
- Pinned all GitHub Actions to 40-char commit SHAs (#104); enforced no-Python-files policy (#97).

## [0.0.2] - 2026-09-02

### Added
- `Parquet.SourceGenerator.V5` package emitting against the Parquet.Net 4.x/5.x `DataColumn`
  API, restoring .NET Framework 4.7.2 support (#48); renamed to `Parquet.SourceGenerator.Legacy`
  (#49).
- .NET 9 target framework support with dual .NET 8/.NET 9 package consumption testing (#45).
- `ReadParquetStreamAsync` `IAsyncEnumerable<T>` streaming reader (#42).
- Pre-allocated single destination array with index ranges in `ReadParquetParallelAsync` (#43).
- Declaration order used as the default for un-ordered columns (#35).

### Changed
- Audited behavioral gaps and closed everything outside the net472 backend (#47).

## [0.0.1] - 2026-08-05

First public release on NuGet.org.

### Added
- Roslyn incremental source generator emitting zero-reflection Parquet serializers and
  deserializers against the Parquet.Net 6 low-level API (#1).
- Native AOT verified for real in CI — publish with the ILCompiler and execute the resulting
  binary (#21).
- Release pipeline: NuGet trusted publishing (#25) and a `workflow_dispatch` release trigger
  with version input (#26).

### Fixed
- Package layout made consumable and verified in CI; column names escaped and hint names
  namespace-qualified in generated code (#19, #20).
- `CompressionMethod` was accepted and discarded; microsecond timestamps were silently written
  at millisecond precision. Both now reach the writer (#22).
