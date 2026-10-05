# Reading Parquet Guide

The modern `Parquet.SourceGenerator` read API is centered on a single, fluent entry point:

```csharp
<Model>Parquet.From(stream)
```

This guide explains how to read, stream, batch, and filter Parquet data with zero reflection and bounded memory.

---

## 1. Core Read Terminals

The fluent reader provides three primary execution terminals depending on your memory and concurrency needs:

### 1.1 In-Memory Array (`ToArrayAsync`)
When you want to deserialize an entire Parquet file or row group into memory at once:

```csharp
using var stream = File.OpenRead("transactions.parquet");

TransactionLog[] records = await TransactionLogParquet
    .From(stream)
    .ToArrayAsync();
```

> 💡 **Tip:** If you need a `List<T>`, call `.ToList()` on the returned array. The generator omits a separate `ToListAsync` to prevent duplicate code paths and redundant allocations.

### 1.2 Memory-Bounded Streaming (`AsAsyncEnumerable`)
When processing large datasets where buffering the entire file into memory would exhaust RAM:

```csharp
using var stream = File.OpenRead("transactions.parquet");

await foreach (TransactionLog record in TransactionLogParquet.From(stream).AsAsyncEnumerable())
{
    ProcessTransaction(record);
}
```

The generator streams row groups sequentially, returning internal array buffers to `ArrayPool.Shared` as each chunk finishes decoding.

### 1.3 High-Throughput Batched Iteration (`AsBatches`)
For maximum throughput in analytical pipelines, consume rows in borrowed columnar chunks:

```csharp
using var stream = File.OpenRead("transactions.parquet");

await foreach (TransactionLogBatch batch in TransactionLogParquet.From(stream).AsBatches())
{
    // Process the batch without per-row object allocations
    for (int i = 0; i < batch.Length; i++)
    {
        ProcessRecord(batch[i]);
    }
}
```

> ⚠️ **Buffer Lifetime Contract:** A `<Model>Batch` borrows its underlying memory for the duration of the loop iteration. Do not store batch references beyond the loop body without copying.

---

## 2. Predicate Pushdown & Column Filtering

Avoid decoding unwanted row groups by pushing predicates directly into the reader:

```csharp
// Filter row groups using column statistics before reading data pages
TransactionLog[] highValueRecords = await TransactionLogParquet
    .From(stream)
    .Where(tx => tx.Amount > 1000.00m)
    .ToArrayAsync();
```

When column statistics indicate that a row group's `[min, max]` range does not satisfy the predicate, the reader skips that row group entirely, eliminating disk I/O and decompression overhead.

---

## 3. In-Memory Byte Buffers

If your Parquet data is already in memory (e.g. from an HTTP payload or cloud blob download), use `ReadOnlyMemory<byte>` directly:

```csharp
ReadOnlyMemory<byte> buffer = await downloadTask;

// Synchronous decoding over memory buffers with optional multi-core parallelization
TransactionLog[] records = await TransactionLogParquet
    .From(buffer)
    .Parallel()
    .ToArrayAsync();
```

---

## 4. Migration from Legacy Flat Reads

Previous versions emitted flat methods like `ReadTransactionLogAsync(stream)`. These were consolidated into the builder to reduce API surface:

| Old Flat Method | Modern Replacement |
|:---|:---|
| `ReadTransactionLogAsync(stream)` | `TransactionLogParquet.From(stream).ToArrayAsync()` |
| `ReadTransactionLogAsync(stream, options)` | `TransactionLogParquet.From(stream, options).ToArrayAsync()` |
| `StreamTransactionLogAsync(stream)` | `TransactionLogParquet.From(stream).AsAsyncEnumerable()` |
| `ReadTransactionLogParallelAsync(buffer)` | `TransactionLogParquet.From(buffer).Parallel().ToArrayAsync()` |

---

## 5. Architectural Background: The Read Grid

Reads represent a cross-product of four distinct dimensions:
1. **Source:** `Stream` vs `ReadOnlyMemory<byte>`
2. **Shape:** Array (`T[]`), Streaming (`IAsyncEnumerable<T>`), or Chunk (`<Model>Batch`)
3. **Execution:** Sequential vs Multi-core Parallel
4. **Pushdown:** Full scan vs Predicate Filter

Rather than emitting $2 \times 3 \times 2 \times 2 = 24$ permutations of `Read*` methods per model, the fluent builder exposes these dimensions as composable operations while keeping the consumer API compact and predictable.
