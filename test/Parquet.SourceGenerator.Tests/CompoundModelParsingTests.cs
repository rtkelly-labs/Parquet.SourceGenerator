using System;
using System.Collections.Generic;
using System.IO;
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
/// M1 of issue #176: the recursive model and parser. These exercise the compound pipeline
/// directly through the symbol seam (<c>allowCompoundTypes: true</c>) while the shipping
/// generator still routes members through the flat PARQ006 gate — the parser's tree-building
/// rules are testable before any emitter consumes a compound model.
/// </summary>
public sealed class CompoundModelParsingTests
{
    private static readonly string[] CityZipNames = ["City", "Zip"];

    private static (TargetParserResult Result, INamedTypeSymbol Symbol) Parse(
        string source,
        string typeName
    )
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

        INamedTypeSymbol symbol =
            compilation.GetTypeByMetadataName(typeName)
            ?? throw new InvalidOperationException($"test source declares no {typeName}");

        return (
            TargetParser.GetTargetModel(symbol, ParquetApiLevel.V6, allowCompoundTypes: true),
            symbol
        );
    }

    private const string AddressDecl = """
        [ParquetSerializable]
        public partial class Address
        {
            public string? City { get; init; }
            public int? Zip { get; init; }
        }
        """;

    private static string Row(string members, string extra = "") =>
        $$"""
            using Parquet.SourceGenerator;
            using System.Collections.Generic;

            namespace App;

            {{AddressDecl}}

            {{extra}}

            [ParquetSerializable]
            public partial class Row
            {
                public int Id { get; init; }
                {{members}}
            }
            """;

    [Fact]
    public void StructMemberExpandsToChildModels()
    {
        var (result, _) = Parse(Row("public Address Ship { get; init; }"), "App.Row");

        result.Diagnostics.ShouldBeEmpty();
        PropertyModel ship = result.Model!.Properties.Single(p => p.Kind == PropertyKind.Struct);
        ship.Name.ShouldBe("Ship");
        ship.IsNullable.ShouldBeFalse(); // non-nullable annotation
        ship.Children.Select(c => c.Name).ToArray().ShouldBe(CityZipNames);
        ship.Children[0].IsNullable.ShouldBeTrue(); // string?
        ship.Children[1].Kind.ShouldBe(PropertyKind.Primitive); // int? unwrapped
        ship.Children[1].IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void NullableStructMemberMarksModelNullable()
    {
        var (result, _) = Parse(Row("public Address? Ship { get; init; }"), "App.Row");

        PropertyModel ship = result.Model!.Properties.First(p => p.Kind == PropertyKind.Struct);
        ship.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void ListOfNullableStringsCarriesNullableElement()
    {
        var (result, _) = Parse(Row("public List<string?> Tags { get; init; }"), "App.Row");

        PropertyModel tags = result.Model!.Properties.Single(p => p.Kind == PropertyKind.List);
        tags.Element.ShouldNotBeNull();
        tags.Element!.Kind.ShouldBe(PropertyKind.Primitive);
        tags.Element!.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void ListOfRequiredIntsCarriesNonNullableElement()
    {
        var (result, _) = Parse(Row("public List<int> Scores { get; init; }"), "App.Row");

        PropertyModel scores = result.Model!.Properties.First(p => p.Kind == PropertyKind.List);
        scores.Element!.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public void NestedListsAreParserOnlyAndRejectedByTheShippingPipeline()
    {
        var (result, symbol) = Parse(
            Row("public List<List<int>> Scores { get; init; }"),
            "App.Row"
        );

        result.Diagnostics.ShouldBeEmpty();
        result
            .Model!.Properties.Single(p => p.Kind == PropertyKind.List)
            .Element!.Kind.ShouldBe(PropertyKind.List);

        TargetParserResult pipeline = TargetParser.GetTargetModel(
            symbol,
            ParquetApiLevel.V6,
            allowCompoundTypes: false
        );
        pipeline.Model.ShouldBeNull();
        pipeline.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
    }

    [Fact]
    public void PipelineDialAcceptsListOfPocoWithLeafChildren()
    {
        var (_, symbol) = Parse(Row("public List<Address>? Stops { get; init; }"), "App.Row");
        TargetParserResult viaPipeline = TargetParser.GetTargetModel(
            symbol,
            ParquetApiLevel.V6,
            CompoundKinds.Struct | CompoundKinds.List
        );
        viaPipeline.Model.ShouldNotBeNull();
        viaPipeline.Diagnostics.ShouldNotContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
    }

    [Fact]
    public void PipelineDialAcceptsListOfValuePoco()
    {
        const string pointDecl = """
            [ParquetSerializable]
            public partial struct Point
            {
                public int X { get; init; }
                public int Y { get; init; }
            }
            """;
        var (_, symbol) = Parse(
            Row("public List<Point>? Spots { get; init; }", extra: pointDecl),
            "App.Row"
        );
        TargetParserResult viaPipeline = TargetParser.GetTargetModel(
            symbol,
            ParquetApiLevel.V6,
            CompoundKinds.Struct | CompoundKinds.List
        );
        // Value-type elements are emitted since adapted collections (docs/44 §A.4) needed them:
        // a non-nullable struct element simply has no null rung to test.
        viaPipeline.Diagnostics.ShouldBeEmpty();
        PropertyModel spots = viaPipeline.Model!.Properties.Single(p => p.Name == "Spots");
        spots.Element!.Kind.ShouldBe(PropertyKind.Struct);
        spots.Element!.CompoundIsValueType.ShouldBeTrue();
    }

    [Fact]
    public void ListOfStructsNestsElementTree()
    {
        var (result, _) = Parse(Row("public List<Address> Stops { get; init; }"), "App.Row");

        PropertyModel stops = result.Model!.Properties.First(p => p.Kind == PropertyKind.List);
        stops.Element!.Kind.ShouldBe(PropertyKind.Struct);
        stops.Element!.Children.Select(c => c.Name).ToArray().ShouldBe(CityZipNames);
    }

    [Fact]
    public void ArrayMemberIsAList()
    {
        var (result, _) = Parse(Row("public int[] Counts { get; init; }"), "App.Row");

        PropertyModel counts = result.Model!.Properties.First(p => p.Kind == PropertyKind.List);
        counts.Element!.Kind.ShouldBe(PropertyKind.Primitive);
    }

    [Fact]
    public void ByteArrayStaysALeafNotAList()
    {
        var (result, _) = Parse(Row("public byte[] Payload { get; init; }"), "App.Row");

        PropertyModel payload = result.Model!.Properties.First(p => p.Name == "Payload");
        payload.Kind.ShouldBe(PropertyKind.ByteArray);
    }

    [Fact]
    public void StringKeyedMapCarriesKeyChildrenAndValueSubtree()
    {
        var (result, _) = Parse(
            Row("public Dictionary<string, string?> Meta { get; init; }"),
            "App.Row"
        );

        PropertyModel meta = result.Model!.Properties.Single(p => p.Kind == PropertyKind.Map);
        meta.Children[0].Name.ShouldBe("key");
        meta.Children[0].IsNullable.ShouldBeFalse(); // MAP keys are REQUIRED
        meta.MapValue.ShouldNotBeNull();
        meta.MapValue!.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void NonStringKeyedDictionaryTriggersPARQ006()
    {
        var (result, _) = Parse(
            Row("public Dictionary<int, string> Scores { get; init; }"),
            "App.Row"
        );

        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
        result.Model.ShouldBeNull(); // emission suppressed while a member is rejected
    }

    [Fact]
    public void UnattributedPocoMemberTriggersPARQ006()
    {
        var (result, _) = Parse(
            Row(
                "public Plain Ship { get; init; }",
                extra: "public class Plain { public int X { get; set; } }"
            ),
            "App.Row"
        );

        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
    }

    [Fact]
    public void SelfReferencingStructTriggersPARQ012()
    {
        string source = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable]
            public partial class Node
            {
                public int Id { get; init; }
                public Node? Next { get; init; }
            }
            """;

        var (result, _) = Parse(source, "App.Node");

        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.NestedTypeCycleDetected.Id
        );
    }

    [Fact]
    public void CycleThroughListTriggersPARQ012()
    {
        string source = """
            using Parquet.SourceGenerator;
            using System.Collections.Generic;

            namespace App;

            [ParquetSerializable]
            public partial class Foo
            {
                public int Id { get; init; }
                public List<Bar>? Kids { get; init; }
            }

            [ParquetSerializable]
            public partial class Bar
            {
                public Foo? Parent { get; init; }
            }
            """;

        var (result, _) = Parse(source, "App.Foo");

        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.NestedTypeCycleDetected.Id
        );
    }

    [Fact]
    public void SiblingsSharingATypeAreNotCycles()
    {
        string source = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable]
            public partial class Address
            {
                public string? City { get; init; }
            }

            [ParquetSerializable]
            public partial class Order
            {
                public Address? Ship { get; init; }
                public Address? Bill { get; init; }
            }
            """;

        var (result, _) = Parse(source, "App.Order");

        result.Diagnostics.ShouldBeEmpty();
        result.Model!.Properties.Length.ShouldBe(2);
    }

    [Fact]
    public void ExcessiveDepthTriggersPARQ013()
    {
        string source = """
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

        var (result, _) = Parse(source, "App.Root");

        // Root -> L1..L7 is seven compound nodes; the seventh exceeds MaxCompoundDepth.
        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.NestedTypeTooDeep.Id
        );
    }

    [Fact]
    public void ExactlyMaxDepthParsesClean()
    {
        string source = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable] public partial class L5 { public int Leaf { get; init; } }
            [ParquetSerializable] public partial class L4 { public L5? Five { get; init; } }
            [ParquetSerializable] public partial class L3 { public L4? Four { get; init; } }
            [ParquetSerializable] public partial class L2 { public L3? Three { get; init; } }
            [ParquetSerializable] public partial class L1 { public L2? Two { get; init; } }
            [ParquetSerializable] public partial class Root { public L1? One { get; init; } }
            """;

        var (result, _) = Parse(source, "App.Root");

        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void ChildViolationsBubbleTheirOwnRules()
    {
        string source = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable]
            public partial class Bad
            {
                public int ReadOnly => 5;
            }

            [ParquetSerializable]
            public partial class Row
            {
                public Bad? Child { get; init; }
            }
            """;

        var (result, _) = Parse(source, "App.Row");

        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.MemberNotAssignable.Id
        );
    }

    [Fact]
    public void GenericChildTriggersPARQ010()
    {
        string source = """
            using Parquet.SourceGenerator;

            namespace App;

            [ParquetSerializable]
            public partial class Wrapper<T>
            {
                public int Id { get; init; }
            }

            [ParquetSerializable]
            public partial class Row
            {
                public Wrapper<int>? Child { get; init; }
            }
            """;

        var (result, _) = Parse(source, "App.Row");

        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.GenericTypeNotSupported.Id
        );
    }

    [Fact]
    public void PipelineWithoutTheFlagStillRejectsCompoundMembers()
    {
        // M1 inertness: the shipping entry point never builds compound models.
        var (result, symbol) = Parse(Row("public Address Ship { get; init; }"), "App.Row");
        result.Model.ShouldNotBeNull(); // compound build for reference

        TargetParserResult viaPipeline = TargetParser.GetTargetModel(
            symbol,
            ParquetApiLevel.V6,
            allowCompoundTypes: false
        );

        viaPipeline.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
        viaPipeline.Model.ShouldBeNull();
    }

    [Fact]
    public void SameTreeFromSeparateCompilationsIsValueEqual()
    {
        // The incremental pipeline caches on model equality; a compound tree must round-trip
        // through Equals exactly like the flat model does.
        string source = Row("public Address Ship { get; init; }");

        var (first, _) = Parse(source, "App.Row");
        var (second, _) = Parse(source, "App.Row");

        first.Model!.ShouldBe(second.Model!);
        first.Model!.GetHashCode().ShouldBe(second.Model!.GetHashCode());
    }
}
