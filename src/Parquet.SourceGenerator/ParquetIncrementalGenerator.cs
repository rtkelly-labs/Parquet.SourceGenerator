using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Parquet.SourceGenerator.Parser;

namespace Parquet.SourceGenerator;

/// <summary>
/// Roslyn 4.0 Incremental Source Generator for Parquet.Net.
/// Emits zero-reflection schema definitions, column serializers, and deserializers at compile time with compiler diagnostic checks.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ParquetIncrementalGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Initializes the incremental generator pipeline.
    /// </summary>
    /// <param name="context">The incremental generator context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Filter syntax nodes decorated with attributes and extract target model + diagnostics
        IncrementalValuesProvider<TargetParserResult> targets =
            context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (s, _) => IsTargetSyntax(s),
                // Compound dial (#176): the v6 emitter expresses nested POCOs (M2) and
                // root-level lists/arrays of primitives (M3a). Maps (M4), lists of POCOs,
                // and lists nested in structs (M3b) still fall to PARQ006.
                transform: static (ctx, _) =>
                    TargetParser.GetTargetModel(
                        ctx,
                        ParquetApiLevel.V6,
                        compoundKinds: Parser.CompoundKinds.Struct | Parser.CompoundKinds.List
                    )
            );

        // 2. Register source output emission & diagnostic reporting
        context.RegisterSourceOutput(
            targets,
            static (spc, result) =>
            {
                // Report compiler diagnostics (PARQ001, PARQ002, PARQ003)
                for (int i = 0; i < result.Diagnostics.Length; i++)
                {
                    spc.ReportDiagnostic(result.Diagnostics[i].ToDiagnostic());
                }

                // Emit generated source code if target model is valid
                if (result.Model is not null)
                {
                    // Qualify the hint name with the namespace. AddSource requires hint names to be
                    // unique within a generator, and it throws rather than warning — so two
                    // [ParquetSerializable] types sharing a class name in different namespaces (an
                    // Order in Sales and one in Billing, say) took the whole generator down with
                    // CS8785, and every type it would have emitted disappeared with it.
                    string prefix = string.IsNullOrEmpty(result.Model.Namespace)
                        ? result.Model.ClassName
                        : $"{result.Model.Namespace}.{result.Model.ClassName}";
                    string hintName = $"{prefix}.ParquetSerializer.g.cs";
                    string sourceCode = CodeEmitter.EmitSource(result.Model);
                    spc.AddSource(hintName, sourceCode);
                }
            }
        );

        // 3. Apache Arrow bridge — emitted only when the consumer compilation references
        //    Apache.Arrow. The gate is a single bool, so every downstream node stays cached until
        //    the reference itself is added or removed; the main emission above never sees it.
        IncrementalValueProvider<bool> arrowReferenced = context.CompilationProvider.Select(
            static (compilation, _) => ReferencesApacheArrow(compilation)
        );

        IncrementalValuesProvider<(TargetParserResult Result, bool ArrowReferenced)> arrowTargets =
            targets.Combine(arrowReferenced);

        context.RegisterSourceOutput(
            arrowTargets,
            static (spc, pair) =>
            {
                if (!pair.ArrowReferenced || pair.Result.Model is null)
                {
                    return;
                }

                TargetClassModel model = pair.Result.Model;
                if (!ArrowBridgeEmitter.CanEmit(model))
                {
                    // A member with no Arrow representation (a nested group, a list, an exotic
                    // leaf) opts the whole type out. Emitting a partial bridge would be worse than
                    // emitting none: the caller would discover the gap at runtime.
                    return;
                }

                string prefix = string.IsNullOrEmpty(model.Namespace)
                    ? model.ClassName
                    : $"{model.Namespace}.{model.ClassName}";
                spc.AddSource($"{prefix}.Arrow.g.cs", ArrowBridgeEmitter.EmitSource(model));
            }
        );
    }

    /// <summary>
    /// Cheap check for an Apache.Arrow reference. The assembly-name scan short-circuits the common
    /// case (no Arrow anywhere) without asking Roslyn to bind a type; the metadata lookup then
    /// confirms the reference actually carries <c>RecordBatch</c>.
    /// </summary>
    private static bool ReferencesApacheArrow(Compilation compilation)
    {
        bool named = false;
        foreach (AssemblyIdentity identity in compilation.ReferencedAssemblyNames)
        {
            if (string.Equals(identity.Name, "Apache.Arrow", System.StringComparison.Ordinal))
            {
                named = true;
                break;
            }
        }

        if (!named)
        {
            return false;
        }

        return compilation.GetTypeByMetadataName("Apache.Arrow.RecordBatch") is not null;
    }

    private static bool IsTargetSyntax(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax { AttributeLists.Count: > 0 }
            || node is RecordDeclarationSyntax { AttributeLists.Count: > 0 }
            || node is StructDeclarationSyntax { AttributeLists.Count: > 0 };
    }
}
