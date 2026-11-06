using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Driver-level coverage for the conditional Apache Arrow emission (#177): the bridge exists only
/// in compilations that reference Apache.Arrow, and toggling that reference must not disturb the
/// main emission's incremental caches.
/// </summary>
public sealed class ArrowConditionalEmissionTests
{
    private const string FlatSource = """
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

            [ParquetColumn("qty")]
            public long? Qty { get; init; }
        }
        """;

    private const string NestedSource = """
        using System;
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Address
        {
            [ParquetColumn("city")]
            public string City { get; init; } = string.Empty;
        }

        [ParquetSerializable]
        public partial class Customer
        {
            [ParquetColumn("id")]
            public int Id { get; init; }

            [ParquetColumn("ship")]
            public Address? Ship { get; init; }
        }
        """;

    private static readonly string[] PocoOnlyHintNames = { "Demo.Trade.ParquetSerializer.g.cs" };

    private static readonly string[] PocoAndArrowHintNames =
    {
        "Demo.Trade.Arrow.g.cs",
        "Demo.Trade.ParquetSerializer.g.cs",
    };

    private static MetadataReference[] BaseReferences()
    {
        return new[]
        {
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
        };
    }

    private static PortableExecutableReference ArrowReference() =>
        MetadataReference.CreateFromFile(
            typeof(global::Apache.Arrow.RecordBatch).Assembly.Location
        );

    private static CSharpCompilation Compilation(string source, bool withArrow)
    {
        List<MetadataReference> references = BaseReferences().ToList();
        if (withArrow)
        {
            references.Add(ArrowReference());
        }

        return CSharpCompilation.Create(
            "ArrowGateAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }

    private static CSharpGeneratorDriver CreateDriver()
    {
        return (CSharpGeneratorDriver)
            CSharpGeneratorDriver.Create(
                new[] { new ParquetIncrementalGenerator().AsSourceGenerator() },
                driverOptions: new GeneratorDriverOptions(
                    IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true
                )
            );
    }

    private static ImmutableArray<GeneratedSourceResult> Run(
        GeneratorDriver driver,
        Compilation compilation,
        out GeneratorDriver updated
    )
    {
        updated = driver.RunGenerators(compilation);
        return updated.GetRunResult().Results.Single().GeneratedSources;
    }

    [Fact]
    public void CompilationWithoutApacheArrowGeneratesZeroArrowTypedCode()
    {
        ImmutableArray<GeneratedSourceResult> sources = Run(
            CreateDriver(),
            Compilation(FlatSource, withArrow: false),
            out _
        );

        sources
            .Select(s => s.HintName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(PocoOnlyHintNames);
        sources.ShouldNotContain(s =>
            s.SourceText.ToString().Contains("Apache.Arrow", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void CompilationWithApacheArrowGeneratesOneExtraFilePerType()
    {
        ImmutableArray<GeneratedSourceResult> sources = Run(
            CreateDriver(),
            Compilation(FlatSource, withArrow: true),
            out _
        );

        sources
            .Select(s => s.HintName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(PocoAndArrowHintNames);

        string arrow = sources
            .Single(s => s.HintName.EndsWith(".Arrow.g.cs", StringComparison.Ordinal))
            .SourceText.ToString();
        arrow.ShouldContain("global::Apache.Arrow.RecordBatch batch", Case.Sensitive);
        arrow.ShouldContain("public static partial class TradeParquetExtensions", Case.Sensitive);
    }

    [Fact]
    public void CompoundModelsGetNoArrowBridgeEvenWithApacheArrowReferenced()
    {
        ImmutableArray<GeneratedSourceResult> sources = Run(
            CreateDriver(),
            Compilation(NestedSource, withArrow: true),
            out _
        );

        // Address is flat, so it gets a bridge. Customer carries a nested group, which #176 has yet
        // to model on the Arrow side, so it deliberately gets none rather than a partial one.
        sources.ShouldContain(s => s.HintName == "Demo.Address.Arrow.g.cs");
        sources.ShouldNotContain(s => s.HintName == "Demo.Customer.Arrow.g.cs");
    }

    [Fact]
    public void AddingTheArrowReferenceRerunsOnlyTheArrowGatedOutput()
    {
        GeneratorDriver driver = CreateDriver();
        CSharpCompilation without = Compilation(FlatSource, withArrow: false);

        ImmutableArray<GeneratedSourceResult> first = Run(driver, without, out driver);
        first.Length.ShouldBe(1);

        Compilation with = without.AddReferences(ArrowReference());
        ImmutableArray<GeneratedSourceResult> second = Run(driver, with, out driver);
        second.Length.ShouldBe(2);

        // The POCO emission's output is byte-identical and its source-output step reports as
        // cached: adding the reference reran the Arrow-gated output and nothing else.
        second
            .Single(s => s.HintName.EndsWith(".ParquetSerializer.g.cs", StringComparison.Ordinal))
            .SourceText.ToString()
            .ShouldBe(first.Single().SourceText.ToString());

        ImmutableArray<IncrementalGeneratorRunStep> outputSteps = driver
            .GetRunResult()
            .Results.Single()
            .TrackedOutputSteps.SelectMany(kvp => kvp.Value)
            .ToImmutableArray();

        outputSteps.ShouldContain(step =>
            step.Outputs.All(o => o.Reason == IncrementalStepRunReason.Cached)
        );
        outputSteps.ShouldContain(step =>
            step.Outputs.Any(o =>
                o.Reason == IncrementalStepRunReason.New
                || o.Reason == IncrementalStepRunReason.Modified
            )
        );
    }

    [Fact]
    public void RemovingTheArrowReferenceRemovesTheBridgeAndKeepsThePocoEmissionCached()
    {
        GeneratorDriver driver = CreateDriver();
        CSharpCompilation with = Compilation(FlatSource, withArrow: true);

        ImmutableArray<GeneratedSourceResult> first = Run(driver, with, out driver);
        first.Length.ShouldBe(2);

        Compilation without = Compilation(FlatSource, withArrow: false);
        ImmutableArray<GeneratedSourceResult> second = Run(driver, without, out driver);

        second.Length.ShouldBe(1);
        second
            .Single()
            .SourceText.ToString()
            .ShouldBe(
                first
                    .Single(s =>
                        s.HintName.EndsWith(".ParquetSerializer.g.cs", StringComparison.Ordinal)
                    )
                    .SourceText.ToString()
            );
    }

    [Fact]
    public void FixedWidthColumnsAreHandedOverWithoutAnArrayPoolRental()
    {
        ImmutableArray<GeneratedSourceResult> sources = Run(
            CreateDriver(),
            Compilation(FlatSource, withArrow: true),
            out _
        );

        string arrow = sources
            .Single(s => s.HintName.EndsWith(".Arrow.g.cs", StringComparison.Ordinal))
            .SourceText.ToString();

        string idBlock = ColumnBlock(arrow, "// column 0: id");
        idBlock.ShouldContain("ArrowFixedWidthMemory<int>", Case.Sensitive);
        idBlock.ShouldNotContain("ArrayPool", Case.Sensitive);

        // The nullable long column cannot be zero-copy: values are packed and def levels derived
        // from the validity bitmap, which does rent.
        string qtyBlock = ColumnBlock(arrow, "// column 2: qty");
        qtyBlock.ShouldContain("WriteAllPartsAsync<long>", Case.Sensitive);
        qtyBlock.ShouldContain("IsValid(row)", Case.Sensitive);
    }

    private static string ColumnBlock(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        (start >= 0).ShouldBeTrue($"marker '{marker}' not found in the generated Arrow bridge.");
        int next = source.IndexOf("// column ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source.Substring(start) : source.Substring(start, next - start);
    }
}
