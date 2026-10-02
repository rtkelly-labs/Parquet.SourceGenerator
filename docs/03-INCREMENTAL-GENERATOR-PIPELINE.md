# 03 - Incremental Generator Pipeline

## Roslyn IIncrementalGenerator Pipeline Architecture

`Parquet.SourceGenerator` is built using Roslyn 4.0's `IIncrementalGenerator` engine. Incremental generators run continuously in modern IDEs (Visual Studio, Rider, VS Code / C# Dev Kit) and MSBuild. High performance and correct caching behavior are critical to ensure that code generation occurs in milliseconds without causing IDE lag.

---

## Pipeline Execution Stages

```
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
             v  Emit C# Source Code & Roslyn Diagnostics
 +------------------------+
 | Code Generation &      |
 | Diagnostic Output      |
 +------------------------+
```

---

## 1. Syntax Provider & Filtering

```csharp
[Generator(LanguageNames.CSharp)]
public sealed class ParquetIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Collect targets with attributes
        IncrementalValuesProvider<ClassToGenerate?> targets = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsTargetSyntax(s),
                transform: static (ctx, token) => GetTargetSymbol(ctx, token))
            .Where(static m => m is not null);

        // Step 2: Register source output
        context.RegisterSourceOutput(targets, static (spc, target) =>
        {
            if (target is null) return;
            GenerateSource(spc, target);
        });
    }

    private static bool IsTargetSyntax(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax { AttributeLists.Count: > 0 }
            || node is RecordDeclarationSyntax { AttributeLists.Count: > 0 }
            || node is StructDeclarationSyntax { AttributeLists.Count: > 0 };
    }
}
```

---

## 2. Value-Equatable Intermediate Models for Roslyn Caching

Roslyn incremental caching relies on strict structural equality (`IEquatable<T>`) for intermediate models. Passing raw `ISymbol` or `SyntaxNode` references downstream breaks Roslyn's cache!

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

The source generator inspects target symbols and emits compile-time diagnostics for error conditions. The IDs are `PARQ001` to `PARQ016`; the authoritative catalogue (severity, cause, remediation) is [13 - Compiler Diagnostics](13-COMPILER-DIAGNOSTICS.md), and the IDs are tracked for release in `AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md`. A table is not repeated here because this one had drifted (it listed four diagnostics and gave every one the wrong ID).

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
