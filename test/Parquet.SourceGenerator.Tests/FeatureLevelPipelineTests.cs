using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Parquet.SourceGenerator.Diagnostics;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The parse runs inside the syntax transform (#395) but the feature level is only known after the
/// analyzer-config provider is combined in, so the transform parses a compound member under both
/// dials and the pipeline picks one. These tests pin that choice through the real driver.
/// </summary>
public sealed class FeatureLevelPipelineTests
{
    private const string PersonSource = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Address
        {
            public string City { get; init; } = string.Empty;
        }

        [ParquetSerializable]
        public partial class Person
        {
            [ParquetColumn("id")]
            public int Id { get; init; }

            [ParquetColumn("home")]
            public Address Home { get; init; } = new();
        }
        """;

    private static GeneratorRunResult Run(string? featureLevel)
    {
        MetadataReference[] references =
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
        ];
        CSharpCompilation compilation = CSharpCompilation.Create(
            "FeatureLevelAssembly",
            [CSharpSyntaxTree.ParseText(PersonSource)],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        var options = new Dictionary<string, string>();
        if (featureLevel is not null)
        {
            options["build_property.ParquetGeneratorFeatureLevel"] = featureLevel;
        }

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ParquetIncrementalGenerator().AsSourceGenerator()],
            additionalTexts: null,
            parseOptions: null,
            optionsProvider: new TestOptionsProvider(options)
        );
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    [Fact]
    public void ACompoundMemberEmitsUnderTheDefaultFeatureLevel()
    {
        GeneratorRunResult result = Run(featureLevel: null);

        result.Exception.ShouldBeNull();
        result.GeneratedSources.ShouldContain(source =>
            source.HintName == "Demo.Person.ParquetSerializer.g.cs"
        );
        result.Diagnostics.ShouldNotContain(d =>
            d.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
    }

    [Fact]
    public void ACompoundMemberIsRejectedUnderLevel1Flat()
    {
        GeneratorRunResult result = Run("Level1Flat");

        result.Exception.ShouldBeNull();
        result.GeneratedSources.ShouldNotContain(source =>
            source.HintName == "Demo.Person.ParquetSerializer.g.cs"
        );
        result.Diagnostics.ShouldContain(d =>
            d.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
    }

    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly TestOptions options;

        public TestOptionsProvider(Dictionary<string, string> values) =>
            options = new TestOptions(values);

        public override AnalyzerConfigOptions GlobalOptions => options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => options;
    }

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> values;

        public TestOptions(Dictionary<string, string> values) => this.values = values;

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
            values.TryGetValue(key, out value);
    }
}
