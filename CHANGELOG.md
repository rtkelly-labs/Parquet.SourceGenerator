# Changelog

All notable changes to **Parquet.SourceGenerator** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

Changes since `0.0.1`. That version is published on nuget.org (alongside the `0.0.1-dev.1` and
`0.0.1-dev.2` prereleases); this section becomes the next release entry when one is cut.

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
