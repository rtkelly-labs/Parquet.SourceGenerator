# Writing Parquet Guide

The `Parquet.SourceGenerator` write API generates zero-reflection, high-throughput serializers that write strongly typed C# models directly to Parquet files and streams.

```csharp
await events.WriteParquetAsync(stream);
```

This guide explains how to serialize in-memory collections, stream asynchronous data, write columnar batches, and configure row groups and compression.

---

## 1. Writing In-Memory Collections

The simplest way to write Parquet data is via the `WriteParquetAsync` extension method, generated for any collection, list, or array of an annotated model:

```csharp
using var stream = File.Create("users.parquet");
List<UserEvent> events = GetEvents();

await events.WriteParquetAsync(stream);
```

### 1.1 Supported Collection Shapes
The generator emits overloads for:
- `IReadOnlyList<T>` / `List<T>` / `T[]`
- `IEnumerable<T>`
- Read-only spans and segments

When writing an in-memory collection with default options, all rows are written into a single row group unless batching options are specified.

---

## 2. Chunked & Streaming Writes

For large datasets, writing rows in chunks (row groups) prevents excessive memory usage and enables downstream engines (DuckDB, Spark, Polars) to prune row groups and process data in parallel.

### 2.1 Fixed-Size Row Group Streaming (`WriteParquetBatchedAsync`)
When writing large collections, use `WriteParquetBatchedAsync` to flush data at regular row thresholds:

```csharp
using var stream = File.Create("large_dataset.parquet");

await items.WriteParquetBatchedAsync(
    stream,
    new ParquetSerializerOptions
    {
        RowGroupSize = 50_000,
        CompressionMethod = CompressionMethod.Snappy
    });
```

The serializer rents columnar buffers from `ArrayPool.Shared`, transposes each batch of 50,000 rows into contiguous columnar memory, writes the row group, and recycles all leased buffers immediately before processing the next chunk.

### 2.2 Asynchronous Streaming (`IAsyncEnumerable<T>`)
When pulling data continuously from an asynchronous source (such as EF Core streaming queries, Cosmos DB change feeds, or message queues), call `WriteParquetAsync` directly on the `IAsyncEnumerable<T>`:

```csharp
IAsyncEnumerable<UserEvent> eventFeed = GetLiveEventStream();
using var stream = File.Create("streamed_events.parquet");

await eventFeed.WriteParquetAsync(
    stream,
    new ParquetSerializerOptions
    {
        RowGroupSize = 25_000,
        CompressionMethod = CompressionMethod.Zstd
    });
```

Rows are accumulated until `RowGroupSize` is reached, at which point the row group is serialized and dispatched, keeping the memory working set strictly bounded.

---

## 3. High-Throughput Columnar Writes (`<Model>Batch`)

If your application already manages columnar data—such as data ingested from Arrow, an analytical query engine, or previous Parquet batches—transposing into intermediate POCOs adds unnecessary CPU and GC overhead.

You can write directly from a generated `<Model>Batch`:

```csharp
await foreach (TransactionLogBatch batch in TransactionLogParquet.From(inputStream).AsBatches())
{
    // Filter or transform columns directly within the batch
    using var outputStream = new MemoryStream();
    
    // Write directly to Parquet without allocating C# row objects
    await batch.WriteParquetAsync(outputStream);
}
```

This path binds directly to `ParquetWriter.WriteAsync<T>(field, ReadOnlyMemory<T>)`, achieving zero object allocations and native columnar transfer speeds.

---

## 4. Serialization Options (`ParquetSerializerOptions`)

Both `WriteParquetAsync` and `WriteParquetBatchedAsync` accept an optional `ParquetSerializerOptions` instance:

```csharp
var options = new ParquetSerializerOptions
{
    // Compression codec (Snappy, Gzip, Zstd, None)
    CompressionMethod = CompressionMethod.Zstd,

    // Optional fine-grained compression level
    CompressionLevel = System.IO.Compression.CompressionLevel.Optimal,

    // Number of rows per row group (default: 50,000)
    RowGroupSize = 100_000
};

await events.WriteParquetBatchedAsync(stream, options);
```

### 4.1 Compression Codecs
- **`CompressionMethod.Snappy`** (Default): Fast, balanced compression optimal for analytical processing.
- **`CompressionMethod.Zstd`**: Superior compression ratio with high decompression speeds.
- **`CompressionMethod.Gzip`**: Legacy compatibility across Hadoop ecosystems.
- **`CompressionMethod.None`**: Uncompressed raw parquet; fastest serialization when I/O is not the bottleneck.

> ⚠️ **Platform Constraint:** Compressed serialization relies on `IronCompress`. On Windows 32-bit (`win-x86`) processes, native compression binaries are unavailable (see [Known Limitations](../reference/known-limitations.md)).

---

## 5. Performance Guidelines & Zero-Boxing

The source generator emits IL that ensures:
1. **Zero Boxing**: Primitive and value-type properties are written directly to unboxed spans/arrays (`WriteParquetRowGroupAsync` emits zero `box` IL instructions).
2. **Buffer Recycling**: Column arrays are rented from `ArrayPool.Shared` and returned in `finally` blocks, maintaining predictable Gen0/Gen1 GC behavior under heavy loads.
3. **Single Pass**: Writing from lists or arrays computes column vectors in a single sequential cache-friendly sweep across memory.
