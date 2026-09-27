# Parquet.SourceGenerator

A zero-reflection C# Roslyn source generator that emits Parquet serializers and deserializers at compile time, targeting [Parquet.Net](https://github.com/aloneguid/parquet-dotnet) low-level primitives.

## 📦 Installation

```bash
dotnet add package Parquet.SourceGenerator
dotnet add package Parquet.Net
```

`Parquet.SourceGenerator.Attributes` is automatically included as a dependency. `Parquet.Net` must be referenced directly by your project.

## 📖 Quick Start Example

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

### Writing Parquet Files

```csharp
List<UserEvent> events = GetEvents();
using var stream = File.Create("events.parquet");

// Simple write
await events.WriteParquetAsync(stream);

// Chunked row-group streaming write
await events.WriteParquetBatchedAsync(
    stream,
    new ParquetSerializerOptions { RowGroupSize = 10_000 });

// Stream directly from IAsyncEnumerable<T>
IAsyncEnumerable<UserEvent> eventStream = GetAsyncEventStream();
await eventStream.WriteParquetAsync(
    stream,
    new ParquetSerializerOptions { RowGroupSize = 10_000 });
```

### Reading Parquet Files (Sequential & Parallel)

```csharp
using var stream = File.OpenRead("events.parquet");

// Sequential read
List<UserEvent> events = await UserEventParquet.From(stream).ToListAsync();

// Read from in-memory byte buffer
ReadOnlyMemory<byte> buffer = File.ReadAllBytes("events.parquet");
List<UserEvent> memEvents = await UserEventParquet.From(buffer).ToListAsync();

// Multi-core parallel read across row groups (buffer source only: one reader over one
// stream cannot decode row groups concurrently)
List<UserEvent> parallelEvents = await UserEventParquet
    .From(buffer)
    .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
    .Parallel()
    .ToListAsync();
```

### Custom Configuration (`ParquetSerializerOptions`)

```csharp
var options = new ParquetSerializerOptions
{
    RowGroupSize = 25_000,
    MaxDegreeOfParallelism = 8,
    CompressionMethod = ParquetCompressionMethod.Zstd
};

await events.WriteParquetBatchedAsync(stream, options: options);
```

Compression options: `None`, `Snappy` (default), `Gzip`, `Lz4`, `Brotli`, `Zstd`.

<!-- BENCHMARK_TABLE_START -->
## ⚡ Performance & Benchmarks

Zero-reflection C# source generation vs **`ParquetSerializer` v6** reflection baseline:

| Operation | Scale | Reflection Baseline | Source Generator | Speedup | Memory Reduction |
|:--- |:---:|:---:|:---:|:---:|:---:|
| **File Serialization (Write)** | 100,000 items | 8.02 ms (11.00 MB) | **3.02 ms** (**5.74 MB**) | ⚡ **2.6x faster** | 📉 **48% less memory** |
| **Streaming Batched Write** | 100,000 items | 8.02 ms (11.00 MB) | **3.80 ms** (**5.03 MB**) | ⚡ **2.1x faster** | 📉 **54% less memory** |
| **File Deserialization (Read)** | 100,000 items | 12.73 ms (12.30 MB) | **6.91 ms** (**8.99 MB**) | ⚡ **1.9x faster** | 📉 **27% less memory** |
| **Parallel Deserialization (Read)** | 100,000 items | 12.73 ms (12.30 MB) | **7.75 ms** (**9.89 MB**) | ⚡ **1.6x faster** | 📉 **20% less memory** |
| **Streaming Read (IAsyncEnumerable)** | 100,000 items | 12.73 ms (12.30 MB) | **5.92 ms** (**8.23 MB**) | ⚡ **2.2x faster** | 📉 **33% less memory** |
| **Guid Serialization** | 100,000 items | 15.14 ms (17.71 MB) | **10.50 ms** (**10.70 MB**) | ⚡ **1.4x faster** | 📉 **40% less memory** |

> 📌 **Note**: BenchmarkDotNet results captured on GitHub Actions. Detailed multi-scale reports (1K, 10K, 100K, 1M rows) are in [docs/BENCHMARKS.md](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/BENCHMARKS.md).


## 🌐 Real-World Provenanced Dataset Benchmarks

Fixed public datasets tracked under Git LFS with full cryptographic SHA-256 data provenance:
- **TPC-H SF 0.01 LineItem**: 60,175 rows, 16 columns (decimals, dates, strings, dictionary encoding)
- **Adult Census Income**: 32,561 rows, 15 columns (9 categorical dictionary columns)
- **Diamonds**: 53,940 rows, 10 columns (continuous float metrics & ordinal cuts)

| Operation | Scale | Reflection Baseline | Source Generator | Speedup | Memory Reduction |
|:--- |:---:|:---:|:---:|:---:|:---:|
| **TPC-H LineItem Deserialization** | 60,175 rows | 85.08 ms (55.11 MB) | **58.39 ms** (**38.61 MB**) | ⚡ **1.4x faster** | 📉 **30% less memory** |
| **TPC-H LineItem Parallel Deserialization** | 60,175 rows | 85.08 ms (55.11 MB) | **58.24 ms** (**39.12 MB**) | ⚡ **1.5x faster** | 📉 **29% less memory** |
| **TPC-H LineItem Streaming Deserialization** | 60,175 rows | 85.08 ms (55.11 MB) | **54.21 ms** (**38.15 MB**) | ⚡ **1.6x faster** | 📉 **31% less memory** |
| **Adult Census Deserialization (Dictionaries)** | 32,561 rows | 47.02 ms (29.20 MB) | **33.24 ms** (**20.38 MB**) | ⚡ **1.4x faster** | 📉 **30% less memory** |
| **Adult Census Parallel Deserialization** | 32,561 rows | 47.02 ms (29.20 MB) | **37.01 ms** (**23.15 MB**) | ⚡ **1.3x faster** | 📉 **21% less memory** |
| **Adult Census Streaming Deserialization** | 32,561 rows | 47.02 ms (29.20 MB) | **19.13 ms** (**20.13 MB**) | ⚡ **2.4x faster** | 📉 **31% less memory** |
| **Diamonds Deserialization** | 53,940 rows | 29.60 ms (19.73 MB) | **14.30 ms** (**12.68 MB**) | ⚡ **2.1x faster** | 📉 **36% less memory** |
| **Diamonds Parallel Deserialization** | 53,940 rows | 29.60 ms (19.73 MB) | **14.14 ms** (**15.90 MB**) | ⚡ **2.1x faster** | 📉 **19% less memory** |
| **Diamonds Streaming Deserialization** | 53,940 rows | 29.60 ms (19.73 MB) | **9.84 ms** (**12.27 MB**) | ⚡ **3.0x faster** | 📉 **38% less memory** |

### 🗜️ TPC-H LineItem Multi-Codec Serialization Throughput (60,175 rows)

| Codec | Compression Profile | Serialization Time | Allocated Memory |
|:--- |:---:|:---:|:---:|
| **Snappy** | Generator Built-in | **48.26 ms** | **14.89 MB** |
| **Zstandard (Fastest)** | Generator Built-in | **61.08 ms** | **22.40 MB** |
| **Zstandard (Optimal)** | Generator Built-in | **68.53 ms** | **23.06 MB** |
| **Uncompressed** | Generator Built-in | **39.87 ms** | **38.18 MB** |
<!-- BENCHMARK_TABLE_END -->

## 🛡️ Compiler Diagnostics

| Diagnostic ID | Severity | Description |
|:--- |:---:|:--- |
| **`PARQ001`** | **Error** | Target type decorated with `[ParquetSerializable]` must be declared as `partial`. |
| **`PARQ002`** | **Error** | Duplicate `[ParquetColumn]` column names detected on model. |
| **`PARQ003`** | **Warning** | Target type has no valid public serializable properties or fields. |
| **`PARQ004`** | **Warning** | Non-public property decorated with `[ParquetColumn]` will be ignored. |
| **`PARQ005`** | **Error** | Invalid `[ParquetDecimal]` precision or scale parameters. |

## 🚀 Native AOT & Cold-Start Performance

Under Native AOT, cold invocations (CLI tools, serverless Lambda functions, batch workers) run **7.2× faster** with **72% less memory**:

| Metric | CoreCLR (JIT) | Native AOT | Improvement |
| :--- | :---: | :---: | :---: |
| **Total Process Time** | **358 ms** | **50 ms** | ⚡ **7.2× faster** |
| **CPU User Time** | **0.30 s** | **0.02 s** | ⚡ **15× less CPU** |
| **Peak Working Set** | **57.0 MB** | **15.8 MB** | 📉 **72% less RAM** |

To enable Native AOT:
```xml
<PropertyGroup>
    <PublishAot>true</PublishAot>
</PropertyGroup>
```

Feature output can be pinned project-wide with `ParquetGeneratorFeatureLevel` in MSBuild or with
`[assembly: ParquetGeneratorOptions(FeatureLevel = ...)]`; see the feature-level documentation.

## Compatibility

The V5/classic generator is a declared core subset for Parquet.Net 4.x/5.x: flat read/write,
batched write, row-group write, and schema. The modern v6 generator additionally provides builder,
filtering, parallel, streaming, column-batch, and experimental Arrow surfaces. The compatibility
matrix documents the supported model and file envelope.

## 🔗 Links & Resources

* [Documentation Portal](https://docs.ryankelly.dev/parquet-sourcegenerator)
* [GitHub Repository](https://github.com/rtkelly13/Parquet.SourceGenerator)
* [Design Documentation](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/INDEX.md)
* [Contributing Guide](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/CONTRIBUTING.md)
* [MIT License](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/LICENSE)
