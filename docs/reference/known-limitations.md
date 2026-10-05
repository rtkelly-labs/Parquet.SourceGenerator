# Known Limitations & Upstream Constraints

This document provides an authoritative overview of active technical constraints, platform boundaries, and architectural trade-offs in `Parquet.SourceGenerator`.

Unlike closed historical bug logs, this guide details **current observed behavior** to help developers configure their projects and select appropriate types.

---

## 1. Platform & Runtime Constraints

### 1.1 IronCompress Missing 32-bit Windows (`win-x86`) Runtime
Underlying compression (Snappy, Gzip, Zstd) relies on `IronCompress`, which packages native binaries for:
- `win-x64`
- `linux-x64`
- `linux-arm64`
- `osx-arm64`

> ⚠️ **Limitation:** There is no native `win-x86` binary provided upstream. 32-bit processes on Windows (such as legacy IIS worker processes running under 32-bit mode or 32-bit .NET Framework apps) will fail with a `DllNotFoundException` when attempting to compress or decompress Parquet data. Use 64-bit processes or `CompressionMethod.None`.

### 1.2 `packages.config` Incompatibility with Native RID Assets
Legacy .NET Framework projects using `packages.config` do not resolve NuGet `runtimes/` RID-specific native binaries automatically. Consuming projects targeting .NET Framework 4.7.2+ must migrate to `<PackageReference>` in their project file to enable runtime native asset resolution.

### 1.3 .NET Framework 4.7.2 Language Version (`LangVersion`)
The source generator emits modern, performant C# patterns (including switch expressions, `??=`, pattern matching, and nullable reference annotations). 

Because the default `LangVersion` for `net472` projects is C# 7.3, consuming projects targeting .NET Framework 4.7.2 must explicitly configure:

```xml
<PropertyGroup>
  <LangVersion>10</LangVersion> <!-- or latest -->
</PropertyGroup>
```

---

## 2. Model Structure & C# Type Boundaries

### 2.1 Parameterless Constructors Required (`PARQ008`)
The generator materializes records and classes using direct property initialization:

```csharp
// Emitted materialization pattern
new UserRecord { Id = id, Name = name };
```

Consequently, the target type must have an accessible parameterless constructor. Positional records with primary constructors that do not declare an explicit parameterless constructor will trigger compiler diagnostic `PARQ008`:

```csharp
// ❌ Rejected by PARQ008 (no parameterless constructor)
[ParquetSerializable]
public partial record User(int Id, string Name);

// ✅ Supported: explicit parameterless constructor provided
[ParquetSerializable]
public partial record User(int Id, string Name)
{
    public User() : this(0, string.Empty) { }
}
```

### 2.2 Generic Models and Nested Types (`PARQ009`, `PARQ010`)
- **Nested Types (`PARQ009`):** Target types declared inside another class are rejected at compile time. Move the model to namespace scope.
- **Generic Models (`PARQ010`):** Generic types (`Record<T>`) cannot be serialized. The emitted Parquet schema is generated as a `static readonly` descriptor per type, which cannot vary by generic argument.

### 2.3 List Nesting Depth Boundaries
Collections (`List<T>`) are fully supported for 1-level list repetition compliant with the standard Apache Parquet 3-level list specification (`list.element`). 

However, multi-level nested collections (e.g. `List<List<T>>` or lists of complex structs containing further nested lists) are not currently supported by the zero-allocation emission engine and are rejected at compile time.

### 2.4 Unsupported CLR Types (`PARQ006`)
The following types do not have a standard or lossless representation in Parquet and trigger `PARQ006`:
- **`System.DateTimeOffset`**: Parquet has no native offset representation. Store as UTC `System.DateTime` with an explicit offset column if required.
- **`char`**: Represent as `string`.
- **Multi-dimensional or Jagged Arrays**: Arbitrary multidimensional arrays (e.g. `int[,]`) are not supported (use single-dimensional byte arrays `byte[]` or `List<T>`).
- **Arbitrary Complex Objects**: Unannotated reference objects without schema mapping.

See the [Compatibility Matrix](./compatibility-matrix.md) for the exhaustive 23-type supported list.

---

## 3. Concurrency & Execution Constraints

### 3.1 Parallel Reads Require Random-Access Memory (`ReadOnlyMemory<byte>`)
The fluent reader provides parallel row group decoding via:

```csharp
await <Model>Parquet.From(memory).ToArrayAsync(options);
```

This distributes decompression and decoding across worker threads using disjoint index offsets.

However, when reading from a sequential `System.IO.Stream`, parallel execution is disabled because a single stream pointer cannot be concurrently repositioned or read safely across multiple threads. Stream reads are executed sequentially.

### 3.2 Classic (`Parquet.SourceGenerator.Legacy`) Backend Scope
The legacy backend targets `Parquet.Net` 4.25.0 and the older `DataColumn` API. Key behavioral differences:
- **No Streaming Readers**: `AsAsyncEnumerable()` and `AsBatches()` are not available on the classic backend.
- **Allocation Profile**: `DataColumn` allocates intermediate managed arrays internally, bypassing the zero-allocation `ArrayPool.Shared` advantages of the modern 6.x backend.
- **String Length Validation**: In the classic backend, `MaxStringLengthBytes` validates strings after `ReadColumnAsync` completes rather than rejecting oversized strings prior to managed string allocation.
