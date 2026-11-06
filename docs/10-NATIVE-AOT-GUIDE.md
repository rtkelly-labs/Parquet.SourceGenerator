# 10 - Native AOT & Type System Guide: Mechanics, Nullable<T>, and Runtime Directives

This document provides a comprehensive technical reference for compiling and running **Parquet.SourceGenerator** applications under **.NET Native AOT (Ahead-of-Time)** compilation. It details the complete supported types matrix, analyzes the underlying CoreCLR/ILCompiler type system mechanics for `Nullable<T>` and generic value types, investigates the architectural shift in **Parquet.Net 6.1.0**, and outlines resolution strategies using Runtime Directives (`rd.xml`) and MSBuild targets.

---

## 🎯 Executive Summary & Motivation

Native Ahead-of-Time (Native AOT) compilation in .NET 8+ compiles C# assemblies directly into self-contained native machine code (ELF on Linux, Mach-O on macOS, PE/COFF on Windows). Native AOT eliminates the Just-In-Time (JIT) compiler, the IL interpreter, and reflection metadata that is not statically proven to be reachable at build time.

### Why Native AOT for Parquet Processing?
1. **Instantaneous Startup**: Command-line utilities (CLIs), AWS Lambda / serverless functions, and batch data extraction jobs start executing within milliseconds without JIT warmup overhead.
2. **Minimal Working Set**: Memory footprint is strictly bounded; metadata structures, intermediate language (IL), and unused framework methods are stripped during tree-shaking (trimming).
3. **Deterministic Performance**: No runtime JIT pauses, tiered compilation transitions, or dynamic code de-optimizations occur during high-throughput columnar ingestion.

### The Source Generator Advantage
Traditional serialization in `Parquet.Net` (`ParquetSerializer`) relies on runtime reflection, property getter/setter delegates, and dynamically generated expressions. Under Native AOT, reflection paths either throw runtime exceptions (`MissingMetadataException`, `NotSupportedException`) or require extensive trimmer configuration. 

`Parquet.SourceGenerator` completely bypasses reflection by emitting compile-time, strongly typed column extraction, buffering, and conversion routines directly into the consumer's assembly.

---

## ⚡ 2. The "Naive Case": Cold-Start & Short-Lived Process Performance

Standard microbenchmarks (such as BenchmarkDotNet) execute tens of thousands of warmup iterations. In long-running tight loops, JIT compilation cost is amortized to near zero, and RyuJIT's steady-state machine code is nearly identical to Native AOT.

However, in **naive, real-world invocations**—such as CLI tools, serverless functions (AWS Lambda, Azure Functions), and ephemeral batch jobs—the process runs only once. Here, the overhead of JIT compilation and runtime initialization dominates execution.

### Empirical Cold-Start Benchmark: CoreCLR (JIT) vs Native AOT

We measured cold single-shot execution across the complete `Parquet.SourceGenerator.AotTest` regression suite (exercising all 11 round-trip serialization paths, compression codecs, and schema types) under **CoreCLR (JIT)** vs **Native AOT (Mach-O ARM64)** on macOS:

| Metric | CoreCLR (JIT) | Native AOT | Improvement with Native AOT |
| :--- | :---: | :---: | :---: |
| **Total Wall-Clock Latency** | **358 ms** | **50 ms** | ⚡ **7.2× faster process time** |
| **CPU User Time (Execution + Compile)** | **0.30 s** | **0.02 s** | ⚡ **15× less CPU time** |
| **Peak Working Set (Max RSS Memory)** | **57.0 MB** | **15.8 MB** | 📉 **72% less memory (3.6× reduction)** |

### Architectural Reasons for the 7.2× Acceleration

1. **JIT Compilation Overhead Elimination**:
   - `Parquet.SourceGenerator` emits ~15 specialized methods per model (`WriteParquetRowGroupAsync`, `ReadParquetParallelAsync`, `WriteParquetBatchedAsync`, etc.).
   - In CoreCLR, RyuJIT must compile every method from CIL bytecode into native machine code upon its first call. Compiling the serializer surface consumes **~280 ms of pure CPU time** before writing a single byte.
   - Under Native AOT, `ILCompiler` performs all code generation ahead of time at build time. The CPU begins executing native machine code within **2 milliseconds** of process launch.

2. **Static Generic Dictionaries & EETypes**:
   - Types like `ArrayPool<int>.Shared`, `ReadOnlyMemory<byte>`, and `List<T>` require CoreCLR to dynamically construct `MethodTable` and `EEType` metadata structures on the fly.
   - In Native AOT, all type descriptors and interface dispatch tables are statically laid out in the read-only data segment (`__DATA` / `__TEXT`) and mapped directly into memory (`mmap`).

3. **Runtime Footprint Reduction**:
   - CoreCLR must load the JIT compiler engine (`clrjit`), IL verification tables, tiered compilation state machines, and dynamic symbol tables.
   - Native AOT strips the entire runtime down to a lightweight garbage collector and static entry points, yielding a **3.6× reduction in resident RAM** (15.8 MB vs 57.0 MB).

---

## 📊 Complete Supported Types Matrix

The following matrix documents all types supported by `Parquet.SourceGenerator`, their CLR categorization, Parquet physical/logical schema representations, and Native AOT behavior:

| C# Type | CLR Category | Parquet Physical Type | Parquet Logical Annotation | Nullable? | Native AOT Code Sharing |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `int` | Value Type (`struct`) | `INT32` | None | `int?` | Specialized Machine Code |
| `long` | Value Type (`struct`) | `INT64` | None | `long?` | Specialized Machine Code |
| `float` | Value Type (`struct`) | `FLOAT` | None | `float?` | Specialized Machine Code |
| `double` | Value Type (`struct`) | `DOUBLE` | None | `double?` | Specialized Machine Code |
| `bool` | Value Type (`struct`) | `BOOLEAN` | None | `bool?` | Specialized Machine Code |
| `byte` / `sbyte` | Value Type (`struct`) | `INT32` | `INT(8, signed/unsigned)` | `byte?` / `sbyte?` | Specialized Machine Code |
| `short` / `ushort` | Value Type (`struct`) | `INT32` | `INT(16, signed/unsigned)` | `short?` / `ushort?` | Specialized Machine Code |
| `uint` | Value Type (`struct`) | `INT32` | `INT(32, unsigned)` | `uint?` | Specialized Machine Code |
| `ulong` | Value Type (`struct`) | `INT64` | `INT(64, unsigned)` | `ulong?` | Specialized Machine Code |
| `decimal` | Value Type (`struct`) | `INT32` / `INT64` / `FIXED_LEN_BYTE_ARRAY` | `DECIMAL(precision, scale)` | `decimal?` | Specialized Machine Code |
| `DateTime` | Value Type (`struct`) | `INT64` / `INT96` | `TIMESTAMP(MILLIS / MICROS)` | `DateTime?` | Specialized Machine Code |
| `TimeSpan` | Value Type (`struct`) | `INT32` | `TIME(MILLIS)` | `TimeSpan?` | Specialized Machine Code |
| `TimeOnly` | Value Type (`struct`) | `INT64` | `TIME(MICROS)` | `TimeOnly?` | Specialized Machine Code |
| `DateOnly` | Value Type (`struct`) | `INT32` | `DATE` | `DateOnly?` | Specialized Machine Code |
| `Guid` | Value Type (`struct`) | `FIXED_LEN_BYTE_ARRAY(16)` | `UUID` | `Guid?` | Specialized Machine Code |
| `enum` (underlying int/short/etc.) | Value Type (`struct`) | `INT32` (or underlying) | None | `Enum?` | Specialized Machine Code |
| `string` | Reference Type (`class`) | `BYTE_ARRAY` | `STRING` (UTF-8) | `string?` | Canonical (`__Canon`) / Dynamic Native Root |
| `byte[]` | Reference Type (`class`) | `BYTE_ARRAY` | None | `byte[]?` | Canonical (`__Canon`) / Dynamic Native Root |
| Blittable Structs | Value Type (`struct`) | Contiguous Primitive Columns | Matches member types | No | Specialized Machine Code |

---

## 🔬 Deep Dive: The Mechanics of `Nullable<T>` Under Native AOT

To understand why Native AOT encounters runtime failures with certain nullable types, we must examine the fundamental differences between **Reference Types** and **Value Types** in the .NET AOT runtime (ILCompiler / CoreCLR).

### 1. `Nullable<T>` is a Value Type Struct
In C#, `T?` for value types is syntax sugar for the generic struct:
```csharp
public struct Nullable<T> where T : struct
{
    private readonly bool hasValue;
    internal T value;
    
    public bool HasValue => hasValue;
    public T Value => hasValue ? value : throw new InvalidOperationException();
}
```
Because `Nullable<T>` is a `struct`, its in-memory size and layout directly depend on `sizeof(T)` and memory alignment rules:
- `Nullable<int>`: 4 bytes (`int`) + 1 byte (`bool`) + 3 bytes padding = **8 bytes**.
- `Nullable<Guid>`: 16 bytes (`Guid`) + 1 byte (`bool`) + 7 bytes padding = **24 bytes**.
- `Nullable<ReadOnlyMemory<char>>`: 16 bytes (`ReadOnlyMemory`) + 1 byte (`bool`) + 7 bytes padding = **24 bytes**.

### 2. Canonical Code Sharing vs Non-Shared Value Types
When compiling generic types, runtime engines optimize binary footprint using code sharing:

#### Reference Types (`class`): Canonical Code Sharing (`__Canon`)
All object references in .NET are identical in pointer size (8 bytes on 64-bit platforms) and garbage collector tracing semantics. The AOT compiler compiles a single shared generic implementation (canonical representation `__Canon`). A method like `List<__Canon>.Add(__Canon item)` serves `List<string>`, `List<MyCustomClass>`, and every other class type. Thus, dynamically creating `typeof(List<>).MakeGenericType(typeof(MyClass))` at runtime can reuse the pre-compiled canonical machine code.

#### Value Types (`struct`): Zero Code Sharing
Value types **cannot share canonical code**:
- Each struct has unique register-passing conventions (ABI) in hardware (e.g. integer registers `RDI`/`RSI`/`RDX` vs SSE vector registers `XMM0` vs stack passing).
- Each struct requires a unique garbage collection reference tracking map (does the struct contain managed pointers like `string` or object references?).
- Each struct has a unique size and struct alignment.

> [!IMPORTANT]
> Because there is no JIT compiler present in a Native AOT binary, the runtime **cannot compile new machine code or construct new MethodTables/EETypes for specialized value types at runtime**.
> If code calls `Type.MakeGenericType(typeof(TValueType))` dynamically, the runtime can only succeed if specialized native code and metadata for `Nullable<TValueType>` was already emitted during build-time compilation.

### 3. The Failure Mechanism: `TypeUnifier.WithVerifiedTypeHandle`
When reflection code executes:
```csharp
Type nullableType = typeof(Nullable<>).MakeGenericType(targetType);
```
Under Native AOT, CoreCLR's reflection engine enters:
```
System.Reflection.Runtime.General.TypeUnifier.WithVerifiedTypeHandle(
    RuntimeConstructedGenericTypeInfo genericType, 
    RuntimeTypeInfo[] typeArguments)
```
If `targetType` is a value type whose `Nullable<targetType>` was not statically compiled into the executable binary's type dictionary, the runtime has no native entry point or EEType layout. It terminates with:
```
System.NotSupportedException: 'System.Nullable`1[TargetType]' is missing native code or metadata.
This can happen for code that is not compatible with trimming or AOT. 
Inspect and fix trimming and AOT related warnings that were generated when the app was published.
```

---

## 💥 The Parquet.Net 6.1.0 Architectural Shift & Root Cause

In `Parquet.Net 6.0.3`, column representations for text and binary were mapped directly to .NET reference types:
- String columns: `typeof(string)` (Reference Type / Class)
- Binary columns: `typeof(byte[])` (Reference Type / Class)

Because `string` and `byte[]` are classes, checking nullability never triggered `Nullable<T>` construction. A nullable string in C# is simply a null object reference.

### What Changed in Parquet.Net 6.1.0?
In Parquet.Net 6.1.0 ([aloneguid/parquet-dotnet#755](https://github.com/aloneguid/parquet-dotnet/issues/755) and related commits), the library refactored internal buffer representations to use `System.ReadOnlyMemory<T>` for zero-copy slicing:
- String column data fields: `typeof(ReadOnlyMemory<char>)`
- Binary column data fields: `typeof(ReadOnlyMemory<byte>)`

**`System.ReadOnlyMemory<T>` is a struct (Value Type)!**

### The Reflection Landmine in `DataField.set_IsNullable`
Inside `Parquet.Schema.DataField`:
```csharp
public bool IsNullable
{
    get => _isNullable;
    set
    {
        _isNullable = value;
        ClrNullableIfHasNullsType = value && ClrType.IsValueType && Nullable.GetUnderlyingType(ClrType) == null
            ? typeof(Nullable<>).MakeGenericType(ClrType)
            : ClrType;
    }
}
```
When `DataField` is constructed for a string column with `isNullable: true`:
1. `ClrType` is set to `typeof(ReadOnlyMemory<char>)`.
2. `isNullable` is `true`.
3. `ClrType.IsValueType` is `true` (because `ReadOnlyMemory<char>` is a struct).
4. `DataField` executes: `typeof(Nullable<>).MakeGenericType(typeof(ReadOnlyMemory<char>))`.
5. For binary columns, it executes: `typeof(Nullable<>).MakeGenericType(typeof(ReadOnlyMemory<byte>))`.

Because `Nullable<ReadOnlyMemory<char>>` and `Nullable<ReadOnlyMemory<byte>>` are never instantiated in standard consumer C# code, the Native AOT trimmer does not compile native code for them. When `DataField` executes during static schema initialization or thrift footer parsing, the application crashes immediately with `System.NotSupportedException`.

---

## 🛠️ Resolution Strategies & Best Practices

To enable 100% Native AOT compatibility with Parquet.Net 6.1.0+, the Native AOT compiler must be instructed to compile the specialized machine code and metadata for these generic value types.

### Strategy 1: Runtime Directives (`rd.xml`) — Recommended for Applications
Add a Runtime Directives (`rd.xml`) file to your application project. This file explicitly directs the AOT compiler (`ilc`) to generate required metadata and dynamic code instantiations.

Create `rd.xml`:
```xml
<Directives xmlns="http://schemas.microsoft.com/netfx/2013/01/metadata">
  <Application>
    <Assembly Name="System.Private.CoreLib">
      <!-- Preserve Nullable value types required dynamically by Parquet.Net 6.1.0 -->
      <Type Name="System.Nullable`1[[System.ReadOnlyMemory`1[[System.Char, System.Private.CoreLib]], System.Private.CoreLib]]" Dynamic="Required All" />
      <Type Name="System.Nullable`1[[System.ReadOnlyMemory`1[[System.Byte, System.Private.CoreLib]], System.Private.CoreLib]]" Dynamic="Required All" />
      <Type Name="System.Nullable`1[[System.TimeOnly, System.Private.CoreLib]]" Dynamic="Required All" />
      <Type Name="System.Nullable`1[[System.DateOnly, System.Private.CoreLib]]" Dynamic="Required All" />
    </Assembly>
  </Application>
</Directives>
```

In your `.csproj`:
```xml
<ItemGroup>
    <RdXmlFile Include="rd.xml" />
</ItemGroup>
```

### Strategy 2: Trimmer Root Descriptors (`ILLink.Descriptors.xml`)
Alternatively, Trimmer Root Descriptors can preserve types during the trimming phase:
```xml
<linker>
  <assembly fullname="System.Private.CoreLib">
    <type fullname="System.Nullable`1[[System.ReadOnlyMemory`1[[System.Char, System.Private.CoreLib]], System.Private.CoreLib]]" preserve="all" />
    <type fullname="System.Nullable`1[[System.ReadOnlyMemory`1[[System.Byte, System.Private.CoreLib]], System.Private.CoreLib]]" preserve="all" />
  </assembly>
</linker>
```

In your `.csproj`:
```xml
<ItemGroup>
    <TrimmerRootDescriptor Include="ILLink.Descriptors.xml" />
</ItemGroup>
```

### Strategy 3: Automatic NuGet Package-Level MSBuild Targets Injection
For a zero-configuration consumer experience, the `Parquet.SourceGenerator` NuGet package can automatically deliver these runtime directives via standard MSBuild package conventions:
- Place `Parquet.SourceGenerator.targets` in the package's `build/` directory.
- The targets file automatically includes `rd.xml` whenever `<PublishAot>true</PublishAot>` is enabled in the consumer project:
```xml
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Condition="'$(PublishAot)' == 'true'">
    <RdXmlFile Include="$(MSBuildThisFileDirectory)..\content\ParquetAotDirectives.rd.xml" />
  </ItemGroup>
</Project>
```
With this packaging pattern, consumers simply install `Parquet.SourceGenerator` and publish with `<PublishAot>true</PublishAot>` without manual XML configuration.

---

## 🧪 The Native AOT Verification Harness (`Parquet.SourceGenerator.AotTest`)

To guarantee that no future dependency update re-introduces runtime reflection failures, the repository maintains an end-to-end Native AOT test harness in `test/Parquet.SourceGenerator.AotTest`.

### Why a Standalone Console App Instead of xUnit?
Standard test runners (e.g. xUnit, NUnit) discover and invoke test methods via runtime reflection. Under Native AOT, a runner's own trimming issues cannot be distinguished from library serialization failures.

`Parquet.SourceGenerator.AotTest` is a standalone executable compiled natively (`dotnet publish -c Release`). It executes an explicit 11-point regression matrix:

```
=================================================
Native AOT regression matrix
=================================================
  PASS  schema construction (static, all kinds)
  PASS  all property kinds round-trip
  PASS  nullable kinds round-trip with nulls present
  PASS  [ParquetIgnore] member is not persisted
  PASS  microsecond timestamps keep sub-millisecond precision
  PASS  batched write produces multiple row groups
  PASS  parallel read across row groups
  PASS  read from ReadOnlyMemory<byte>
  PASS  IAsyncEnumerable streaming write
  PASS  schema field resolution by name (reordered columns)
  PASS  every compression codec round-trips
=================================================
All 11 AOT checks passed.
=================================================
```

### Running Native AOT Verification Locally
```bash
# Publish as native binary (macOS ARM64 example)
dotnet publish test/Parquet.SourceGenerator.AotTest/Parquet.SourceGenerator.AotTest.csproj -c Release

# Execute compiled native binary directly
./test/Parquet.SourceGenerator.AotTest/bin/Release/net8.0/osx-arm64/publish/Parquet.SourceGenerator.AotTest
```

This verification runs automatically in GitHub Actions CI on every pull request, ensuring zero regressions across all supported data types.
