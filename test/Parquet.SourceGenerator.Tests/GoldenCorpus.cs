extern alias LegacyGenerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.ApiGates;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;
using LegacyEmitter = LegacyGenerator::Parquet.SourceGenerator.Legacy.Emitter.LegacyCodeEmitter;
using LegacyModels = LegacyGenerator::Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Tests;

/// <summary>One golden model: the file name its emitted source is published under, the source
/// itself, and the generator diagnostics (empty for the models emitted without the driver).</summary>
internal sealed record GoldenEmission(
    string FileName,
    string Source,
    IReadOnlyList<Diagnostic> Diagnostics
);

/// <summary>
/// The canonical models whose emitted source is published for review. Nothing here is compared
/// against a checked-in copy: the code is the source of truth. Each emission is written to
/// <see cref="OutputDirectory"/> as <c>Name.g.cs</c>, its signature-only API <c>Name.api.txt</c>
/// and its shape summary <c>Name.api.shape.txt</c>, all from the same string. CI diffs that
/// directory against the pull request's base and posts the result (docs/17-GENERATED-API-BASELINES.md).
/// </summary>
internal static class GoldenCorpus
{
    /// <summary>
    /// <c>GOLDEN_OUTPUT_DIR</c> when set, otherwise <c>artifacts/golden/</c> under the repository
    /// root (gitignored).
    /// </summary>
    public static string OutputDirectory =>
        Environment.GetEnvironmentVariable("GOLDEN_OUTPUT_DIR") is { Length: > 0 } configured
            ? configured
            : IOPath.Combine(FindRepositoryRoot(), "artifacts", "golden");

    private static readonly Lazy<IReadOnlyList<GoldenEmission>> AllEmissions = new(() =>
        new[]
        {
            OrderEvent(),
            ScalarMetric(),
            LegacyRecord(),
            NestedOrder(),
            ListOrder(),
            PocoOrder(),
            SortedShipment(),
        }
    );

    /// <summary>Every golden model, emitted once per test run.</summary>
    public static IReadOnlyList<GoldenEmission> All => AllEmissions.Value;

    public static GoldenEmission Get(string fileName) =>
        All.Single(e => string.Equals(e.FileName, fileName, StringComparison.Ordinal));

    /// <summary>The signature-only API of an emission, in <c>PublicAPI.txt</c> grammar.</summary>
    public static string ApiOf(GoldenEmission emission) =>
        GeneratedApiBaseline.Create(emission.Source);

    /// <summary>
    /// Writes the emission and its two derived API files to <see cref="OutputDirectory"/>.
    /// Idempotent and deterministic: the same emitter writes the same bytes.
    /// </summary>
    public static void Publish(GoldenEmission emission)
    {
        string directory = OutputDirectory;
        IODirectory.CreateDirectory(directory);
        string stem = emission.FileName.Substring(0, emission.FileName.Length - ".g.cs".Length);
        var utf8 = new UTF8Encoding(false);
        IOFile.WriteAllText(
            IOPath.Combine(directory, emission.FileName),
            emission.Source.Replace("\r\n", "\n").TrimEnd() + "\n",
            utf8
        );
        IOFile.WriteAllText(IOPath.Combine(directory, stem + ".api.txt"), ApiOf(emission), utf8);
        IOFile.WriteAllText(
            IOPath.Combine(directory, stem + ".api.shape.txt"),
            GeneratedApiBaseline.CreateShapeSummary(emission.Source),
            utf8
        );
    }

    private static GoldenEmission OrderEvent()
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

        return new GoldenEmission(
            "OrderEventParquetExtensions.g.cs",
            CodeEmitter.EmitSource(model),
            Array.Empty<Diagnostic>()
        );
    }

    private static GoldenEmission ScalarMetric()
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

        return new GoldenEmission(
            "ScalarMetricParquetExtensions.g.cs",
            CodeEmitter.EmitSource(model),
            Array.Empty<Diagnostic>()
        );
    }

    private static GoldenEmission LegacyRecord()
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

        return new GoldenEmission(
            "LegacyRecordParquetLegacyExtensions.g.cs",
            LegacyEmitter.EmitSource(model),
            Array.Empty<Diagnostic>()
        );
    }

    private static GoldenEmission NestedOrder()
    {
        // Compound members through the REAL pipeline (issue #176 M2): driver-generated
        // source must compile clean against Parquet.Net. Covers the value-type-struct
        // ladder branches no reference-type round-trip exercises.
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

        return Driven("NestedOrderParquetExtensions.g.cs", source);
    }

    private static GoldenEmission ListOrder()
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

        return Driven("ListOrderParquetExtensions.g.cs", source);
    }

    private static GoldenEmission PocoOrder()
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

        return Driven("PocoOrderParquetExtensions.g.cs", source);
    }

    private static GoldenEmission SortedShipment()
    {
        // #264 prerequisite: ONE model carrying both row-group pruning mechanisms at once.
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

        return Driven("SortedShipmentParquetExtensions.g.cs", source);
    }

    private static GoldenEmission Driven(string fileName, string source)
    {
        var (diagnostics, outputTrees) = RunGenerator(source);
        return new GoldenEmission(
            fileName,
            outputTrees[outputTrees.Count - 1].ToString(),
            diagnostics
        );
    }

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

    internal static (
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

        // Driver-generated goldens are compiled here, against the real reference set. Assert no
        // binding errors so an emitter regression cannot land as output that merely "looks"
        // stable (#255). The EmitSource-based models are compiled by scripts/CodeMetrics.cs
        // against the declarations in GoldenModels/.
        var genErrors = outputCompilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        genErrors
            .Select(d => $"{d.Id}: {d.Location.GetLineSpan().StartLinePosition.Line}")
            .ShouldBeEmpty();

        return (diagnostics, outputCompilation.SyntaxTrees.ToList());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (IOFile.Exists(IOPath.Combine(directory.FullName, "Parquet.SourceGenerator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
