extern alias LegacyGenerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.ApiGates;
using Parquet.SourceGenerator.Diagnostics;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Xunit;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;
using LegacyEmitter = LegacyGenerator::Parquet.SourceGenerator.Legacy.Emitter.LegacyCodeEmitter;
using LegacyModels = LegacyGenerator::Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Golden-master code generation regression suite.
/// Emulates the source generator across diverse canonical models and asserts:
/// 1. Emitted source exactly matches the source-controlled golden .cs file.
/// 2. Full C# syntax validity (parses with 0 diagnostics).
/// 3. In-memory Roslyn compilation with 0 compile errors against Parquet.Net.
/// </summary>
public sealed class GoldenCodeGenRegressionTests
{
    private static readonly string GoldenFilesDir = IOPath.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "..",
        "..",
        "..",
        "GoldenFiles"
    );

    private static bool UpdateGolden =>
        string.Equals(
            Environment.GetEnvironmentVariable("UPDATE_GOLDEN_FILES"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    private static void AssertGoldenMatch(string fileName, string emittedSource)
    {
        string filePath = IOPath.Combine(GoldenFilesDir, fileName);
        bool updateGolden = UpdateGolden;
        string normalizedEmitted = emittedSource.Replace("\r\n", "\n").TrimEnd();

        if (updateGolden || !IOFile.Exists(filePath))
        {
            IODirectory.CreateDirectory(GoldenFilesDir);
            IOFile.WriteAllText(filePath, normalizedEmitted + "\n");
        }

        string expectedSource = IOFile.ReadAllText(filePath).Replace("\r\n", "\n").TrimEnd();

        // 1. Exact string-level consistency against source-controlled golden file
        Assert.Equal(expectedSource, normalizedEmitted);

        // 2. Verify Roslyn parses emitted source without syntax errors
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(emittedSource);
        IEnumerable<Diagnostic> syntaxDiagnostics = syntaxTree
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(syntaxDiagnostics);

        // 3. Signature-only public API baseline, from the same emitted string as the golden file
        //    above, so the two physically cannot drift (issue #215).
        AssertApiBaselineMatch(fileName, emittedSource, updateGolden);
    }

    /// <summary>
    /// Asserts the signature-only baseline that sits next to each golden file. Format and its
    /// rationale are documented on <see cref="GeneratedApiBaseline"/> and in
    /// <c>docs/17-GENERATED-API-BASELINES.md</c>.
    /// </summary>
    private static void AssertApiBaselineMatch(
        string goldenFileName,
        string emittedSource,
        bool updateGolden
    )
    {
        string baselineFileName = BaselineFileNameFor(goldenFileName);
        string baselinePath = IOPath.Combine(GoldenFilesDir, baselineFileName);
        string actual = GeneratedApiBaseline.Create(emittedSource);

        if (updateGolden || !IOFile.Exists(baselinePath))
        {
            IODirectory.CreateDirectory(GoldenFilesDir);
            IOFile.WriteAllText(baselinePath, actual);
        }

        string expected = IOFile.ReadAllText(baselinePath).Replace("\r\n", "\n");

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Assert.Fail(DescribeBaselineDrift(baselineFileName, expected, actual));
        }
    }

    /// <summary>Maps <c>Name.g.cs</c> to its baseline companion <c>Name.api.txt</c>.</summary>
    private static string BaselineFileNameFor(string goldenFileName)
    {
        const string GeneratedSuffix = ".g.cs";
        string stem = goldenFileName.EndsWith(GeneratedSuffix, StringComparison.Ordinal)
            ? goldenFileName.Substring(0, goldenFileName.Length - GeneratedSuffix.Length)
            : IOPath.GetFileNameWithoutExtension(goldenFileName);
        return stem + ".api.txt";
    }

    /// <summary>
    /// Builds a failure message that names the baseline file and the exact members that appeared or
    /// disappeared, rather than dumping two multi-thousand-character strings at the reader.
    /// </summary>
    private static string DescribeBaselineDrift(
        string baselineFileName,
        string expected,
        string actual
    )
    {
        string[] expectedLines = SplitBaseline(expected);
        string[] actualLines = SplitBaseline(actual);

        List<string> removed = expectedLines.Except(actualLines, StringComparer.Ordinal).ToList();
        List<string> added = actualLines.Except(expectedLines, StringComparer.Ordinal).ToList();

        var message = new System.Text.StringBuilder();
        message
            .Append("Generated public API baseline drifted: GoldenFiles/")
            .Append(baselineFileName)
            .Append('\n');
        message.Append(
            "The emitted public surface no longer matches the checked-in baseline. If the change is "
        );
        message.Append(
            "intended, refresh it with UPDATE_GOLDEN_FILES=true (or comment /update-golden on the PR) "
        );
        message.Append("and commit the result.\n");

        foreach (string line in removed)
        {
            message.Append("  - removed: ").Append(line).Append('\n');
        }

        foreach (string line in added)
        {
            message.Append("  + added:   ").Append(line).Append('\n');
        }

        if (removed.Count == 0 && added.Count == 0)
        {
            message.Append(
                "  (no member differences; the file differs only in ordering or whitespace — "
            );
            message.Append("regenerate it rather than hand-editing.)\n");
        }
        else
        {
            message
                .Append("  ")
                .Append(removed.Count)
                .Append(" removed, ")
                .Append(added.Count)
                .Append(" added, ")
                .Append(actualLines.Length)
                .Append(" public members emitted in total.\n");
        }

        return message.ToString();
    }

    private static string[] SplitBaseline(string baseline) =>
        baseline
            .Split('\n')
            .Where(line =>
                line.Length > 0
                && !string.Equals(
                    line,
                    GeneratedApiBaseline.NullableHeader,
                    StringComparison.Ordinal
                )
            )
            .ToArray();

    private static PropertyModel Prop(
        string name,
        string parquetColumnName,
        string typeName,
        int order,
        PropertyKind kind = PropertyKind.Primitive,
        bool isNullable = false,
        string? timestampUnit = null,
        string? enumUnderlyingTypeName = null,
        int? decimalPrecision = null,
        int? decimalScale = null
    ) =>
        new(
            name,
            parquetColumnName,
            typeName,
            timestampUnit,
            enumUnderlyingTypeName,
            order,
            decimalPrecision,
            decimalScale,
            kind,
            isNullable
        );

    private static LegacyModels.PropertyModel LegacyProp(
        string name,
        string parquetColumnName,
        string typeName,
        int order,
        LegacyModels.PropertyKind kind = LegacyModels.PropertyKind.Primitive,
        bool isNullable = false,
        string? timestampUnit = null,
        string? enumUnderlyingTypeName = null,
        int? decimalPrecision = null,
        int? decimalScale = null
    ) =>
        new(
            name,
            parquetColumnName,
            typeName,
            timestampUnit,
            enumUnderlyingTypeName,
            order,
            decimalPrecision,
            decimalScale,
            kind,
            isNullable
        );

    [Fact]
    public void GoldenMasterComprehensiveModernV6Model()
    {
        var properties = new[]
        {
            Prop("Id", "id", "int", 1),
            Prop("Name", "name", "string?", 2, isNullable: true),
            Prop("Score", "score", "double", 3),
            Prop(
                "Price",
                "price",
                "decimal",
                4,
                PropertyKind.Decimal,
                decimalPrecision: 18,
                decimalScale: 4
            ),
            Prop(
                "CreatedAt",
                "created_at",
                "System.DateTime",
                5,
                PropertyKind.DateTime,
                timestampUnit: "Microseconds"
            ),
            Prop("Duration", "duration", "System.TimeSpan", 6, PropertyKind.TimeSpan),
            Prop("CorrelationId", "correlation_id", "System.Guid", 7, PropertyKind.Guid),
            Prop(
                "OptionalGuid",
                "optional_guid",
                "System.Guid?",
                8,
                PropertyKind.Guid,
                isNullable: true
            ),
            Prop("Payload", "payload", "byte[]", 9, PropertyKind.ByteArray, isNullable: true),
        };

        var model = new TargetClassModel(
            Namespace: "SampleDomain.Models",
            ClassName: "OrderEvent",
            Properties: new EquatableArray<PropertyModel>(properties)
        );

        string emittedSource = CodeEmitter.EmitSource(model);
        AssertGoldenMatch("OrderEventParquetExtensions.g.cs", emittedSource);
    }

    [Fact]
    public void GoldenMasterScalarsAndEnumsModel()
    {
        var properties = new[]
        {
            Prop("RowId", "row_id", "long", 1),
            Prop("Flag", "is_valid", "bool", 2),
            Prop("NullableFlag", "maybe_flag", "bool?", 3, isNullable: true),
            Prop(
                "StatusCode",
                "status",
                "SampleDomain.Models.ProcessStatus",
                4,
                PropertyKind.Enum,
                enumUnderlyingTypeName: "int"
            ),
            Prop(
                "OptionalStatus",
                "opt_status",
                "SampleDomain.Models.ProcessStatus?",
                5,
                PropertyKind.Enum,
                isNullable: true,
                enumUnderlyingTypeName: "int"
            ),
            Prop("TinyNum", "tiny_num", "byte", 6),
            Prop("ShortNum", "short_num", "short", 7),
            Prop("FloatVal", "float_val", "float", 8),
        };

        var model = new TargetClassModel(
            Namespace: "SampleDomain.Models",
            ClassName: "ScalarMetric",
            Properties: new EquatableArray<PropertyModel>(properties)
        );

        string emittedSource = CodeEmitter.EmitSource(model);
        AssertGoldenMatch("ScalarMetricParquetExtensions.g.cs", emittedSource);
    }

    [Fact]
    public void GoldenMasterLegacyV4V5DataColumnModel()
    {
        var properties = new[]
        {
            LegacyProp("Id", "id", "int", 1),
            LegacyProp("Description", "desc", "string", 2, isNullable: true),
            LegacyProp(
                "RawData",
                "raw_data",
                "byte[]",
                3,
                LegacyModels.PropertyKind.ByteArray,
                isNullable: true
            ),
            LegacyProp(
                "Level",
                "level",
                "SampleDomain.Models.AccessLevel",
                4,
                LegacyModels.PropertyKind.Enum,
                enumUnderlyingTypeName: "int"
            ),
        };

        var model = new LegacyModels.TargetClassModel(
            Namespace: "SampleDomain.Models",
            ClassName: "LegacyRecord",
            Properties: new LegacyModels.EquatableArray<LegacyModels.PropertyModel>(properties)
        );

        string emittedSource = LegacyEmitter.EmitSource(model);
        AssertGoldenMatch("LegacyRecordParquetLegacyExtensions.g.cs", emittedSource);
    }

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

        var (diagnostics, outputTrees) = RunGenerator(modelSource);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(
            outputTrees.Count >= 2,
            "Expected generator to emit at least 1 syntax tree besides the input."
        );

        // Grab emitted extensions source
        SyntaxTree emittedTree = outputTrees[outputTrees.Count - 1];
        string emittedCode = emittedTree.ToString();

        Assert.Contains("public static partial class GoldenModelParquetExtensions", emittedCode);
        Assert.Contains("WriteParquetAsync", emittedCode);
        Assert.Contains("ReadParquetParallelAsync", emittedCode);
    }

    [Fact]
    public void GoldenMasterNestedStructModel()
    {
        // Compound members through the REAL pipeline (issue #176 M2): driver-generated
        // source is golden-matched and must compile clean against Parquet.Net. Covers the
        // value-type-struct ladder branches no reference-type round-trip exercises.
        string source = """
            using Parquet.SourceGenerator;

            namespace SampleDomain.Models;

            [ParquetSerializable]
            public partial record Address
            {
                public string? City { get; init; }
                public int? Zip { get; init; }
            }

            [ParquetSerializable]
            public partial struct Point
            {
                public int X { get; init; }
                public int Y { get; init; }
            }

            [ParquetSerializable]
            public partial record NestedOrder
            {
                public int Id { get; init; }
                public Address? Ship { get; init; }
                public Address Bill { get; init; } = new();
                public Point Origin { get; init; }
                public Point? Start { get; init; }
            }
            """;

        var (diagnostics, outputTrees) = RunGenerator(source);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        string generated = outputTrees[outputTrees.Count - 1].ToString();
        AssertGoldenMatch("NestedOrderParquetExtensions.g.cs", generated);
    }

    [Fact]
    public void GoldenMasterRowLevelListsModel()
    {
        // M3a (#176): row-level lists/arrays with leaf elements through the real pipeline.
        string source = """
            using Parquet.SourceGenerator;
            using System;
            using System.Collections.Generic;

            namespace SampleDomain.Models;

            [ParquetSerializable]
            public partial record ListOrder
            {
                public int Id { get; init; }
                public List<string?>? Tags { get; init; }
                public List<int> Scores { get; init; } = new();
                public Guid[]? Keys { get; init; }
            }
            """;

        var (diagnostics, outputTrees) = RunGenerator(source);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        string generated = outputTrees[outputTrees.Count - 1].ToString();
        Assert.Contains("ListField(", generated);
        Assert.Contains("repLevels_", generated);
        AssertGoldenMatch("ListOrderParquetExtensions.g.cs", generated);
    }

    [Fact]
    public void GoldenMasterListOfPocoModel()
    {
        // M3b stack 2 (#176): row-level lists/arrays of attributed reference POCOs with
        // leaf children through the real pipeline — 3-level ListField over a StructField
        // element, one lane column per element field, aligned def walk on read.
        string source = """
            using Parquet.SourceGenerator;
            using System;
            using System.Collections.Generic;

            namespace SampleDomain.Models;

            [ParquetSerializable]
            public partial class PitStop
            {
                public string? City { get; init; }
                public int? Zip { get; init; }
                public Guid Node { get; init; }
            }

            [ParquetSerializable]
            public partial record PocoOrder
            {
                public int Id { get; init; }
                public List<PitStop>? Stops { get; init; }
                public PitStop[]? Route { get; init; }
            }
            """;

        var (diagnostics, outputTrees) = RunGenerator(source);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        string generated = outputTrees[outputTrees.Count - 1].ToString();
        Assert.Contains("new global::Parquet.Schema.ListField(", generated);
        Assert.Contains("StructField(\n", generated);
        Assert.Contains(".Item).Fields[", generated);
        AssertGoldenMatch("PocoOrderParquetExtensions.g.cs", generated);
    }

    [Fact]
    public void GoldenMasterSortedAndPrunableModel()
    {
        // #264 prerequisite: ONE model carrying both row-group pruning mechanisms at
        // once. Until now no golden did — the combination that #205 and #203 deferred
        // was measured by nothing and reviewed by no diff. SortedEvent in
        // SortedRowGroupPruningTests.cs proves the merged runtime works, but it emits
        // no checked-in baseline; this closes the coverage gap the convergence needs
        // before it can report its own ELOC_PER_MEMBER claim.
        //
        // The column choices straddle the two eligibility sets deliberately:
        //   Sequence   (long)          — in BOTH sets: the one column the shared
        //                                footer read must serve twice
        //   ShippedAt  (DateTime)      — sortable, never projected into the zone map
        //                                (statistics are physical epoch values)
        //   WeightGrams(int)           — projected for predicates, not opted in as a key
        //   Carrier    (string?)       — projected, and can never be a sort key
        //                                (BYTE_ARRAY order vs culture-sensitive compare)
        string source = """
            using Parquet.SourceGenerator;
            using System;

            namespace SampleDomain.Models;

            [ParquetSerializable]
            public partial class SortedShipment
            {
                [ParquetSortKey]
                public long Sequence { get; set; }
                [ParquetSortKey]
                public System.DateTime ShippedAt { get; set; }
                public int WeightGrams { get; set; }
                public string? Carrier { get; set; }
            }
            """;

        var (diagnostics, outputTrees) = RunGenerator(source);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, d => d.Descriptor.Id == "PARQ014");

        string generated = outputTrees[outputTrees.Count - 1].ToString();
        // Predicate pushdown surface.
        Assert.Contains("RowGroupMetadata", generated);
        Assert.Contains("bool AcceptRowGroup(", generated);
        Assert.Contains("ParquetColumnStatistics<long> Sequence { get; }", generated);
        Assert.Contains("ParquetColumnStatistics<int> WeightGrams { get; }", generated);
        Assert.Contains("ParquetColumnStatistics<string> Carrier { get; }", generated);

        // Sorted-lookup surface, one pair per key column, over the shared core.
        Assert.Contains("bool TryPruneSortedRowGroups<", generated);
        Assert.Contains("ReadPrunedRangeAsync<", generated);
        Assert.Contains("ReadParquetBySequenceAsync(", generated);
        Assert.Contains("ReadParquetByShippedAtAsync(", generated);

        // Eligibility boundaries held where they should: a DateTime key is searched but
        // never projected; a string is projected but can never be searched. A golden
        // that starts disagreeing with this is a silent API-surface move.
        Assert.DoesNotContain(
            "ParquetColumnStatistics<System.DateTime> ShippedAt",
            generated,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("ReadParquetByCarrierAsync(", generated, StringComparison.Ordinal);

        AssertGoldenMatch("SortedShipmentParquetExtensions.g.cs", generated);
    }

    private static (
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<SyntaxTree> OutputTrees
    ) RunGenerator(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        // The real runtime reference set (filtered TPA): an emitted source that only
        // "looks" stable but would not bind in a consumer project fails here (#255).
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpaJoined)
        {
            throw new InvalidOperationException("TPA unavailable");
        }
        string[] tpa = tpaJoined.Split(Path.PathSeparator);
        var references = tpa.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase))
            .Where(p =>
                Path.GetFileName(p).StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p)
                    .StartsWith("Microsoft.Win32", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p)
                    is "netstandard.dll"
                        or "mscorlib.dll"
                        or "Parquet.dll"
                        or "Parquet.SourceGenerator.Attributes.dll"
            )
            .Distinct(StringComparer.Ordinal)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        CSharpCompilation compilation = CSharpCompilation.Create(
            "GoldenTestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        var generator = new ParquetIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out var diagnostics
        );

        // Goldens are shipped as .g.cs text excluded from this project's compilation — the
        // only place their generated bodies are compiled is here. Assert no binding errors so
        // an emitter regression cannot land as a golden that merely "looks" stable (#255).
        var genErrors = outputCompilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(
            genErrors.Select(d => $"{d.Id}: {d.Location.GetLineSpan().StartLinePosition.Line}")
        );

        return (diagnostics, outputCompilation.SyntaxTrees.ToList());
    }
}
