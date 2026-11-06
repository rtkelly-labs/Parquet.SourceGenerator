using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public sealed class GeneratorConfigurationTests
{
    [Fact]
    public void ReadsFeatureLevelFromGlobalAnalyzerConfig()
    {
        var provider = new TestOptionsProvider(
            new Dictionary<string, string>
            {
                ["build_property.ParquetGeneratorFeatureLevel"] = "Level3ModernCSharp",
            }
        );

        GeneratorConfiguration
            .From(CSharpCompilation.Create("test"), provider)
            .FeatureLevel.ShouldBe(GeneratorFeatureLevel.Level3ModernCSharp);
    }

    [Fact]
    public void EmptyGlobalFeatureLevelUsesTheDefault()
    {
        var provider = new TestOptionsProvider(
            new Dictionary<string, string> { ["build_property.ParquetGeneratorFeatureLevel"] = "" }
        );

        GeneratorConfiguration configuration = GeneratorConfiguration.From(
            CSharpCompilation.Create("test"),
            provider
        );

        configuration.FeatureLevel.ShouldBe(GeneratorConfiguration.Default.FeatureLevel);
        configuration.ConfigurationDiagnostic.ShouldBeNull();
    }

    [Fact]
    public void ConfiguredFeatureLevelIsStampedInEmittedSource()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "Widget",
            Properties: EquatableArray<PropertyModel>.Empty
        );

        string source = CodeEmitter.EmitSource(
            model,
            new GeneratorConfiguration(GeneratorFeatureLevel.Level2CompoundPreview, "test-version")
        );

        source.ShouldContain("// ParquetGeneratorFeatureLevel: Level2CompoundPreview");
        source.ShouldContain("// ParquetGeneratorVersion:");
    }

    [Fact]
    public void InvalidGlobalFeatureLevelProducesAnErrorInsteadOfFallingBack()
    {
        var provider = new TestOptionsProvider(
            new Dictionary<string, string>
            {
                ["build_property.ParquetGeneratorFeatureLevel"] = "TypoLevel",
            }
        );

        GeneratorConfiguration configuration = GeneratorConfiguration.From(
            CSharpCompilation.Create("test"),
            provider
        );

        configuration.ConfigurationDiagnostic.ShouldNotBeNull();
        configuration.ConfigurationDiagnostic.Value.Descriptor.Id.ShouldBe("PARQ015");
        configuration.ConfigurationDiagnostic.Value.MessageArgs.ShouldContain("TypoLevel");
    }

    [Fact]
    public void InformationalVersionPreservesPrereleaseAndDropsOnlyBuildMetadata()
    {
        string? informational = typeof(GeneratorConfiguration)
            .Assembly.GetCustomAttributes(inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .SingleOrDefault()
            ?.InformationalVersion;
        string expected =
            informational?.Split('+')[0]
            ?? typeof(GeneratorConfiguration).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";

        GeneratorConfiguration.Default.GeneratorVersion.ShouldBe(expected);
    }

    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions options;

        public TestOptionsProvider(IReadOnlyDictionary<string, string> values)
        {
            options = new TestOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText text) => options;
    }

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> values;

        public TestOptions(IReadOnlyDictionary<string, string> values)
        {
            this.values = values;
        }

        public override bool TryGetValue(string key, out string value) =>
            values.TryGetValue(key, out value!);
    }
}
