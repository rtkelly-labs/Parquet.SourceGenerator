using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Emitter.Components;
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
        // 1. Read project policy as value-equatable state, then parse decorated target nodes with
        // the feature level's compound-shape allowance.
        IncrementalValueProvider<GeneratorConfiguration> configuration = context
            .CompilationProvider.Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) => GeneratorConfiguration.From(pair.Left, pair.Right));
        IncrementalValuesProvider<GeneratorSyntaxContext> targetNodes =
            context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (s, _) => IsTargetSyntax(s),
                transform: static (ctx, _) => ctx
            );
        IncrementalValuesProvider<TargetParserResult> targets = targetNodes
            .Combine(configuration)
            .Select(
                static (pair, _) =>
                    TargetParser.GetTargetModel(
                        pair.Left,
                        ParquetApiLevel.V6,
                        compoundKinds: CompoundKindsFor(pair.Right.FeatureLevel)
                    )
            );

        context.RegisterSourceOutput(
            configuration,
            static (spc, config) =>
            {
                if (config.ConfigurationDiagnostic is DiagnosticInfo diagnostic)
                {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            }
        );

        // 2. Register source output emission & diagnostic reporting
        context.RegisterSourceOutput(
            targets.Combine(configuration),
            static (spc, pair) =>
            {
                TargetParserResult result = pair.Left;
                if (pair.Right.ConfigurationDiagnostic is not null)
                {
                    return;
                }

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
                    string sourceCode = CodeEmitter.EmitSource(result.Model, pair.Right);
                    spc.AddSource(hintName, sourceCode);

                    // Adapted members read and write through generated storage shadows on the
                    // partial type (docs/44-TYPE-ADAPTERS.md); emitted only when there are any, so
                    // every model without an adapter produces exactly the files it always did.
                    if (!result.Model.AdapterShadows.IsEmpty)
                    {
                        spc.AddSource(
                            $"{prefix}.{AdapterShadowComponent.HintSuffix}",
                            AdapterShadowComponent.EmitSource(result.Model.AdapterShadows)
                        );
                    }
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

    private static Parser.CompoundKinds CompoundKindsFor(GeneratorFeatureLevel featureLevel) =>
        featureLevel == GeneratorFeatureLevel.Level1Flat
            ? Parser.CompoundKinds.None
            : Parser.CompoundKinds.Struct | Parser.CompoundKinds.List;
}
