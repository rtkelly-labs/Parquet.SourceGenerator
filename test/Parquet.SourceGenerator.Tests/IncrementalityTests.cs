using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Driver-level evidence for issue #258: supported Roslyn tracked steps expose incremental
/// boundaries, unrelated edits stay cached, and a model edit invalidates only that model's output.
/// </summary>
public sealed class IncrementalityTests
{
    private const string ModelASource = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class ModelA
        {
            [ParquetColumn("id")]
            public int Id { get; init; }
        }
        """;

    private const string ModelBSource = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class ModelB
        {
            [ParquetColumn("name")]
            public string Name { get; init; } = string.Empty;
        }
        """;

    private const string UnrelatedSource = """
        namespace Demo;

        internal static class Unrelated
        {
            public const int Value = 1;
        }
        """;

    private static CSharpCompilation CreateCompilation(
        SyntaxTree modelA,
        SyntaxTree modelB,
        SyntaxTree unrelated
    )
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

        return CSharpCompilation.Create(
            "IncrementalityAssembly",
            [modelA, modelB, unrelated],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }

    private static CSharpGeneratorDriver CreateDriver() =>
        (CSharpGeneratorDriver)
            CSharpGeneratorDriver.Create(
                [new ParquetIncrementalGenerator().AsSourceGenerator()],
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

    private static IEnumerable<IncrementalGeneratorRunStep> AllTrackedOutputSteps(
        GeneratorRunResult result
    ) => result.TrackedOutputSteps.Values.SelectMany(steps => steps);

    private static string DescribeTrackedSteps(GeneratorRunResult result) =>
        string.Join(
            "; ",
            result.TrackedSteps.SelectMany(pair =>
                pair.Value.Select(step =>
                    $"{pair.Key}={string.Join(',', step.Outputs.Select(output => output.Reason))}"
                )
            )
        );

    [Fact]
    public void TrackedOutputStepsForAnUnrelatedFileEditAreCached()
    {
        SyntaxTree modelA = CSharpSyntaxTree.ParseText(ModelASource);
        SyntaxTree modelB = CSharpSyntaxTree.ParseText(ModelBSource);
        SyntaxTree unrelated = CSharpSyntaxTree.ParseText(UnrelatedSource);
        CSharpCompilation initial = CreateCompilation(modelA, modelB, unrelated);

        GeneratorRunResult first = Run(CreateDriver(), initial, out GeneratorDriver driver);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(
            unrelated,
            CSharpSyntaxTree.ParseText(UnrelatedSource.Replace("Value = 1", "Value = 2"))
        );

        GeneratorRunResult second = Run(driver, edited, out _);

        AllTrackedOutputSteps(second)
            .SelectMany(step => step.Outputs)
            .ShouldAllBe(
                output => output.Reason == IncrementalStepRunReason.Cached,
                DescribeTrackedSteps(second)
            );
        second
            .GeneratedSources.Select(source => source.SourceText.ToString())
            .OrderBy(source => source, StringComparer.Ordinal)
            .ShouldBe(
                first
                    .GeneratedSources.Select(source => source.SourceText.ToString())
                    .OrderBy(source => source, StringComparer.Ordinal)
                    .ToArray()
            );
    }

    [Fact]
    public void TrackedOutputStepsForAPerModelEditAreSelective()
    {
        SyntaxTree modelA = CSharpSyntaxTree.ParseText(ModelASource);
        SyntaxTree modelB = CSharpSyntaxTree.ParseText(ModelBSource);
        SyntaxTree unrelated = CSharpSyntaxTree.ParseText(UnrelatedSource);
        CSharpCompilation initial = CreateCompilation(modelA, modelB, unrelated);

        GeneratorRunResult first = Run(CreateDriver(), initial, out GeneratorDriver driver);
        SyntaxTree editedModelA = CSharpSyntaxTree.ParseText(
            ModelASource.Replace("public int Id", "public long Id")
        );
        CSharpCompilation edited = initial.ReplaceSyntaxTree(modelA, editedModelA);

        GeneratorRunResult second = Run(driver, edited, out _);

        var modelOutputs = AllTrackedOutputSteps(second).SelectMany(step => step.Outputs).ToArray();
        modelOutputs
            .Count(output => output.Reason == IncrementalStepRunReason.Modified)
            .ShouldBe(1, DescribeTrackedSteps(second));
        modelOutputs
            .Count(output => output.Reason == IncrementalStepRunReason.Cached)
            // The configuration diagnostic output is independently registered and remains cached
            // alongside the unaffected model and Arrow output steps.
            .ShouldBe(3, DescribeTrackedSteps(second));
        modelOutputs
            .Count(output => output.Reason == IncrementalStepRunReason.Unchanged)
            .ShouldBe(1, DescribeTrackedSteps(second));

        second
            .GeneratedSources.Single(source =>
                source.HintName == "Demo.ModelA.ParquetSerializer.g.cs"
            )
            .SourceText.ToString()
            .ShouldNotBe(
                first
                    .GeneratedSources.Single(source =>
                        source.HintName == "Demo.ModelA.ParquetSerializer.g.cs"
                    )
                    .SourceText.ToString()
            );
        second
            .GeneratedSources.Single(source =>
                source.HintName == "Demo.ModelB.ParquetSerializer.g.cs"
            )
            .SourceText.ToString()
            .ShouldBe(
                first
                    .GeneratedSources.Single(source =>
                        source.HintName == "Demo.ModelB.ParquetSerializer.g.cs"
                    )
                    .SourceText.ToString()
            );
    }

    [Fact]
    public void ModelEqualityIncludesNestedStateAndUsesValueHashes()
    {
        PropertyModel leaf = new(
            "Id",
            "id",
            "int",
            TimestampUnit: null,
            EnumUnderlyingTypeName: null,
            Order: 0,
            DecimalPrecision: null,
            DecimalScale: null,
            Kind: PropertyKind.Primitive,
            IsNullable: false
        );
        PropertyModel nested = new(
            "Address",
            "address",
            "Address",
            TimestampUnit: null,
            EnumUnderlyingTypeName: null,
            Order: 0,
            DecimalPrecision: null,
            DecimalScale: null,
            Kind: PropertyKind.Struct,
            IsNullable: false
        )
        {
            Children = new EquatableArray<PropertyModel>([leaf]),
            CompoundIsValueType = true,
        };
        PropertyModel equalNested = nested with
        {
            Children = new EquatableArray<PropertyModel>([leaf with { }]),
        };
        PropertyModel changedNested = nested with
        {
            Children = new EquatableArray<PropertyModel>([leaf with { TypeName = "long" }]),
        };

        nested.ShouldBe(equalNested);
        nested.GetHashCode().ShouldBe(equalNested.GetHashCode());
        nested.ShouldNotBe(changedNested);
        nested.ShouldNotBe(nested with { Element = leaf });
        nested.ShouldNotBe(nested with { MapValue = leaf });
        nested.ShouldNotBe(nested with { IsSortKey = true });
        nested.ShouldNotBe(nested with { CompoundIsValueType = false });

        TargetClassModel first = new("Demo", "Model", new EquatableArray<PropertyModel>([nested]));
        TargetClassModel equal = new(
            "Demo",
            "Model",
            new EquatableArray<PropertyModel>([equalNested])
        );
        TargetClassModel changed = new(
            "Demo",
            "Model",
            new EquatableArray<PropertyModel>([changedNested])
        );

        first.ShouldBe(equal);
        first.GetHashCode().ShouldBe(equal.GetHashCode());
        first.ShouldNotBe(changed);
    }
}
