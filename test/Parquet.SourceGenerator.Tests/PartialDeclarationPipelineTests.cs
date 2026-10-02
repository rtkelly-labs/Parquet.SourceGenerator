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

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Issue #368: the pipeline element used to be a syntax node, so a second declaration of the same
/// type (a partial part carrying any unrelated attribute) produced a second identical model and a
/// second <c>AddSource</c> with the same hint name. <c>AddSource</c> throws on that, which took
/// every generated serializer in the compilation down with it. These tests run both generators,
/// because the legacy one has the same output shape.
/// </summary>
public sealed class PartialDeclarationPipelineTests
{
    public static TheoryData<string> Generators => new() { "current", "legacy" };

    private const string OrderMainPart = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Order
        {
            [ParquetColumn("id")]
            public int Id { get; init; }
        }
        """;

    private const string OrderSecondPart = """
        namespace Demo;

        [System.Diagnostics.DebuggerDisplay("Order {Id}")]
        public partial class Order { }
        """;

    private const string OtherModel = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Other
        {
            [ParquetColumn("name")]
            public string Name { get; init; } = string.Empty;
        }
        """;

    private static IIncrementalGenerator Create(string flavor) =>
        flavor == "legacy"
            ? new LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator()
            : new ParquetIncrementalGenerator();

    private static CSharpCompilation Compile(params string[] sources)
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

        var trees = new List<SyntaxTree>();
        for (int i = 0; i < sources.Length; i++)
        {
            trees.Add(CSharpSyntaxTree.ParseText(sources[i], path: $"File{i}.cs"));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            "PartialPipelineAssembly",
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        return compilation;
    }

    private static GeneratorRunResult Run(string flavor, params string[] sources) =>
        Run(CreateDriver(flavor), Compile(sources), out _);

    private static GeneratorDriver CreateDriver(string flavor) =>
        CSharpGeneratorDriver.Create(
            [Create(flavor).AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true
            )
        );

    private static GeneratorRunResult Run(
        GeneratorDriver driver,
        Compilation compilation,
        out GeneratorDriver updated
    )
    {
        updated = driver.RunGenerators(compilation);
        return updated.GetRunResult().Results.Single();
    }

    private static string[] Hints(GeneratorRunResult result, string suffix) =>
        result
            .GeneratedSources.Select(source => source.HintName)
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();

    [Theory]
    [MemberData(nameof(Generators))]
    public void AnUnrelatedAttributeOnASecondPartialPartDoesNotKillTheGenerator(string flavor)
    {
        GeneratorRunResult result = Run(flavor, OrderMainPart, OrderSecondPart, OtherModel);

        result.Exception.ShouldBeNull();
        string suffix =
            flavor == "legacy" ? ".ParquetLegacySerializer.g.cs" : ".ParquetSerializer.g.cs";
        Hints(result, suffix)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(["Demo.Order" + suffix, "Demo.Other" + suffix]);
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void ASecondPartialPartDoesNotReportEveryDiagnosticTwice(string flavor)
    {
        // PARQ008 rejects the type, so nothing is emitted and no AddSource collision can mask the
        // count: on its own, the diagnostic was reported once per declaration.
        const string NoDefaultCtorMain = """
            using Parquet.SourceGenerator;

            namespace Demo;

            [ParquetSerializable]
            public partial class Positional
            {
                public Positional(int id) { Id = id; }

                [ParquetColumn("id")]
                public int Id { get; init; }
            }
            """;
        const string NoDefaultCtorSecond = """
            namespace Demo;

            [System.Obsolete]
            public partial class Positional { }
            """;

        GeneratorRunResult result = Run(flavor, NoDefaultCtorMain, NoDefaultCtorSecond);

        result.Exception.ShouldBeNull();
        result.GeneratedSources.ShouldBeEmpty();
        result
            .Diagnostics.Count(d => d.Id == DiagnosticDescriptors.NoParameterlessConstructor.Id)
            .ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void TwoFileLocalTypesWithTheSameNameDoNotKillTheGenerator(string flavor)
    {
        // Distinct symbols that flatten to the same Namespace.ClassName hint. File-local types are
        // not a supported target (#402 will say so with a diagnostic); what this pins is that the
        // collision cannot take every other type's output down with it.
        const string TwinA = """
            using Parquet.SourceGenerator;

            namespace Demo;

            [ParquetSerializable]
            file partial class Twin
            {
                [ParquetColumn("a")]
                public int A { get; init; }
            }
            """;
        const string TwinB = """
            using Parquet.SourceGenerator;

            namespace Demo;

            [ParquetSerializable]
            file partial class Twin
            {
                [ParquetColumn("b")]
                public int B { get; init; }
            }
            """;

        GeneratorRunResult result = Run(flavor, TwinA, TwinB, OtherModel);

        result.Exception.ShouldBeNull();
        string suffix =
            flavor == "legacy" ? ".ParquetLegacySerializer.g.cs" : ".ParquetSerializer.g.cs";
        result.GeneratedSources.ShouldContain(source => source.HintName == "Demo.Other" + suffix);
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void GeneratedNameCollisionTracksDeclarationsInOtherFilesOnAReusedDriver(string flavor)
    {
        // PARQ016 depends on the whole set of targets, but it is computed inside each target's
        // parse. A.BC's own file never changes here, so this pins that the parse is re-evaluated
        // against the current compilation: were its result cached per syntax tree, the collision
        // would go stale when AB.C comes and goes.
        const string Nested = """
            using Parquet.SourceGenerator;

            namespace Demo;

            public partial class A
            {
                [ParquetSerializable]
                public partial class BC { [ParquetColumn("x")] public int X { get; init; } }
            }
            """;
        const string Colliding = """
            using Parquet.SourceGenerator;

            namespace Demo;

            public partial class AB
            {
                [ParquetSerializable]
                public partial class C { [ParquetColumn("x")] public int X { get; init; } }
            }
            """;
        string hint =
            flavor == "legacy"
                ? "Demo.A.BC.ParquetLegacySerializer.g.cs"
                : "Demo.A.BC.ParquetSerializer.g.cs";
        CSharpCompilation alone = Compile(Nested, OtherModel);
        CSharpCompilation together = Compile(Nested, OtherModel, Colliding);

        GeneratorRunResult first = Run(CreateDriver(flavor), alone, out GeneratorDriver driver);
        GeneratorRunResult second = Run(driver, together, out driver);
        GeneratorRunResult third = Run(driver, alone, out _);

        static int Collisions(GeneratorRunResult result) =>
            result.Diagnostics.Count(d => d.Id == DiagnosticDescriptors.GeneratedNameCollision.Id);

        Collisions(first).ShouldBe(0);
        first.GeneratedSources.ShouldContain(source => source.HintName == hint);
        Collisions(second).ShouldBe(2, "both targets report once the second one appears");
        second.GeneratedSources.ShouldNotContain(source => source.HintName == hint);
        Collisions(third).ShouldBe(0, "no stale PARQ016 once the collision is gone");
        third.GeneratedSources.ShouldContain(source => source.HintName == hint);
    }
}
