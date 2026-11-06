# 08 - IL Interrogation & Codegen Performance Verification

This guide outlines the workflow and tooling used in **Parquet.SourceGenerator** to interrogate emitted Intermediate Language (IL), decompile generated C# code, and verify low-level runtime compiler optimizations.

---

## 🎯 Motivation

`Parquet.SourceGenerator` emits high-throughput column readers, writers, and string deduplicators at compile time. Reviewing C# source alone (e.g. via `<EmitCompilerGeneratedFiles>`) is insufficient to confirm whether runtime performance goals are met:

1. **Boxing Detection (`box`)**: Value types (`struct`), nullable primitives, or enums accidentally boxed onto the managed heap during column transfers generate GC pressure.
2. **Devirtualization (`callvirt` vs `call`)**: Methods on hot paths should ideally be non-virtual to enable aggressive JIT inlining and omit vtable lookups.
3. **Bounds Check Elimination (BCE)**: Span access and `MemoryMarshal.Cast` operations must avoid emitting array bounds checks and throw-blocks in tight inner loops.
4. **Regression Diffing**: Maintainers can compare the emitted IL between commits or PR branches to detect unexpected codegen regressions or bloat.

---

## 🛠️ Tooling Overview

The project integrates CLI decompilation and inspection tools via `.config/dotnet-tools.json`:

- **[ilspycmd](https://github.com/icsharpcode/ILSpy)**: Command-line decompiler engine from ILSpy. Produces both C# decompilation and raw IL disassembly (`-il`).
- **[dotnet-inspect](https://github.com/dotnet/inspect)**: Member inspection and performance triage CLI tool by the .NET team.

Restore the tools locally with:
```bash
dotnet tool restore
```

---

## 🚀 Running the IL Interrogation Script

We provide an automated interrogation runner in `scripts/InterrogateIL.cs`. It compiles the target project in `Release` configuration, isolates generated extension classes, disassembles their IL, decompiles their C#, and generates an analysis report.

### Companion: `scripts/StateMachineMetrics.cs`

`InterrogateIL.cs` asks whether the emitted IL **boxes**. `StateMachineMetrics.cs` asks how **big**
it is, and how much of it the compiler hoisted into an async state machine. It reads assembly
metadata directly through `System.Reflection.Metadata`, so it needs no decompiler and no
`dotnet tool restore`, and the numbers are the real IL body sizes and the real field counts rather
than an inference from the generated source:

```bash
dotnet run scripts/StateMachineMetrics.cs -- \
    --assembly test/Parquet.SourceGenerator.CLI/bin/Release/net8.0/Parquet.SourceGenerator.CLI.dll \
    --type ParquetExtensions
```

It reports, per emitted type: every async state machine with its field count and `MoveNext` IL
size, the largest methods by IL bytes, and the full field list of each state machine. This is the
harness that answered
[#257](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/257) — see
[22 § Is emitted method size actionable?](./22-GENERATED-CODE-METRICS.md#is-emitted-method-size-actionable-no-watch-it-do-not-fix-it)
— by showing that a 1,148-line emitted method with ~38 declared locals carries a state machine with
only **eight** fields, three of them hoisted locals.

### Standard Run
```bash
dotnet run scripts/InterrogateIL.cs
```

### Options & Custom Targets
```bash
# Interrogate a specific project or assembly
dotnet run scripts/InterrogateIL.cs --project benchmarks/Parquet.SourceGenerator.Benchmarks/Parquet.SourceGenerator.Benchmarks.csproj

# Interrogate by specific type pattern
dotnet run scripts/InterrogateIL.cs --type "*AdultCensus*"

# Specify a custom output directory for dumped IL & decompiled C#
dotnet run scripts/InterrogateIL.cs --out temp/my-il-dump

# Run in CI verification mode (fails with non-zero exit code if any boxing 'box' is found)
dotnet run scripts/InterrogateIL.cs --check
```

---

## 📊 Generated Artifacts

Running the tool produces the following artifacts in the output directory (default: `temp/il/`):

1. **`IL_INTERROGATION_REPORT.md`**: Summary report listing total `box` opcodes, `callvirt` invocations, and `newobj`/`newarr` heap allocations across all interrogated types.
2. **`<TypeName>.il`**: Full disassembled IL listing for each generated type.
3. **`<TypeName>.decompiled.cs`**: Decompiled C# source reconstructed directly from the compiled binary.

### Example Report Output
```markdown
| Type Name | `box` Opcodes | `callvirt` Calls | `newobj`/`newarr` Allocations |
| :--- | :---: | :---: | :---: |
| `Parquet.SourceGenerator.CLI.ProfileEventParquetExtensions` | 0 | 308 | 104 |
```

---

## 🔍 Diffing IL Between Branches

To compare generated IL between your branch and `main`:

```bash
# 1. On main branch: dump baseline IL
git checkout main
dotnet run scripts/InterrogateIL.cs --out temp/il-baseline

# 2. On feature branch: dump new IL
git checkout my-feature-branch
dotnet run scripts/InterrogateIL.cs --out temp/il-feature

# 3. Diff the IL files
diff -u temp/il-baseline/ProfileEventParquetExtensions.il temp/il-feature/ProfileEventParquetExtensions.il
```
