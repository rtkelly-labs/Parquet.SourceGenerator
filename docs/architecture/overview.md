# Architectural Vision & Design Tenets

## Executive Summary

`Parquet.SourceGenerator` is a high-performance, compile-time Roslyn source generator for C# targeting the [Parquet.Net](https://github.com/aloneguid/parquet-dotnet) ecosystem. Its primary objective is to replace reflection-based object serialization and schema discovery with zero-allocation, strongly-typed compile-time generated column reader/writer implementations.

---

## 1. The Problem: Reflection Bottlenecks in Data Pipelines

The default high-level API in `Parquet.Net` (`ParquetConvert.SerializeAsync<T>` and `ParquetConvert.DeserializeAsync<T>`) relies on runtime reflection:
1. **Property Scanning**: At runtime, `ParquetConvert` uses `typeof(T).GetProperties()` to discover fields, inspect attributes, and map C# types to Parquet `DataField`s.
2. **Dynamic Value Dereferencing**: Reading and writing property values requires invoking `PropertyInfo.GetValue` and `PropertyInfo.SetValue`, resulting in boxing overhead for primitive types (`int`, `double`, `DateTime`, etc.).
3. **Array Allocations & Transposition**: High-throughput Parquet storage relies on column-oriented batching. Converting a row-oriented collection (`List<T>`) into column arrays via reflection causes significant GC pressure and CPU cache misses.
4. **Native AOT & Trimming Incompatibility**: Reflection-heavy serialization breaks when compiling applications with `PublishAot=true` or trimming enabled in .NET 8/9+, as the linker cannot statically trace dynamic property access.

---

## 2. The Solution: Compile-Time Code Generation

`Parquet.SourceGenerator` leverages Roslyn `IIncrementalGenerator` APIs to inspect annotated classes, records, and structs during compilation and generate dedicated serializer extensions:

```text
+-------------------+      Roslyn Incremental      +--------------------------------+
| User POCO / Record| ---- Generator Pipeline ---> | Generated Extension Methods    |
| [ParquetClass]    |                              | - Schema Discovery             |
+-------------------+                              | - Column Batch Writer          |
                                                   | - Fluent Column Batch Reader   |
                                                   +--------------------------------+
```

### Key Benefits
- **Zero Reflection at Runtime**: Property access and column array mapping are hardcoded by the compiler.
- **Ultra-High Throughput**: Directly transposes primitive arrays between memory buffers and Parquet `DataColumn` instances.
- **Minimal GC Allocations**: Eliminates object boxing and intermediate dynamic objects via progressive `ArrayPool.Shared` recycling.
- **Native AOT & Trimmer Safe**: Fully deterministic compile-time C# code with zero dynamic code emission (`Reflection.Emit`).
- **Compile-Time Diagnostics**: Emits Roslyn compiler errors and warnings (`PARQ001` to `PARQ016`, catalogued in [Compiler Diagnostics Reference](../reference/compiler-diagnostics.md)) if an unsupported type or invalid attribute configuration is used.

---

## 3. Core Design Tenets: The Member Test & Visibility Tiers

To keep the durable public surface minimal while allowing deep internal optimizations, all API additions are governed by two foundational principles:

### The Member Test
Every candidate public member must pass one question:

> **Does this member describe user intent, or an implementation strategy?**

The stable surface answers user questions: How do I annotate a type? How do I read, stream, or filter rows? How do I write rows or columnar arrays? How do I configure codec and resource limits?

It does not expose implementation strategies: which reader state type is active, which emitter slot a column occupies, which pooled array backs a batch, or which SIMD helper performs a transform. Implementation strategy stays internal.

### Three Visibility Tiers

| Tier | What Lives There | Governed By |
|:---|:---|:---|
| **Stable Public Contract** | Attributes, `ParquetSerializerOptions`, core read/write entry points, and `<Model>Batch` with borrowed lifetime semantics | Derived `*.api.txt` review, `PublicAPI.*.txt`, [API Ledger](../api/LEDGER.md) |
| **Internal Generated Capability** | Generated into consumer assemblies but not exported: low-level row-group writers, conversion helpers, pruning instrumentation | Golden `.g.cs` review diffs |
| **Repository Implementation Detail** | Parser models, emitters, planning types, SIMD helpers, test adapters, benchmark helpers | `internal` + `InternalsVisibleTo` |

---

## 4. Architecture Pipeline Overview

```text
Syntax/Symbol Discovery
        ↓
TargetParser (SyntaxProvider, equatable models)
        ↓
Backend-Neutral Compile-Time Model (internal)
        ↓
EmissionPlan (computed once per model)
        ↓
Capability-Focused Emitters
        ↓
Generated Source (*.g.cs)
```

- Parser outputs are value-equatable for Roslyn caching.
- `EmissionPlan` is computed once per model, not reconstructed inside emitter templates.
- Modern and legacy backends share common planning models while targeting specialized column I/O engines.
- For complete pipeline details, see [Roslyn Pipeline Architecture](roslyn-pipeline.md).
