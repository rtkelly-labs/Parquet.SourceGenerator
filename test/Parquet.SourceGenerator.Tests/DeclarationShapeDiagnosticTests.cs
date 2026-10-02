extern alias LegacyGenerator;

using System;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Issue #402: abstract, ref struct and file-local targets passed every declaration check and then
/// failed to compile inside the generated file. Each now reports one diagnostic and emits nothing.
/// </summary>
public sealed class DeclarationShapeDiagnosticTests
{
    private const string Abstract = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public abstract partial class Base
        {
            public Base() { }

            [ParquetColumn("id")]
            public int Id { get; set; }
        }
        """;

    private const string RefStruct = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public ref partial struct Span1
        {
            [ParquetColumn("x")]
            public int X { get; set; }
        }
        """;

    private const string FileLocal = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        file partial class Hidden
        {
            [ParquetColumn("x")]
            public int X { get; set; }
        }
        """;

    private const string NestedInFileLocal = """
        using Parquet.SourceGenerator;

        namespace Demo;

        file partial class Outer
        {
            [ParquetSerializable]
            public partial class Inner
            {
                [ParquetColumn("x")]
                public int X { get; set; }
            }
        }
        """;

    private const string Concrete = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Fine
        {
            [ParquetColumn("x")]
            public int X { get; set; }
        }
        """;

    private static GeneratorRunResult Run(string flavor, params string[] sources)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ShapeAssembly",
            sources.Select((text, i) => CSharpSyntaxTree.ParseText(text, path: $"File{i}.cs")),
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
                MetadataReference.CreateFromFile(
                    typeof(ParquetSerializableAttribute).Assembly.Location
                ),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
                MetadataReference.CreateFromFile(
                    typeof(global::Parquet.ParquetWriter).Assembly.Location
                ),
            ],
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        IIncrementalGenerator generator =
            flavor == "legacy"
                ? new LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator()
                : new ParquetIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    [Theory]
    [InlineData("current", Abstract, "PARQ020")]
    [InlineData("legacy", Abstract, "PARQ020")]
    [InlineData("current", RefStruct, "PARQ021")]
    [InlineData("legacy", RefStruct, "PARQ021")]
    [InlineData("current", FileLocal, "PARQ022")]
    [InlineData("legacy", FileLocal, "PARQ022")]
    [InlineData("current", NestedInFileLocal, "PARQ022")]
    [InlineData("legacy", NestedInFileLocal, "PARQ022")]
    public void AnUnsupportedShapeReportsOneDiagnosticAndEmitsNothing(
        string flavor,
        string source,
        string id
    )
    {
        GeneratorRunResult result = Run(flavor, source);

        result.Exception.ShouldBeNull();
        result.Diagnostics.Count(d => d.Id == id).ShouldBe(1);
        result.GeneratedSources.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("current")]
    [InlineData("legacy")]
    public void AnUnsupportedTargetDoesNotStopAConcreteOneEmitting(string flavor)
    {
        GeneratorRunResult result = Run(flavor, Abstract, RefStruct, FileLocal, Concrete);

        result.Exception.ShouldBeNull();
        result.GeneratedSources.Length.ShouldBe(1);
        result.GeneratedSources[0].HintName.ShouldStartWith("Demo.Fine.");
    }

    [Fact]
    public void TheDescriptorIdsAreDistinctFromTheTypeAdapterRange()
    {
        // PR #476 (type adapters, not in 0.1) claims PARQ016 to PARQ019. These must not overlap it.
        string[] ids =
        [
            DiagnosticDescriptors.AbstractTypeNotSupported.Id,
            DiagnosticDescriptors.RefStructNotSupported.Id,
            DiagnosticDescriptors.FileLocalTypeNotSupported.Id,
        ];

        ids.ShouldBe(["PARQ020", "PARQ021", "PARQ022"]);
    }
}
