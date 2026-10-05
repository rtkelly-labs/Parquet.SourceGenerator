# Vision & Design Tenets

`Parquet.SourceGenerator` is a compile-time Roslyn source generator for C# targeting the [Parquet.Net](https://github.com/aloneguid/parquet-dotnet) ecosystem. Its primary objective is to replace reflection-based object serialization and runtime schema discovery with zero-allocation, strongly-typed compile-time column reader and writer implementations.

---

## 1. The Problem: Reflection Bottlenecks in Data Pipelines

The standard high-level API in `Parquet.Net` (`ParquetConvert.SerializeAsync<T>` and `ParquetConvert.DeserializeAsync<T>`) relies on runtime reflection:

1. **Runtime Property Scanning**: At runtime, `ParquetConvert` uses `typeof(T).GetProperties()` to discover fields, inspect attributes, and construct `DataField` schema representations.
2. **Dynamic Value Dereferencing**: Reading and writing property values requires invoking `PropertyInfo.GetValue` and `PropertyInfo.SetValue`, causing boxing overhead for every primitive value (`int`, `double`, `DateTime`, etc.).
3. **Array Allocations & Transposition**: High-throughput Parquet storage relies on column-oriented batching. Converting a row-oriented collection (`List<T>`) into columnar arrays via reflection forces severe GC pressure and cache thrashing.
4. **Native AOT & Trimming Incompatibility**: Reflection-heavy serialization fails when publishing with `PublishAot=true` or trimming enabled in modern .NET (.NET 8/9/10+), because trimming linkers cannot trace dynamic property access.

---

## 2. The Solution: Compile-Time Code Generation

`Parquet.SourceGenerator` leverages Roslyn's `IIncrementalGenerator` API to inspect annotated types (`[ParquetSerializable]`) during compilation and emit dedicated serializer extensions:

```text
+-------------------+      Roslyn Incremental      +--------------------------------+
| User POCO / Record| ---- Generator Pipeline ---> | Generated Extension Methods    |
| [ParquetClass]    |                              | - Schema Discovery             |
+-------------------+                              | - Column Batch Writer          |
                                                   | - Fluent Column Batch Reader   |
                                                   +--------------------------------+
```

### Key Capabilities & Benefits

- **Zero Reflection at Runtime**: Member access and column array mappings are emitted as plain C# code at compile time.
- **High Throughput**: Directly transposes primitive arrays between memory buffers and Parquet `DataColumn` instances.
- **Minimal Allocations**: Eliminates object boxing and intermediate collection allocations through disciplined `ArrayPool.Shared` buffer recycling.
- **Full Native AOT & Trimming Support**: Fully deterministic compile-time C# code with zero dynamic code emission (`Reflection.Emit`).
- **Compile-Time Diagnostics**: Emits Roslyn compiler errors and warnings (`PARQ001` to `PARQ016`, catalogued in [Compiler Diagnostics Reference](reference/compiler-diagnostics.md)) whenever unsupported types or invalid attributes are encountered.

---

## 3. Core Design Tenets

To ensure the public API remains durable and ergonomic while enabling deep internal optimization, all design decisions are guided by two tenets:

### The Member Test

Every candidate public member must pass one essential question:

> **Does this member describe user intent, or an implementation strategy?**

The stable public surface answers user questions:
- *How do I annotate a type?* (`[ParquetColumn]`, `[ParquetTimestamp]`)
- *How do I read, stream, or filter rows?* (`<Model>Parquet.From(source).Where(...).ToArrayAsync()`)
- *How do I write rows or columnar arrays?* (`<Model>Parquet.WriteAsync(stream, items)`)
- *How do I configure serialization limits and codecs?* (`ParquetSerializerOptions`)

It never exposes internal strategies: which reader state type is active, which emitter slot a column occupies, which pooled array backs a batch, or which SIMD helper performs a transform. Implementation strategy stays internal.

### Three Visibility Tiers

| Tier | Scope | Governed By |
|:---|:---|:---|
| **Stable Public Contract** | Attributes, `ParquetSerializerOptions`, fluent read builders, write entry points, and `<Model>Batch` | Derived `*.api.txt` PR diffs, `PublicAPI.*.txt`, and [API Governance](architecture/api-governance.md) |
| **Internal Generated Capability** | Generated into consumer assemblies as `internal`: low-level row-group writers, conversion helpers, pruning lookups | Golden `.g.cs` review diffs |
| **Repository Implementation Detail** | Parser models, emitters, planning types, SIMD helpers, test adapters | `internal` + `InternalsVisibleTo` |

---

## 4. Architectural Deep Dives

To explore the mechanics behind this vision, see:

- [System Architecture Overview](architecture/overview.md) — The subsystem pipeline, emitter components, and dual-backend topology.
- [Roslyn Incremental Pipeline](architecture/roslyn-pipeline.md) — How the generator achieves fine-grained Roslyn caching.
- [Memory & Buffer Management](architecture/memory-and-performance.md) — Buffer rental, pinned arrays, and zero-allocation columnar writes.
- [API Governance & Ledger](architecture/api-governance.md) — The strict change contract governing public and internal API surfaces.
- [Architecture Decision Records (ADRs)](architecture/adrs/0001-generator-tooling-evaluation.md) — Architectural trade-off records and peer studies.
