extern alias LegacyGenerator;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Shouldly;
using Xunit;
using LegacyApiLevel = LegacyGenerator::Parquet.SourceGenerator.Parser.ParquetApiLevel;
using LegacyEmitter = LegacyGenerator::Parquet.SourceGenerator.Legacy.Emitter.LegacyCodeEmitter;
using LegacyModels = LegacyGenerator::Parquet.SourceGenerator.Models;

using LegacyParser = LegacyGenerator::Parquet.SourceGenerator.Parser.TargetParser;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// M1 of issue #176 on the classic backend: the compound parser is shared source, so the same
/// recursive tree must hold for the Parquet.Net 4.x/5.x generation — including the PARQ011 rule
/// for leaf types (list elements and map values included) that only the v4 API lacks.
/// </summary>
public sealed class LegacyCompoundModelParsingTests
{
    private static readonly string[] CityZipNames = ["City", "Zip"];
    private const string RowSource = """
        using Parquet.SourceGenerator;
        using System.Collections.Generic;

        namespace App;

        [ParquetSerializable]
        public partial class Address
        {
            public string? City { get; init; }
            public int? Zip { get; init; }
        }

        [ParquetSerializable]
        public partial class Row
        {
            public int Id { get; init; }
            public Address? Ship { get; init; }
            public List<string?>? Tags { get; init; }
            public Dictionary<string, string?>? Meta { get; init; }
        }
        """;

    private static INamedTypeSymbol CompileAndGet(string source, string typeName)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(ParquetSerializableAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
        };

        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        return compilation.GetTypeByMetadataName(typeName)
            ?? throw new InvalidOperationException($"test source declares no {typeName}");
    }

    [Fact]
    public void ClassicBackendBuildsTheSameCompoundTrees()
    {
        INamedTypeSymbol row = CompileAndGet(RowSource, "App.Row");

        LegacyGenerator::Parquet.SourceGenerator.Parser.TargetParserResult result =
            LegacyParser.GetTargetModel(row, LegacyApiLevel.V4, allowCompoundTypes: true);

        result.Diagnostics.ShouldBeEmpty();
        result.Model.ShouldNotBeNull();

        LegacyModels::PropertyModel ship = result.Model!.Properties.Single(p =>
            p.Kind == LegacyModels::PropertyKind.Struct
        );
        ship.Children.Select(c => c.Name).ToArray().ShouldBe(CityZipNames);

        LegacyModels::PropertyModel tags = result.Model!.Properties.Single(p =>
            p.Kind == LegacyModels::PropertyKind.List
        );
        tags.Element!.IsNullable.ShouldBeTrue();

        LegacyModels::PropertyModel meta = result.Model!.Properties.Single(p =>
            p.Kind == LegacyModels::PropertyKind.Map
        );
        meta.Children[0].IsNullable.ShouldBeFalse(); // required key
        meta.MapValue!.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void ListElementsUnsupportedByTheClassicApiTriggerPARQ011()
    {
        // ReadOnlyMemory<byte> is a v6-only representation (#118); the rule must apply inside
        // compound lanes exactly as it does on bare members.
        string source = """
            using Parquet.SourceGenerator;
            using System;
            using System.Collections.Generic;

            namespace App;

            [ParquetSerializable]
            public partial class Row
            {
                public int Id { get; init; }
                public List<ReadOnlyMemory<byte>>? Blobs { get; init; }
            }
            """;

        INamedTypeSymbol row = CompileAndGet(source, "App.Row");
        var result = LegacyParser.GetTargetModel(row, LegacyApiLevel.V4, allowCompoundTypes: true);

        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.TypeUnsupportedOnClassicApi.Id
        );
    }

    [Fact]
    public void ClassicBackendGuardsCyclesAndDepth()
    {
        string cycleSource = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable]
            public partial class Node
            {
                public int Id { get; init; }
                public Node? Next { get; init; }
            }
            """;

        var cycle = LegacyParser.GetTargetModel(
            CompileAndGet(cycleSource, "App.Node"),
            LegacyApiLevel.V4,
            allowCompoundTypes: true
        );
        cycle.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.NestedTypeCycleDetected.Id
        );

        string deepSource = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable] public partial class L7 { public int Leaf { get; init; } }
            [ParquetSerializable] public partial class L6 { public L7? Seven { get; init; } }
            [ParquetSerializable] public partial class L5 { public L6? Six { get; init; } }
            [ParquetSerializable] public partial class L4 { public L5? Five { get; init; } }
            [ParquetSerializable] public partial class L3 { public L4? Four { get; init; } }
            [ParquetSerializable] public partial class L2 { public L3? Three { get; init; } }
            [ParquetSerializable] public partial class L1 { public L2? Two { get; init; } }
            [ParquetSerializable] public partial class Root { public L1? One { get; init; } }
            """;

        var deep = LegacyParser.GetTargetModel(
            CompileAndGet(deepSource, "App.Root"),
            LegacyApiLevel.V4,
            allowCompoundTypes: true
        );
        deep.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.NestedTypeTooDeep.Id
        );
    }

    [Fact]
    public void ClassicShippingPipelineRemainsInertToCompoundMembers()
    {
        INamedTypeSymbol row = CompileAndGet(RowSource, "App.Row");

        var viaPipeline = LegacyParser.GetTargetModel(
            row,
            LegacyApiLevel.V4,
            allowCompoundTypes: false
        );

        viaPipeline.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
        viaPipeline.Model.ShouldBeNull();
    }

    [Fact]
    public void LegacyEmitterFlattensNestedTargetIdentifiers()
    {
        // Mirrors the v6 ContainerNestedRow test: the dotted ClassName resolves type references,
        // the identifier flattens. PARQ009's reversal must emit on the classic backend too.
        string source = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable]
            public partial class Row
            {
                public int Id { get; init; }
            }
            """;

        var result = LegacyParser.GetTargetModel(
            CompileAndGet(source, "App.Row"),
            LegacyApiLevel.V4,
            allowCompoundTypes: false
        );

        result.Model.ShouldNotBeNull();
        string code = LegacyEmitter.EmitSource(result.Model!);
        code.ShouldContain("RowParquetLegacyExtensions");
    }
}
