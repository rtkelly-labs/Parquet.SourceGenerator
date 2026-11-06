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

## 3. Roslyn Diagnostics & Validations (`PARQ001` - `PARQ099`)

The source generator inspects target symbols and emits compile-time diagnostics for error conditions:

| Diagnostic ID | Severity | Description | Remedy |
| :--- | :--- | :--- | :--- |
| `PARQ001` | Error | Target type must have a accessible parameterless or primary constructor. | Add a parameterless constructor or record positional parameters. |
| `PARQ002` | Error | Property type is not supported by `Parquet.Net` schema. | Use a supported primitive type (`int`, `string`, `DateTime`, etc.) or custom converter. |
| `PARQ003` | Warning | Duplicate `ParquetColumn` name detected within the same type. | Ensure unique column names across properties. |
| `PARQ004` | Error | Invalid `ParquetDecimal` attribute precision/scale specification. | Ensure precision > scale and scale >= 0. |

```csharp
public static class Diagnostics
{
    public static readonly DiagnosticDescriptor MissingConstructor = new(
        id: "PARQ001",
        title: "Missing accessible constructor",
        messageFormat: "Type '{0}' decorated with [ParquetSerializable] must have an accessible parameterless constructor or primary constructor",
        category: "ParquetSourceGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
```
