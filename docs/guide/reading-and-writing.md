---
description: "Writing, reading, parallel and streaming reads, columnar batches, row-group pruning and options."
order: 30
---

# Reading & Writing

Every example assumes a `[ParquetSerializable]` model such as `UserEvent` from
[Getting Started](./getting-started.md). The full generated surface, and why it is shaped this way,
is in [Public API Surface](../reference/api-surface.md).

## Writing

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

### Writing from data that is already columnar

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
[Buffer Reuse & Column Extraction](../internals/buffer-reuse.md#-6-direct-columnar-handoff--measured-issue-137).
Models with struct, list or map members keep the row-oriented API only.

## Reading

```csharp
using var stream = File.OpenRead("events.parquet");

// Sequential read
List<UserEvent> events = await UserEventParquet.From(stream).ToListAsync();

// Multi-core parallel read over an in-memory byte buffer
ReadOnlyMemory<byte> buffer = File.ReadAllBytes("events.parquet");
List<UserEvent> fast = await UserEventParquet
    .From(buffer)
    .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 8 })
    .Parallel()
    .ToListAsync();

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

`<Model>Parquet.From(...)` is the only generated read entry point: the source (`Stream` or
`ReadOnlyMemory<byte>`), the execution (`.Parallel()`, buffer only), pushdown (`.Where(...)`) and
options (`.WithOptions(...)`) are members of the builder, and the terminal (`ToListAsync`,
`ToArrayAsync`, `AsAsyncEnumerable`, `Batches`) picks the shape. The flat `ReadParquet*Async` methods
were removed before `0.1.0`; the mapping is in [CHANGELOG.md](../../CHANGELOG.md) and the decision in
[Flat-Read Removal](../design/flat-read-removal.md). The `Parquet.SourceGenerator.Legacy` package has no
builder and keeps its flat `ReadParquetAsync` / `ReadParquetArrayAsync`.

`Batches()` is emitted for flat models only (no nested structs, lists or maps) and
allocates no domain objects; the pooled column buffers are returned when the enumerator advances
or is disposed, so nothing in a batch may outlive the loop body.

## Row-group pruning with min/max statistics

`.Where(...)` on the read builder takes a predicate over the statistics Parquet records in the file
footer, from either source and for every materializing or streaming shape. A row group the zone map rules out is never opened: no page read, no decompression,
no buffer rental.

```csharp
// Only the row groups whose [min, max] range can still hold a key >= 1000 are read.
List<OrderEvent> recent = await OrderEventParquet
    .From(stream)
    .Where(meta => meta.OrderKey.MayContainAtLeast(1_000))
    .ToListAsync();

// Conjunctive filters compose; any column that cannot match prunes the whole group.
List<OrderEvent> narrow = await OrderEventParquet
    .From(stream)
    .Where(meta => meta.OrderKey.MayContainBetween(1_000, 2_000)
                && meta.Region.MayContain("emea"))
    .ToListAsync();
```

The generated `<Model>RowGroupMetadata` struct exposes `RowGroupIndex`, `RowCount` and one
`ParquetColumnStatistics<T>` per integral, floating-point or string column, carrying `Min`, `Max`,
`NullCount`, `DistinctCount` and the `May*` range helpers. Only the generated reader constructs it
(its constructor is `internal`); a predicate just reads it. Pruning is conservative: a row group
whose statistics are incomplete is always read.

## Options (`ParquetSerializerOptions`)

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
