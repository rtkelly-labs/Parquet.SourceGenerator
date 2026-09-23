<p align="center">
  <img src="https://raw.githubusercontent.com/rtkelly13/Parquet.SourceGenerator/main/docs/assets/logo.svg" width="220" alt="Parquet.SourceGenerator Logo" />
</p>

# Parquet.SourceGenerator

[![Build & E2E Status](https://github.com/rtkelly13/Parquet.SourceGenerator/actions/workflows/ci.yml/badge.svg)](https://github.com/rtkelly13/Parquet.SourceGenerator/actions/workflows/ci.yml)
[![Documentation](https://img.shields.io/badge/docs-docs.ryankelly.dev-blue.svg)](https://docs.ryankelly.dev/parquet-sourcegenerator)
[![NuGet](https://img.shields.io/nuget/v/Parquet.SourceGenerator.svg)](https://www.nuget.org/packages/Parquet.SourceGenerator)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/LICENSE)

A high-performance, zero-reflection C# Roslyn source generator that emits strongly-typed Parquet serializers and deserializers at compile time, targeting [Parquet.Net](https://github.com/aloneguid/parquet-dotnet) low-level columnar primitives.

- ⚡ **1.5× – 3.3× Faster**: Direct columnar array transposition and zero reflection overhead.
- 📉 **20% – 54% Less Memory**: Eager progressive buffer return lifecycle via `ArrayPool.Shared`.
- 🚀 **Native AOT Ready**: 100% reflection-free generated code, verified in CoreCLR Linux x64 AOT CI.
- 🧵 **Multi-Core Parallel Reader**: Chunk-parallel decoding over memory buffers.
- 🌊 **Memory-Bounded Streaming**: Fixed-chunk row group streaming and `IAsyncEnumerable<T>` support.

---

<!-- BENCHMARK_TABLE_START -->
## ⚡ Performance & Benchmarks

Zero-reflection C# source generation vs **`ParquetSerializer` v6** reflection baseline:

| Operation | Scale | Reflection Baseline | Source Generator | Speedup | Memory Reduction |
|:--- |:---:|:---:|:---:|:---:|:---:|
| **File Serialization (Write)** | 100,000 items | 6.66 ms (11.00 MB) | **2.57 ms** (**5.74 MB**) | ⚡ **2.6x faster** | 📉 **48% less memory** |
| **Streaming Batched Write** | 100,000 items | 6.66 ms (11.00 MB) | **3.39 ms** (**5.03 MB**) | ⚡ **2.0x faster** | 📉 **54% less memory** |
| **File Deserialization (Read)** | 100,000 items | 12.41 ms (12.30 MB) | **6.05 ms** (**8.99 MB**) | ⚡ **2.0x faster** | 📉 **27% less memory** |
| **Parallel Deserialization (Read)** | 100,000 items | 12.41 ms (12.30 MB) | **6.95 ms** (**9.86 MB**) | ⚡ **1.8x faster** | 📉 **20% less memory** |
| **Streaming Read (IAsyncEnumerable)** | 100,000 items | 12.41 ms (12.30 MB) | **5.18 ms** (**8.22 MB**) | ⚡ **2.4x faster** | 📉 **33% less memory** |
| **Guid Serialization** | 100,000 items | 15.82 ms (17.71 MB) | **10.39 ms** (**10.70 MB**) | ⚡ **1.5x faster** | 📉 **40% less memory** |

> 📌 **Note**: BenchmarkDotNet results captured on GitHub Actions. Detailed multi-scale reports (1K, 10K, 100K, 1M rows) are in [docs/BENCHMARKS.md](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/BENCHMARKS.md).


## 🌐 Real-World Provenanced Dataset Benchmarks

Fixed public datasets tracked under Git LFS with full cryptographic SHA-256 data provenance:
- **TPC-H SF 0.01 LineItem**: 60,175 rows, 16 columns (decimals, dates, strings, dictionary encoding)
- **Adult Census Income**: 32,561 rows, 15 columns (9 categorical dictionary columns)
- **Diamonds**: 53,940 rows, 10 columns (continuous float metrics & ordinal cuts)

| Operation | Scale | Reflection Baseline | Source Generator | Speedup | Memory Reduction |
|:--- |:---:|:---:|:---:|:---:|:---:|
| **TPC-H LineItem Deserialization** | 60,175 rows | 85.45 ms (55.11 MB) | **54.69 ms** (**38.59 MB**) | ⚡ **1.6x faster** | 📉 **30% less memory** |
| **TPC-H LineItem Parallel Deserialization** | 60,175 rows | 85.45 ms (55.11 MB) | **57.63 ms** (**39.08 MB**) | ⚡ **1.5x faster** | 📉 **29% less memory** |
| **TPC-H LineItem Streaming Deserialization** | 60,175 rows | 85.45 ms (55.11 MB) | **50.11 ms** (**38.13 MB**) | ⚡ **1.7x faster** | 📉 **31% less memory** |
| **Adult Census Deserialization (Dictionaries)** | 32,561 rows | 44.32 ms (29.20 MB) | **31.55 ms** (**20.38 MB**) | ⚡ **1.4x faster** | 📉 **30% less memory** |
| **Adult Census Parallel Deserialization** | 32,561 rows | 44.32 ms (29.20 MB) | **34.92 ms** (**23.04 MB**) | ⚡ **1.3x faster** | 📉 **21% less memory** |
| **Adult Census Streaming Deserialization** | 32,561 rows | 44.32 ms (29.20 MB) | **14.09 ms** (**20.13 MB**) | ⚡ **3.1x faster** | 📉 **31% less memory** |
| **Diamonds Deserialization** | 53,940 rows | 30.74 ms (19.73 MB) | **14.08 ms** (**12.66 MB**) | ⚡ **2.2x faster** | 📉 **36% less memory** |
| **Diamonds Parallel Deserialization** | 53,940 rows | 30.74 ms (19.73 MB) | **14.03 ms** (**15.79 MB**) | ⚡ **2.2x faster** | 📉 **20% less memory** |
| **Diamonds Streaming Deserialization** | 53,940 rows | 30.74 ms (19.73 MB) | **7.93 ms** (**12.25 MB**) | ⚡ **3.8x faster** | 📉 **38% less memory** |

### 🗜️ TPC-H LineItem Multi-Codec Serialization Throughput (60,175 rows)

| Codec | Compression Profile | Serialization Time | Allocated Memory |
|:--- |:---:|:---:|:---:|
| **Snappy** | Generator Built-in | **21.51 ms** | **10.96 MB** |
| **Zstandard (Fastest)** | Generator Built-in | **61.62 ms** | **22.40 MB** |
| **Zstandard (Optimal)** | Generator Built-in | **70.02 ms** | **23.06 MB** |
| **Uncompressed** | Generator Built-in | **40.31 ms** | **38.18 MB** |
<!-- BENCHMARK_TABLE_END -->

---

## 📦 Quick Start (30 Seconds)

### 1. Installation

Install `Parquet.SourceGenerator` and `Parquet.Net` into your project:

```bash
dotnet add package Parquet.SourceGenerator
dotnet add package Parquet.Net
```

`Parquet.SourceGenerator.Attributes` is referenced automatically. `Parquet.Net` is required because the generated code targets its low-level columnar APIs directly.

### 2. Define Your Model

Decorate your model with `[ParquetSerializable]` and declare it as `partial`:

```csharp
using System;
using Parquet.SourceGenerator;

[ParquetSerializable]
public partial record UserEvent
{
    [ParquetColumn("event_id", Order = 1)]
    public Guid Id { get; init; }

    [ParquetColumn("username", Order = 2)]
    public string Username { get; init; } = string.Empty;

    [ParquetColumn("timestamp", Order = 3)]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime Timestamp { get; init; }

    [ParquetColumn("duration", Order = 4)]
    public TimeSpan Duration { get; init; }
}
```

### 3. Writing Parquet Files

```csharp
List<UserEvent> events = GetEvents();
using var stream = File.Create("events.parquet");

// Simple write
await events.WriteParquetAsync(stream);

// Chunked streaming write in fixed 10,000 row-group chunks
await events.WriteParquetBatchedAsync(
    stream,
    new ParquetSerializerOptions { RowGroupSize = 10_000 });

// Stream directly from IAsyncEnumerable<T>
IAsyncEnumerable<UserEvent> eventStream = GetAsyncEventStream();
await eventStream.WriteParquetAsync(
    stream,
    new ParquetSerializerOptions { RowGroupSize = 10_000 });
```

#### Writing from data that is already columnar

If the caller already holds contiguous column buffers — Arrow arrays, a query engine's column
vectors, pre-split `ReadOnlyMemory<T>` — there is no reason to materialise POCOs first. Flat models
also get a generated batch struct whose buffers go straight to Parquet.Net with no pooled rental and
no copy:

```csharp
var batch = new UserEventColumnarBatch
{
    RowCount = rowCount,
    Id = idBuffer,                                  // ReadOnlyMemory<int>
    Name = nameBuffer,                              // ReadOnlyMemory<ReadOnlyMemory<char>?>
    Score = packedScores,                           // packed non-nulls only
    ScoreDefinitionLevels = scoreDefinitionLevels,  // 1 = present, 0 = null, one per row
};

await batch.WriteParquetAsync(stream);
```

Nullable value columns take packed values plus explicit definition levels, because that is the only
shape Parquet.Net's `WriteAllPartsAsync` accepts without an intermediate buffer. On a 16-column
schema this removes around 7% of end-to-end write time (the transpose it deletes); allocation is
unchanged, since the row-oriented path's rentals come from a warm `ArrayPool`. Measured numbers, both
GC modes, and the reasons the API is shaped this way are in
[docs/12](docs/12-BUFFER-REUSE-AND-EXTRACTION-STRATEGIES.md#-6-direct-columnar-handoff--measured-issue-137).
Models with struct, list or map members keep the row-oriented API only.

### 4. Reading Parquet Files (Sequential & Multi-Core Parallel)

```csharp
using var stream = File.OpenRead("events.parquet");

// Sequential read
UserEvent[] events = await UserEventParquet.From(stream).ToArrayAsync();

// Need a List<T>? Convert the array: the reader has one materialised shape.
List<UserEvent> eventList = events.ToList(); // or new List<UserEvent>(events)

// Multi-core parallel read over an in-memory byte buffer
ReadOnlyMemory<byte> buffer = File.ReadAllBytes("events.parquet");
UserEvent[] fast = await UserEventParquet
    .From(buffer)
    .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 8 })
    .Parallel()
    .ToArrayAsync();

// Low-memory streaming reader
await foreach (var e in UserEventParquet.From(buffer).AsAsyncEnumerable())
{
    // Process item by item with O(1) memory
}

// Columnar (struct-of-arrays) batches — one per row group, no UserEvent ever constructed
await foreach (var batch in UserEventParquet.From(buffer).Batches())
{
    ReadOnlySpan<long> ids = batch.UserIdSpan;
    ReadOnlySpan<double> amounts = batch.AmountSpan;
    // SIMD-friendly: the spans alias pooled buffers, valid until the next iteration
}
```

`<Model>Parquet.From(...)` is the only generated read entry point, and both overloads return the
same `readonly struct <Model>ParquetReader`: the source (`Stream` or `ReadOnlyMemory<byte>`), the
execution (`.Parallel()`), pushdown (`.Where(...)`) and options (`.WithOptions(...)`) are reader
state, and the terminal (`ToArrayAsync`, `AsAsyncEnumerable`, `Batches`) picks the shape. A
`List<T>` is a conversion of the array (`.ToList()` or `new List<T>(array)`), not a separate read.
Combinations no backend can execute throw `NotSupportedException` from the call that completes them
rather than being silently degraded: `.Parallel()` on a `Stream` source (buffer the file and use
`From(ReadOnlyMemory<byte>)`); `.Parallel()` together with `.Where(...)`; `AsAsyncEnumerable()` or
`Batches()` after `.Parallel()` (streaming is sequential); and `Batches()` after `.Where(...)`. The
flat `ReadParquet*Async` methods were removed before `0.1.0`; the mapping is in [CHANGELOG.md](CHANGELOG.md) and the decision in
[docs/48](docs/48-FLAT-READ-REMOVAL-480.md). The `Parquet.SourceGenerator.Legacy` package has no
builder and keeps its flat `ReadParquetAsync` / `ReadParquetArrayAsync`.

`Batches()` is emitted for flat models only (no nested structs, lists or maps) and
allocates no domain objects; the pooled column buffers are returned when the enumerator advances
or is disposed, so nothing in a batch may outlive the loop body.

### 5. Row-Group Pruning with Min/Max Statistics

`.Where(...)` on the read builder takes a predicate over the statistics Parquet records in the file
footer, from either source and for every materializing or streaming shape. A row group the zone map rules out is never opened: no page read, no decompression,
no buffer rental.

```csharp
// Only the row groups whose [min, max] range can still hold a key >= 1000 are read.
OrderEvent[] recent = await OrderEventParquet
    .From(stream)
    .Where(meta => meta.OrderKey.MayContainAtLeast(1_000))
    .ToArrayAsync();

// Conjunctive filters compose; any column that cannot match prunes the whole group.
OrderEvent[] narrow = await OrderEventParquet
    .From(stream)
    .Where(meta => meta.OrderKey.MayContainBetween(1_000, 2_000)
                && meta.Region.MayContain("emea"))
    .ToArrayAsync();
```

The generated `<Model>RowGroupMetadata` struct exposes `RowGroupIndex`, `RowCount` and one
`ParquetColumnStatistics<T>` per integral, floating-point or string column, carrying `Min`, `Max`,
`NullCount`, `DistinctCount` and the `May*` range helpers. Only the generated reader constructs it
(its constructor is `internal`); a predicate just reads it. Pruning is conservative: a row group
whose statistics are incomplete is always read.

### 6. Custom Configuration (`ParquetSerializerOptions`)

```csharp
var options = new ParquetSerializerOptions
{
    RowGroupSize = 25_000,
    MaxDegreeOfParallelism = 8,
    CompressionMethod = ParquetCompressionMethod.Zstd,
    CompressionLevel = ParquetCompressionLevel.Fastest
};

await events.WriteParquetBatchedAsync(stream, options: options);
```

Supported codecs: `None`, `Snappy` (default), `Gzip`, `Lz4`, `Brotli`, and `Zstd`.

---

## ✨ Core Features & Architecture

- **Zero Runtime Reflection**: Schemas, serializers, and deserializers are generated at compile time as strongly-typed C# extensions.
- **Low-Level Parquet.Net Primitives**: Emits direct calls to `ParquetRowGroupWriter.WriteAsync` and `WriteAllPartsAsync`, bypassing reflection overhead and boxing.
- **Eager Progressive Buffer Returns**: Column buffers rented from `ArrayPool.Shared` are returned immediately after writing each column chunk, releasing memory milliseconds earlier during asynchronous I/O and compression.
- **Single-Pass Row Transposition**: Domain models are traversed once, maximizing CPU L1/L2 cache spatial locality.
- **Multi-Core Parallel Reader**: Decodes independent row groups concurrently across CPU threads when reading from in-memory buffers (`ReadOnlyMemory<byte>`).
- **Nullability-Aware Schemas**: Under `#nullable enable`, non-null types map to `required` columns and nullable types (`T?`) map to `optional` columns automatically.

---

## 🛡️ Safety, Compatibility & Diagnostics

### Compile-Time Diagnostics

The Roslyn analyzer enforces correct usage at compile time, catching errors before build or runtime:

```csharp
// ❌ Produces PARQ001 at compile time: type must be partial
[ParquetSerializable]
public record Metric(int Id); 
```

Full details on all 11 diagnostic rules, examples, and fixes are documented in **[`docs/13-COMPILER-DIAGNOSTICS.md`](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/13-COMPILER-DIAGNOSTICS.md)**:
- `PARQ001`: Type must be declared `partial`
- `PARQ002`: Duplicate `[ParquetColumn]` column names detected
- `PARQ005`: Invalid `[ParquetDecimal]` precision or scale
- `PARQ006`: Unsupported property type (mirrors Parquet.Net supported types)
- `PARQ007`–`PARQ010`: Assignability, constructors, nested, and generic type constraints
- `PARQ011`: Classic API version compatibility

---

## 🚀 Native AOT & Cold-Start Performance

`Parquet.SourceGenerator` is designed from the ground up for **.NET Native AOT** (Ahead-of-Time compilation). Traditional reflection-based serializers fail or require brittle trimmer configurations under Native AOT because expression trees and dynamic delegates cannot be emitted at runtime. `Parquet.SourceGenerator` emits 100% compile-time, reflection-free C# primitives.

### The "Naive Case": Cold Invocations & Short-Lived Jobs

In long-running daemon processes, JIT compilation cost is amortized after thousands of warmup iterations. However, in real-world **CLI utilities, serverless functions (AWS Lambda, Azure Functions), and ephemeral container batch jobs**, the process runs only once. 

In this "naive case", eliminating runtime JIT overhead yields dramatic gains:

| Metric | Standard CoreCLR (JIT) | Native AOT (Ahead-of-Time) | Improvement |
| :--- | :---: | :---: | :---: |
| **Total Process Wall-Clock Time** | **358 ms** | **50 ms** | ⚡ **7.2× faster process time** |
| **CPU Time (Execution + JIT Compile)** | **0.30 s** | **0.02 s** | ⚡ **15× less CPU time** |
| **Peak Working Set (Max RSS Memory)** | **57.0 MB** | **15.8 MB** | 📉 **72% less memory (3.6× reduction)** |

*Measured executing the complete 11-step end-to-end serialization and compression test matrix under macOS ARM64.*

#### Architectural Drivers of the 7.2× Acceleration
1. **Zero Runtime JIT Compilation**: CoreCLR must compile ~15 methods per model on demand upon first call, consuming ~280 ms of pure CPU time. Native AOT executes pre-compiled machine code within 2 milliseconds of process launch.
2. **Static Generic Dictionaries**: `ArrayPool<T>`, `ReadOnlyMemory<T>`, and `List<T>` metadata tables are statically baked into the binary data segment (`mmap`) rather than dynamically synthesized.
3. **Stripped Runtime Footprint**: No JIT compiler engine (`clrjit`), IL bytecode, or dynamic symbol tables are loaded, cutting process memory from 57 MB to 15.8 MB.

To enable Native AOT in your application:

```xml
<PropertyGroup>
    <PublishAot>true</PublishAot>
</PropertyGroup>
```

> 📖 For a deep dive into CoreCLR type system mechanics, `Nullable<T>` value type sharing, and runtime directives, see **[`docs/10-NATIVE-AOT-GUIDE.md`](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/10-NATIVE-AOT-GUIDE.md)**.

---

## 🏹 Apache Arrow RecordBatch Ingestion (Experimental)

If your pipeline already holds an `Apache.Arrow.RecordBatch` (DataFusion, Arrow Flight, PyArrow via
IPC), row-wise extraction through the POCO writer is wasted work — Arrow columns are already the
contiguous buffers Parquet.Net wants. Add the package and the generator emits the bridge:

```xml
<PackageReference Include="Apache.Arrow" Version="23.0.0" />
```

```csharp
await using var writer = await ParquetWriter.CreateAsync(OrderEventParquetExtensions.Schema, stream);
OrderEventParquetExtensions.WriteParquetRowGroupAsync(writer, recordBatch);
```

- **No new dependency.** Neither the generator nor the Attributes package references Apache.Arrow.
  The extra file (`{Namespace}.{Type}.Arrow.g.cs`, one per Arrow-representable type) is emitted only
  when *your* compilation references Apache.Arrow — so a project that opts in generates two files
  per annotated type instead of one.
- **Strict validation.** Columns are matched by name and checked by physical/logical type before
  anything is written; every offending field is reported in one `InvalidDataException`. Nothing is
  silently coerced, and `DictionaryArray` / `LargeUtf8` are rejected outright.
- **Zero-copy where it is real.** Fixed-width Arrow buffers are reinterpreted in place — no copy, no
  `ArrayPool` rental. Nullable columns derive their definition levels from the Arrow validity
  bitmap and produce byte-identical files to the POCO path.
- **Supported floor:** Apache.Arrow `23.0.0`. Full mapping table and rejection rules in
  **[`docs/14-COMPATIBILITY-MATRIX.md`](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/14-COMPATIBILITY-MATRIX.md#apache-arrow-recordbatch-ingestion-experimental-177)**.

---

## 🛡️ Compatibility & Known Limitations

| Capability | Status | Notes |
|:--- |:---:|:--- |
| **Supported Types** | `Guid`, `DateTime`, `TimeSpan`, `Enum`, `decimal`, `byte[]`, `string`, numeric primitives, `Nullable<T>` | Standard flat analytical schemas. |
| **Nested Collections** | ❌ Unsupported | `List<T>` or `Dictionary<K, V>` reported at compile time as `PARQ006`. |
| **`DateTimeOffset`** | ❌ Unsupported | Parquet has no direct representation; use `DateTime` + offset column. |
| **Positional Records** | ❌ Unsupported | Constructor with parameters reported as `PARQ008`. Use nominal records with `{ get; init; }`. |
| **.NET Framework (net472)** | ✅ Supported via V5 | Use `Parquet.SourceGenerator.V5` for Parquet.Net 4.x/5.x support. |
| **Apache Arrow ingestion** | 🧪 Experimental (v6 only) | Emitted only when the consumer references Apache.Arrow. Flat models only; Native AOT exercised by the repository's published AOT harness. |
| **Generator feature level** | ✅ Configurable | Defaults to `Level2CompoundPreview`; pin `Level1Flat` or opt into `Level3ModernCSharp` with `ParquetGeneratorFeatureLevel`. |
| **V5 generated API** | ✅ Declared core subset | V5 intentionally exposes flat read/write, batched write, row-group write, and schema; modern builder, filtering, parallel, streaming, column-batch, and Arrow members are v6-only. |

> A complete audit of limitations and remediation roadmap is in **[`docs/07-KNOWN-LIMITATIONS.md`](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/07-KNOWN-LIMITATIONS.md)**.

---

## 📖 Documentation Hub

> 🌐 **Interactive Documentation & API Catalog**: Visit [docs.ryankelly.dev/parquet-sourcegenerator](https://docs.ryankelly.dev/parquet-sourcegenerator) for interactive guides, live search, architecture diagrams, and full generated API symbol catalogs.

| Document | Topic |
|:--- |:--- |
| 🏗️ **[01 - Vision & Architecture](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/01-VISION-AND-ARCHITECTURE.md)** | Core design tenets, columnar transposition, and benchmarks. |
| 🏷️ **[02 - API Design & Attributes](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/02-API-DESIGN-AND-ATTRIBUTES.md)** | `[ParquetSerializable]`, `[ParquetColumn]`, decimals, and timestamps. |
| ⚙️ **[03 - Incremental Generator Pipeline](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/03-INCREMENTAL-GENERATOR-PIPELINE.md)** | Roslyn incremental pipeline stages and caching semantics. |
| ⚠️ **[07 - Known Limitations Audit](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/07-KNOWN-LIMITATIONS.md)** | Comprehensive audit of behavioural gaps and remediation plans. |
| 🚀 **[10 - Native AOT & Trimming Guide](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/10-NATIVE-AOT-GUIDE.md)** | ILCompiler analysis, CoreCLR runtime directives, and AOT compilation. |
| 🔬 **[11 - Performance & Zero-Boxing Findings](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/11-PERFORMANCE-OPTIMIZATION-FINDINGS.md)** | IL interrogation, zero-boxing string serialization, and L1 cache deduplication. |
| 🧠 **[12 - Buffer Reuse & Extraction Strategies](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/12-BUFFER-REUSE-AND-EXTRACTION-STRATEGIES.md)** | CPU cache spatial locality vs multi-pass traversal empirical analysis. |
| 🛡️ **[13 - Compiler Diagnostics Reference](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/13-COMPILER-DIAGNOSTICS.md)** | Full catalog of `PARQ001`–`PARQ011` diagnostic rules, causes, and fixes. |
| 🧪 **[14 - Parquet Compatibility Matrix](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/14-COMPATIBILITY-MATRIX.md)** | Supported format envelope, producer/consumer boundaries, and compatibility definitions. |
| 📊 **[Full Benchmarks Report](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/BENCHMARKS.md)** | Multi-scale sweeps (1k, 10k, 100k, 1M rows) and real-world datasets. |

---

## 🤝 Contributing & Community

Contributions are welcome! Please review our community guidelines:

- 📖 **[Contributing Guide](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/CONTRIBUTING.md)**: Build setup, testing, and PR guidelines.
- 📜 **[Code of Conduct](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/CODE_OF_CONDUCT.md)**: Community standards.
- 🛡️ **[Security Policy](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/SECURITY.md)**: Vulnerability disclosure.
- 📝 **[Changelog](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/CHANGELOG.md)**: Release history and notes.

To build from source:

```bash
git clone https://github.com/rtkelly13/Parquet.SourceGenerator.git
cd Parquet.SourceGenerator
dotnet build Parquet.SourceGenerator.slnx --configuration Release
```

---

## 📄 License

This project is licensed under the [MIT License](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/LICENSE).
