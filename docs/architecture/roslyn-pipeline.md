# Roslyn Incremental Generator Pipeline

## Overview

`Parquet.SourceGenerator` is built using Roslyn 4.0's `IIncrementalGenerator` engine. Incremental generators run continuously in modern IDEs (Visual Studio, Rider, VS Code / C# Dev Kit) and MSBuild. High performance and correct caching behavior are critical to ensure that code generation occurs in milliseconds without causing IDE lag.

---

## 1. Pipeline Execution Stages

```text
 +------------------------+
 |  Compilation / Syntax  |
 +------------------------+
             |
             v  Filter classes/structs with [ParquetSerializable]
 +------------------------+
 | SyntaxValueProvider    |
 +------------------------+
             |
             v  Transform & Extract Symbols (INamedTypeSymbol -> Equatable Model)
 +------------------------+
 | Transform Model Step   |
 +------------------------+
             |
             v  Combine & Filter duplicates
 +------------------------+
 | Incremental Pipeline   |
 +------------------------+
             |
             v  Emit Source Code (*.g.cs)
 +------------------------+
 | Source Output Step     |
 +------------------------+
```

---

## 2. Model Equatable Contracts for Caching

Roslyn incremental caching relies on strict structural equality (`IEquatable<T>`) for intermediate models. Passing raw `ISymbol` or `SyntaxNode` references downstream breaks Roslyn's cache:

```csharp
public sealed record PropertyModel(
    string Name,
    string ParquetColumnName,
    string TypeName,
    bool IsNullable,
    int Order) : IEquatable<PropertyModel>;

public sealed record ClassToGenerate(
    string Namespace,
    string ClassName,
    EquatableArray<PropertyModel> Properties) : IEquatable<ClassToGenerate>;
```

---

## 3. Roslyn Diagnostics & Validations

The source generator inspects target symbols and emits compile-time diagnostics for error conditions. The IDs are `PARQ001` to `PARQ016`; the authoritative catalogue (severity, cause, remediation) is [Compiler Diagnostics Reference](../reference/compiler-diagnostics.md), and the IDs are tracked for release in `AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md`.

```csharp
public static class Diagnostics
{
    public static readonly DiagnosticDescriptor MustBePartial = new(
        id: "PARQ001",
        title: "Type decorated with [ParquetSerializable] must be partial",
        messageFormat: "The type '{0}' is decorated with [ParquetSerializable] but is not declared as partial",
        category: "ParquetSourceGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
```

---

## 4. Pipeline Incrementality Proof

To prevent compiler slowdown in large solutions, every pipeline step is tracked and verified via `IncrementalityTests`:
- **Unrelated Edits:** Modifying a method body or adding comments in an unrelated file results in zero generator executions (`GeneratorDriverRunResult.GeneratedTrees` is empty, all tracked outputs are cached).
- **Whitespace / Formatting:** Modifying whitespace or non-semantic tokens on an annotated model leaves downstream emission outputs cached.
- **Per-Model Isolation:** Modifying a property in `ModelA` invalidates only `ModelA.g.cs`; other annotated models in the compilation remain strictly cached.
