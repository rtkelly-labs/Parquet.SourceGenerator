# 04 - Roadmap & Open Source Blueprint

## 🗺️ Project Roadmap

> Phases 1–5 shipped. Remaining work is tracked in
> [07 - Known Limitations](./07-KNOWN-LIMITATIONS.md), which records observed behaviour and a
> sequenced remediation plan; this section is the historical outline.

### Phase 1: Foundation & Infrastructure ✅
- [x] Fix SDK rollForward policy in `global.json`.
- [x] Establish project documentation & architectural blueprints in `docs/`.
- [x] Configure gitignore rules for local todo management.
- [x] Target `net8.0` / `net9.0`.
- [x] Take a dependency on `Parquet.Net` 6.x. (Superseded the original 4.x/5.x plan — v6 introduced
      the `Memory<T>` column API the emitter is built on. See limitations 1.2 for why supporting
      4.x/5.x is a second emitter rather than a version bump.)
- [x] Implement attribute marker definitions (`ParquetSerializableAttribute`, `ParquetColumnAttribute`, `ParquetIgnoreAttribute`).

### Phase 2: Primitive Types & Basic Code Generation ✅
- [x] Implement `ParquetIncrementalGenerator` Roslyn 4.0 pipeline.
- [x] Generate static `ParquetSchema` from decorated target types.
- [x] Generate zero-reflection `WriteParquetAsync` column array exporter for primitives.
- [x] Generate `ReadParquetAsync` column array importer for primitive types.
- [x] Add a unit test suite driving the generator through `CSharpGeneratorDriver`.

### Phase 3: Nullability, Complex Types & Diagnostics ✅
- [x] Support nullable primitives (`int?`, `DateTime?`, `double?`).
- [x] Honour nullable *reference* annotations, so `string` and `string?` differ in the schema.
- [x] Implement Roslyn compiler diagnostics `PARQ001`–`PARQ011`.
- [x] Support custom decimal precision/scale (`[ParquetDecimal]`) and timestamp units (`[ParquetTimestamp]`).
- [x] Collect inherited members from base types declared in source.
- [ ] Support nested collections (`List<T>`, arrays) and nested POCO structs (`StructField`).
      Currently rejected at compile time by `PARQ006` rather than failing at runtime.

### Phase 4: Native AOT, Performance Optimization & Benchmarking ✅
- [x] Create `Parquet.SourceGenerator.Benchmarks` using `BenchmarkDotNet`.
- [x] Benchmark serialization & deserialization against the reflection implementation.
- [x] Validate `PublishAot=true` — CI publishes and runs a native binary (`linux-x64` only).
- [x] Optimize allocations using `ArrayPool<T>` and pooled column buffers.

### Phase 5: Open Source Launch & CI/CD ✅
- [x] Setup GitHub Actions workflow for building, testing, and formatting.
- [x] Configure NuGet package metadata, licensing (MIT), icons, and README embedded docs.
- [x] Publish to NuGet.org (`0.0.1`).
- [ ] Cut a `1.0.0` release. Blocked on the API-shaping items in the limitations audit.

### Phase 6: Broader Runtime Support ✅
- [x] `Parquet.SourceGenerator.V5` — a `DataColumn`-based emitter covering Parquet.Net 4.x and 5.x,
      and with it `netstandard2.0` / net472 consumers. See limitations 1.1–1.3 and 6.
- [x] Genuine parallel reading, built on the buffer overloads. See limitations 2.1.

### Phase 7: Measurement
- [x] Benchmark regression detection. Runs compare against `benchmarks/baseline.json`; allocated
      bytes is the gate, wall-clock is reported only. See "Benchmark regression gate" below.
- [x] Benchmarks for the buffer read paths, including the genuinely parallel one — the allocation
      column is what would catch a per-worker buffer copy returning.
- [ ] **Run the benchmarks and commit the baseline.** Nothing has been measured yet: the gate
      exists but has no baseline to compare against, and two performance changes have shipped
      unmeasured — removing the per-row-group `List.Capacity` reallocation (limitations 2.2) and the
      parallel reader (limitations 2.1). The published README table predates both, so it describes a
      build that no longer exists. Until a run happens, both improvements are reasoned, not measured.
- [ ] Add a classic-backend row to the comparison. `DataColumn` allocates its own arrays, so the
      V5 package should not inherit the main package's allocation figures — and nobody has checked
      how far apart they are.

#### Benchmark regression gate

Dispatch `benchmarks.yml`. It runs BenchmarkDotNet, then compares the results against
`benchmarks/baseline.json`:

| Input | Effect |
|:---|:---|
| _(none)_ | Compare against the baseline. Fails on an allocation regression. |
| `update_baseline` | Record this run as the new baseline instead, for review in the generated PR. |
| `fail_on_time` | Also fail on wall-clock regressions. Only meaningful on a quiet machine. |

**Allocated bytes is the gate; wall-clock is reported but does not fail.** That split is deliberate.
Allocation counts on a fixed input are near-deterministic — the same code allocates the same bytes
on any machine — so a change in them is a real change in what the code does. Wall-clock on a shared
runner moves tens of percent between runs of identical code, and a gate nobody can act on is a gate
that ends up switched off.

The first run with no baseline records one and passes, rather than failing for the absence of a file
it is about to create. The comparison logic is unit-tested in `BenchmarkRegressionTests` — unit
normalisation, threshold arithmetic and baseline round-tripping are ordinary logic, and testing them
in the normal CI run means a broken gate surfaces there rather than the next time someone dispatches
the benchmark workflow.

---

## 🧪 Testing Strategy

Quality and correctness are verified across three testing tiers:

### 1. Source Generator Unit Tests (`Verify.SourceGenerators`)
Inspects generated C# code strings against verified snapshot outputs to prevent regressions when refactoring Roslyn code.

```csharp
[Fact]
public Task GeneratesCorrectSerializerForBasicPoco()
{
    string source = """
        using Parquet.SourceGenerator;
        
        [ParquetSerializable]
        public record Person(int Id, string Name);
        """;

    return TestHelpers.VerifyGenerator(source);
}
```

### 2. Integration Tests (`Parquet.Net` Compatibility)
Generates actual Parquet byte streams, writes them to disk/memory, reads them back using both `ParquetReader` and generated extensions, and asserts object field equality.

### 3. Native AOT Verification
Builds an AOT test console app with `<PublishAot>true</PublishAot>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to guarantee trim safety.

---

## 🤝 Contributing Guidelines

1. **Code Style**: Adhere to modern C# 12/13 conventions, file-scoped namespaces, nullable reference types (`#nullable enable`), and pattern matching.
2. **Roslyn Performance**: Never hold raw `ISymbol` or `SyntaxNode` references in incremental generator pipeline state. Always pass value-equatable record models.
3. **Commit Workflow**: Run `dotnet test` locally before submitting pull requests. Ensure all snapshot tests pass.
