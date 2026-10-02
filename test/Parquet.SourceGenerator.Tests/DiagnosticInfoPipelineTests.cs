using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Parquet.SourceGenerator.Models;
using Parquet.SourceGenerator.Parser;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Issue #398: <c>DiagnosticInfo</c> held a Roslyn <c>Location</c> inside the cached pipeline
/// value, so every model with a diagnostic pinned its <c>SyntaxTree</c> for the driver's lifetime.
/// </summary>
public sealed class DiagnosticInfoPipelineTests
{
    private const string NonPartialSource = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public class NotPartial
        {
            [ParquetColumn("id")]
            public int Id { get; init; }
        }
        """;

    private static readonly Type[] RetainedRoslynTypes =
    [
        typeof(Location),
        typeof(SyntaxTree),
        typeof(SyntaxNode),
        typeof(SyntaxReference),
        typeof(SemanticModel),
        typeof(Compilation),
        typeof(ISymbol),
        typeof(AttributeData),
    ];

    [Fact]
    public void NothingReachableFromAPipelineValueHoldsASyntaxOrSemanticObject()
    {
        // The pipeline caches TargetParseSet and GeneratorConfiguration. Walking their field graph
        // is what catches the next Location or ISymbol that slips into a model, which no analyzer
        // does (docs/44 section 5).
        var offenders = new List<string>();
        var seen = new HashSet<Type>();
        Walk(typeof(TargetParseSet), "TargetParseSet", seen, offenders);
        Walk(typeof(GeneratorConfiguration), "GeneratorConfiguration", seen, offenders);

        offenders.ShouldBeEmpty();
    }

    private static void Walk(Type type, string path, HashSet<Type> seen, List<string> offenders)
    {
        if (type.IsGenericParameter)
        {
            return;
        }

        if (RetainedRoslynTypes.Any(banned => banned.IsAssignableFrom(type)))
        {
            offenders.Add($"{path}: {type.FullName}");
            return;
        }

        if (!seen.Add(type))
        {
            return;
        }

        if (type.IsArray)
        {
            Walk(type.GetElementType()!, path + "[]", seen, offenders);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                Walk(argument, path + "<>", seen, offenders);
            }
        }

        if (type.Assembly != typeof(DiagnosticInfo).Assembly && !type.IsGenericType)
        {
            // Framework and Roslyn value types (TextSpan, string, enums, descriptors) are leaves;
            // only this assembly's own models can hide a reference.
            return;
        }

        foreach (
            FieldInfo field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            )
        )
        {
            Walk(field.FieldType, $"{path}.{field.Name}", seen, offenders);
        }
    }

    [Fact]
    public void ARebuiltDiagnosticPointsAtTheSameFileAndSpan()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            NonPartialSource,
            path: "Models/NotPartial.cs"
        );
        TypeDeclarationSyntaxHelper.Identifier(tree, out Location expected);
        var info = new DiagnosticInfo(
            DiagnosticDescriptors.MustBePartial,
            expected,
            ["NotPartial"]
        );

        Location rebuilt = info.ToDiagnostic().Location;

        rebuilt.IsInSource.ShouldBeFalse("a rebuilt location is path and span data, not a tree");
        rebuilt.GetLineSpan().Path.ShouldBe("Models/NotPartial.cs");
        rebuilt.GetLineSpan().Span.ShouldBe(expected.GetLineSpan().Span);
        rebuilt.SourceSpan.ShouldBe(expected.SourceSpan);
    }

    [Fact]
    public void ARebuiltDiagnosticKeepsALineDirectiveMapping()
    {
        // A diagnosed declaration after #line reports at the mapped file and line, which is what
        // the IDE and the build output show. Keeping only the physical span would move it.
        const string Mapped = """
            using Parquet.SourceGenerator;

            #line 100 "Mapped.cs"
            [ParquetSerializable]
            public class NotPartial
            {
                [ParquetColumn("id")]
                public int Id { get; init; }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Mapped, path: "Physical.cs");
        TypeDeclarationSyntaxHelper.Identifier(tree, out Location expected);
        var info = new DiagnosticInfo(
            DiagnosticDescriptors.MustBePartial,
            expected,
            ["NotPartial"]
        );

        FileLinePositionSpan rebuilt = info.ToDiagnostic().Location.GetMappedLineSpan();

        rebuilt.Path.ShouldBe("Mapped.cs");
        rebuilt.Span.ShouldBe(expected.GetMappedLineSpan().Span);
        rebuilt.StartLinePosition.Line.ShouldBe(100);
    }

    [Fact]
    public void ADiagnosticMovesWithItsMemberAfterAnEditAboveIt()
    {
        SyntaxTree before = CSharpSyntaxTree.ParseText(NonPartialSource, path: "NotPartial.cs");
        CSharpCompilation initial = Compile(before);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ParquetIncrementalGenerator().AsSourceGenerator()
        );

        driver = driver.RunGenerators(initial);
        int lineBefore = LineOf(driver, DiagnosticDescriptors.MustBePartial.Id);
        SyntaxTree after = CSharpSyntaxTree.ParseText(
            "\n\n" + NonPartialSource,
            path: "NotPartial.cs"
        );
        driver = driver.RunGenerators(initial.ReplaceSyntaxTree(before, after));

        LineOf(driver, DiagnosticDescriptors.MustBePartial.Id).ShouldBe(lineBefore + 2);
    }

    private static int LineOf(GeneratorDriver driver, string id) =>
        driver
            .GetRunResult()
            .Diagnostics.Single(d => d.Id == id)
            .Location.GetLineSpan()
            .StartLinePosition.Line;

    private static CSharpCompilation Compile(SyntaxTree tree) =>
        CSharpCompilation.Create(
            "DiagnosticInfoAssembly",
            [tree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
                MetadataReference.CreateFromFile(
                    typeof(ParquetSerializableAttribute).Assembly.Location
                ),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            ],
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

    private static class TypeDeclarationSyntaxHelper
    {
        public static void Identifier(SyntaxTree tree, out Location location) =>
            location = tree.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Single()
                .Identifier.GetLocation();
    }
}
