using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Generated code calls Lake.Parquet (the Parquet.Net fork in the <c>Lake.Parquet</c> namespace) when
/// the consumer references it instead of Parquet.Net, and Parquet.Net otherwise.
/// </summary>
public sealed class LakeParquetRetargetTests
{
    private const string Source = """
        using System;
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Trade
        {
            [ParquetColumn("id")]
            public int Id { get; init; }

            [ParquetColumn("symbol")]
            public string Symbol { get; init; } = string.Empty;

            [ParquetColumn("price")]
            public decimal? Price { get; init; }

            [ParquetColumn("at")]
            public DateTime? At { get; init; }
        }
        """;

    private static readonly Regex ParquetNetName = new(@"global::Parquet\.(?!SourceGenerator\b)");

    private static List<MetadataReference> BaseReferences() =>
        new()
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(ParquetSerializableAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
        };

    private static MetadataReference ParquetNet() =>
        MetadataReference.CreateFromFile(typeof(global::Parquet.ParquetWriter).Assembly.Location);

    // Only the assembly's name decides the library, so an empty assembly named Lake.Parquet stands in for it.
    private static MetadataReference LakeParquetStub()
    {
        CSharpCompilation stub = CSharpCompilation.Create(
            "Lake.Parquet",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    "namespace Lake.Parquet { public sealed class Marker { } }"
                ),
            },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var image = new System.IO.MemoryStream();
        stub.Emit(image).Success.ShouldBeTrue();
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static string Generate(params MetadataReference[] library)
    {
        List<MetadataReference> references = BaseReferences();
        references.AddRange(library);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "RetargetAssembly",
            new[] { CSharpSyntaxTree.ParseText(Source) },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ParquetIncrementalGenerator().AsSourceGenerator()
        );
        ImmutableArray<GeneratedSourceResult> sources = driver
            .RunGenerators(compilation)
            .GetRunResult()
            .Results.Single()
            .GeneratedSources;
        return string.Join("\n", sources.Select(s => s.SourceText.ToString()));
    }

    [Fact]
    public void ParquetNetConsumersGetParquetNetNames()
    {
        string code = Generate(ParquetNet());

        code.ShouldContain("global::Parquet.ParquetOptions", Case.Sensitive);
        code.ShouldNotContain("global::Lake.Parquet.", Case.Sensitive);
    }

    [Fact]
    public void LakeParquetConsumersGetLakeParquetNamesAndKeepTheGeneratorsOwn()
    {
        string code = Generate(LakeParquetStub());

        code.ShouldContain("global::Lake.Parquet.ParquetOptions", Case.Sensitive);
        ParquetNetName.IsMatch(code).ShouldBeFalse();
        code.ShouldContain("global::Parquet.SourceGenerator.", Case.Sensitive);
    }

    [Fact]
    public void ReferencingBothKeepsParquetNet()
    {
        string code = Generate(ParquetNet(), LakeParquetStub());

        code.ShouldContain("global::Parquet.ParquetOptions", Case.Sensitive);
        code.ShouldNotContain("global::Lake.Parquet.", Case.Sensitive);
    }
}
