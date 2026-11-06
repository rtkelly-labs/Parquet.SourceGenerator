using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.ApiGates;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Golden-model code generation suite. For each canonical model in <see cref="GoldenCorpus"/> it
/// asserts:
/// 1. Full C# syntax validity (parses with 0 diagnostics).
/// 2. For driver-generated models, an in-memory Roslyn compilation with 0 errors against Parquet.Net.
/// 3. The model-specific surface claims below.
/// and publishes the emitted source plus its derived API files to
/// <see cref="GoldenCorpus.OutputDirectory"/>. Nothing is compared against a checked-in copy: CI
/// diffs the published output against the pull request's base and posts the diff for review
/// (docs/17-GENERATED-API-BASELINES.md).
/// </summary>
public sealed class GoldenCodeGenRegressionTests
{
    private static GoldenEmission VerifyAndPublish(string fileName)
    {
        GoldenEmission emission = GoldenCorpus.Get(fileName);

        emission.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(emission.Source);
        IEnumerable<Diagnostic> syntaxDiagnostics = syntaxTree
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
        syntaxDiagnostics.ShouldBeEmpty();

        // An emitter that stops producing a public surface at all is a defect, not a diff.
        GeneratedApiBaseline.CountMembers(GoldenCorpus.ApiOf(emission)).ShouldBeGreaterThan(0);

        GoldenCorpus.Publish(emission);
        return emission;
    }

    [Fact]
    public void GoldenMasterComprehensiveModernV6Model() =>
        VerifyAndPublish("OrderEventParquetExtensions.g.cs");

    [Fact]
    public void GoldenMasterScalarsAndEnumsModel() =>
        VerifyAndPublish("ScalarMetricParquetExtensions.g.cs");

    [Fact]
    public void GoldenMasterLegacyV4V5DataColumnModel() =>
        VerifyAndPublish("LegacyRecordParquetLegacyExtensions.g.cs");

    [Fact]
    public void EmittedSourceCompilesCleanlyWithRoslyn()
    {
        string modelSource = """
            namespace GoldenTest;

            using System;
            using Parquet.SourceGenerator;

            [ParquetSerializable]
            public partial record GoldenModel
            {
                [ParquetColumn("id")]
                public int Id { get; init; }

                [ParquetColumn("title")]
                public string Title { get; init; } = string.Empty;

                [ParquetColumn("is_active")]
                public bool IsActive { get; init; }

                [ParquetColumn("created_utc")]
                public DateTime CreatedUtc { get; init; }
            }
            """;

        var (diagnostics, outputTrees) = GoldenCorpus.RunGenerator(modelSource);

        diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        (outputTrees.Count >= 2).ShouldBeTrue(
            "Expected generator to emit at least 1 syntax tree besides the input."
        );

        // Grab emitted extensions source
        SyntaxTree emittedTree = outputTrees[outputTrees.Count - 1];
        string emittedCode = emittedTree.ToString();

        emittedCode.ShouldContain("public static partial class GoldenModelParquetExtensions");
        emittedCode.ShouldContain("WriteParquetAsync");
        emittedCode.ShouldContain("ReadParallelArrayCoreAsync");
        // #479: List<T> is a caller-side conversion, not a second read implementation.
        emittedCode.ShouldNotContain("ReadParallelListCoreAsync");
        emittedCode.ShouldNotContain("ReadListCoreAsync");
        emittedCode.ShouldNotContain("ReadParquetParallelAsync");
    }

    [Fact]
    public void GoldenMasterNestedStructModel() =>
        VerifyAndPublish("NestedOrderParquetExtensions.g.cs");

    [Fact]
    public void GoldenMasterRowLevelListsModel()
    {
        string generated = VerifyAndPublish("ListOrderParquetExtensions.g.cs").Source;
        generated.ShouldContain("ListField(");
        generated.ShouldContain("repLevels_");
    }

    [Fact]
    public void GoldenMasterListOfPocoModel()
    {
        string generated = VerifyAndPublish("PocoOrderParquetExtensions.g.cs").Source;
        generated.ShouldContain("new global::Parquet.Schema.ListField(");
        generated.ShouldContain("StructField(\n");
        generated.ShouldContain(".Item).Fields[");
    }

    [Fact]
    public void GoldenMasterSortedAndPrunableModel()
    {
        GoldenEmission emission = VerifyAndPublish("SortedShipmentParquetExtensions.g.cs");
        emission.Diagnostics.ShouldNotContain(d => d.Descriptor.Id == "PARQ014");

        string generated = emission.Source;
        // Predicate pushdown surface.
        generated.ShouldContain("RowGroupMetadata");
        generated.ShouldContain("bool AcceptRowGroup(");
        generated.ShouldContain("ParquetColumnStatistics<long> Sequence { get; }");
        generated.ShouldContain("ParquetColumnStatistics<int> WeightGrams { get; }");
        generated.ShouldContain("ParquetColumnStatistics<string> Carrier { get; }");

        // Sorted-lookup surface, one pair per key column, over the shared core.
        generated.ShouldContain("bool TryPruneSortedRowGroups<");
        generated.ShouldContain("ReadPrunedRangeAsync<");
        generated.ShouldContain("ReadParquetBySequenceAsync(");
        generated.ShouldContain("ReadParquetByShippedAtAsync(");

        // Eligibility boundaries held where they should: a DateTime key is searched but
        // never projected; a string is projected but can never be searched. Output that
        // starts disagreeing with this is a silent API-surface move.
        generated.ShouldNotContain(
            "ParquetColumnStatistics<System.DateTime> ShippedAt",
            Case.Sensitive
        );
        generated.ShouldNotContain("ReadParquetByCarrierAsync(", Case.Sensitive);
    }

    [Fact]
    public void EveryCorpusModelHasAUniqueFileName()
    {
        GoldenCorpus
            .All.Select(e => e.FileName)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(GoldenCorpus.All.Count);
        GoldenCorpus.All.ShouldAllBe(e => e.FileName.EndsWith(".g.cs", StringComparison.Ordinal));
    }
}
