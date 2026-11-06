extern alias LegacyGenerator;

using System;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Xunit;

using LegacyModels = LegacyGenerator::Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Tests;

public class LegacyEmitterTests
{
    [Fact]
    public void LegacyCodeEmitterEmitsValidDataColumnBasedCode()
    {
        string code = Emit(
            Prop("Id", "id", "int", LegacyModels::PropertyKind.Primitive, isNullable: false),
            Prop("Name", "name", "string", LegacyModels::PropertyKind.Primitive, isNullable: true)
        );

        Assert.Contains("namespace TestNamespace;", code);
        Assert.Contains("public static partial class TestModelParquetLegacyExtensions", code);
        Assert.Contains(
            "public static readonly global::Parquet.Schema.ParquetSchema Schema = new global::Parquet.Schema.ParquetSchema(",
            code
        );
        Assert.Contains("new global::Parquet.Data.DataColumn(_field_0, colArray_0)", code);
        Assert.Contains("rgWriter.WriteColumnAsync(col_0, cancellationToken)", code);
        Assert.Contains("rgReader.ReadColumnAsync(field_0, cancellationToken)", code);
    }

    /// <summary>
    /// <c>byte[]</c> is the one column element type that is itself an array, and the array-creation
    /// expression was built as <c>new {elementType}[count]</c> — yielding <c>new byte[][count]</c>,
    /// which does not parse. Any model with a <c>byte[]</c> column produced an uncompilable file.
    /// </summary>
    [Fact]
    public void ByteArrayColumnCreatesAJaggedArrayWithTheRankAfterTheLength()
    {
        string code = Emit(
            Prop(
                "Payload",
                "payload",
                "byte[]",
                LegacyModels::PropertyKind.ByteArray,
                isNullable: true
            )
        );

        Assert.Contains("var colArray_0 = new byte[count][];", code);
        Assert.DoesNotContain("new byte[][count]", code);

        // The read cast has to name the jagged type, where the rank does belong at the end.
        // The optional column is read through the schema-evolution ternary, so the cast sits on the
        // awaited read rather than on a separate col_0 local.
        Assert.Contains("? new byte[groupRows][]", code);
        Assert.Contains("(byte[][])(await rgReader.ReadColumnAsync(field_0", code);
    }

    /// <summary>
    /// DataColumn validates the array's element type against
    /// <c>DataField.ClrNullableIfHasNullsType</c> and throws on a mismatch, so a nullable column
    /// needs a nullable array. The emitter used to drop the <c>?</c> for enums and cast straight
    /// through, which could not represent a null and threw at the cast before it got the chance to.
    /// </summary>
    [Fact]
    public void NullableEnumColumnKeepsItsNullsOnBothSides()
    {
        string code = Emit(
            Prop(
                "Grade",
                "grade",
                "global::MyApp.Grade?",
                LegacyModels::PropertyKind.Enum,
                isNullable: true,
                enumUnderlyingTypeName: "int"
            )
        );

        Assert.Contains("var colArray_0 = new int?[count];", code);
        Assert.Contains("item.Grade is null ? (int?)null : (int)item.Grade.Value", code);
        Assert.Contains(
            "data_0[k] is null ? (global::MyApp.Grade?)null : (global::MyApp.Grade)data_0[k]!",
            code
        );
    }

    [Fact]
    public void NonNullableEnumColumnUsesTheBareUnderlyingType()
    {
        string code = Emit(
            Prop(
                "Grade",
                "grade",
                "global::MyApp.Grade",
                LegacyModels::PropertyKind.Enum,
                isNullable: false,
                enumUnderlyingTypeName: "int"
            )
        );

        Assert.Contains("var colArray_0 = new int[count];", code);
        Assert.Contains("colArray_0[k] = (int)item.Grade;", code);
    }

    /// <summary>
    /// Parquet.Net 4.x/5.x keeps compression on the writer, not on <c>ParquetOptions</c>, so a
    /// <c>BuildFormatOptions</c> that only populated the options object discarded every setting —
    /// a Gzip request silently wrote Snappy.
    /// </summary>
    [Fact]
    public void CompressionIsAppliedToTheWriterFromEveryWriteEntryPoint()
    {
        string code = Emit(
            Prop("Id", "id", "int", LegacyModels::PropertyKind.Primitive, isNullable: false)
        );

        Assert.Contains("writer.CompressionMethod = options.CompressionMethod switch", code);
        Assert.Contains(
            "writer.CompressionLevel = global::System.IO.Compression.CompressionLevel.Fastest;",
            code
        );

        // Both WriteParquetAsync and WriteParquetBatchedAsync must call it, or the batched path
        // quietly keeps the default while the simple path honours the option.
        Assert.Equal(2, CountOccurrences(code, "ApplyCompression(writer, options);"));
    }

    /// <summary>
    /// <c>CompressionLevel.SmallestSize</c> arrived in .NET 6. The generated code compiles inside
    /// the consumer's project, so naming it unguarded would break exactly the .NET Framework
    /// consumers this backend exists to serve.
    /// </summary>
    [Fact]
    public void SmallestSizeCompressionLevelIsGuardedForPreNet6Consumers()
    {
        string code = Emit(
            Prop("Id", "id", "int", LegacyModels::PropertyKind.Primitive, isNullable: false)
        );

        int guardStart = code.IndexOf("#if NET6_0_OR_GREATER", StringComparison.Ordinal);
        int guardEnd = code.IndexOf("#endif", StringComparison.Ordinal);
        int smallestSize = code.IndexOf(
            "global::System.IO.Compression.CompressionLevel.SmallestSize",
            StringComparison.Ordinal
        );

        Assert.True(
            guardStart >= 0,
            "The emitted code should guard the .NET 6+ compression level."
        );
        Assert.InRange(smallestSize, guardStart, guardEnd);
    }

    /// <summary>
    /// The reader was created without options at all — the defect audit item 3.1 closed on the v6
    /// side, reintroduced by the classic backend.
    /// </summary>
    [Fact]
    public void ReaderReceivesTheFormatOptions()
    {
        string code = Emit(
            Prop("Id", "id", "int", LegacyModels::PropertyKind.Primitive, isNullable: false)
        );

        Assert.Contains("global::Parquet.ParquetReader.CreateAsync(", code);
        Assert.Contains("BuildFormatOptions(options)", code);
    }

    /// <summary>
    /// Field resolution is a property of the file, not of a row group. Doing it per row group also
    /// re-invoked <c>GetDataFields()</c>, which allocates a fresh array on every call.
    /// </summary>
    [Fact]
    public void SchemaFieldsAreResolvedOncePerFileRatherThanPerRowGroup()
    {
        string code = Emit(
            Prop("Id", "id", "int", LegacyModels::PropertyKind.Primitive, isNullable: false),
            Prop("Name", "name", "string", LegacyModels::PropertyKind.Primitive, isNullable: true)
        );

        Assert.Equal(1, CountOccurrences(code, "reader.Schema.GetDataFields()"));
        Assert.Equal(
            1,
            CountOccurrences(code, "ResolveSchemaField(fileFields, 0, _field_0, ref fieldsByName,")
        );

        // Row counts come from row-group metadata, so the file is not walked twice just to total them.
        Assert.Contains("totalRows += (int)reader.RowGroups[r].RowCount;", code);
    }

    /// <summary>
    /// Parquet.Net 4.25's <c>SchemaEncoder.SupportedTypes</c> has no <c>ReadOnlyMemory&lt;T&gt;</c>
    /// entry, so the shared v6 allowlist would have let the classic backend emit a column that fails
    /// at runtime. PARQ011 says so at compile time, and says which package to use instead.
    /// </summary>
    [Fact]
    public void TypeSupportedOnlyByV6TriggersPARQ011OnTheClassicBackend()
    {
        string source = """
            using System;
            using Parquet.SourceGenerator;

            [ParquetSerializable]
            public partial class BufferRow
            {
                [ParquetColumn("payload")]
                public ReadOnlyMemory<byte> Payload { get; set; }
            }
            """;

        Assert.Contains(
            RunLegacyGenerator(source),
            d => d.Id == DiagnosticDescriptors.TypeUnsupportedOnClassicApi.Id
        );
    }

    [Fact]
    public void TypesSupportedByBothApiGenerationsAreLeftAlone()
    {
        string source = """
            using System;
            using Parquet.SourceGenerator;

            [ParquetSerializable]
            public partial class PlainRow
            {
                [ParquetColumn("id")]
                public int Id { get; set; }

                [ParquetColumn("name")]
                public string? Name { get; set; }

                [ParquetColumn("payload")]
                public byte[]? Payload { get; set; }

                [ParquetColumn("at")]
                public DateTime At { get; set; }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunLegacyGenerator(source);

        Assert.DoesNotContain(
            diagnostics,
            d => d.Id == DiagnosticDescriptors.TypeUnsupportedOnClassicApi.Id
        );
        Assert.DoesNotContain(
            diagnostics,
            d => d.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
    }

    [Fact]
    public void LegacyEmittedCodeContainsReadParquetArrayAsyncAndSmallBatchOptimization()
    {
        string code = Emit(
            Prop("Id", "id", "int", LegacyModels::PropertyKind.Primitive, isNullable: false)
        );

        // Direct array reader overload
        Assert.Contains("Task<TestModel[]> ReadParquetArrayAsync(", code);

        // Batched write small-collection fast path
        Assert.Contains(
            "if (items is global::System.Collections.Generic.IReadOnlyList<TestModel> list && list.Count <= batchSize)",
            code
        );
    }

    [Fact]
    public void LegacyCodeEmitterEmitsAllSupportedTypesCorrectly()
    {
        string code = Emit(AllSupportedLegacyProperties());

        Assert.Contains("var colArray_10 = new global::System.Guid[count];", code);
        Assert.Contains("var colArray_11 = new global::System.Guid?[count];", code);
        Assert.Contains("struct StringDeduplicator", code);
        Assert.Contains("ReadColumnAsync", code);
        Assert.Contains("WriteColumnAsync", code);
    }

    [Fact]
    public void LegacyCodeEmitterEmptyModelEmitsCompletedTaskAndEmptyArray()
    {
        string code = Emit();

        Assert.Contains(
            "await global::System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);",
            code
        );
        Assert.Contains("return global::System.Array.Empty<TestModel>();", code);
    }

    [Fact]
    public void LegacyCodeEmitterModelWithoutNamespaceEmitsTopLevelClass()
    {
        var model = new LegacyModels::TargetClassModel(
            "",
            "GlobalModel",
            new LegacyModels::EquatableArray<LegacyModels::PropertyModel>(
                new[]
                {
                    Prop(
                        "Id",
                        "id",
                        "int",
                        LegacyModels::PropertyKind.Primitive,
                        isNullable: false
                    ),
                }
            )
        );

        string code =
            LegacyGenerator::Parquet.SourceGenerator.Legacy.Emitter.LegacyCodeEmitter.EmitSource(
                model
            );

        Assert.DoesNotContain("namespace ;", code);
        Assert.Contains("public static partial class GlobalModelParquetLegacyExtensions", code);
    }

    [Fact]
    public void LegacyCodeEmitterSingleFieldBlittableStructEmitsFastPath()
    {
        var model = new LegacyModels::TargetClassModel(
            "TestNamespace",
            "BlittableInt",
            new LegacyModels::EquatableArray<LegacyModels::PropertyModel>(
                new[]
                {
                    Prop(
                        "Value",
                        "val",
                        "int",
                        LegacyModels::PropertyKind.Primitive,
                        isNullable: false
                    ),
                }
            ),
            IsValueType: true,
            IsUnmanaged: true,
            HasSingleInstanceField: true
        );

        string code =
            LegacyGenerator::Parquet.SourceGenerator.Legacy.Emitter.LegacyCodeEmitter.EmitSource(
                model
            );

        Assert.Contains(
            "global::System.Runtime.InteropServices.MemoryMarshal.Cast<BlittableInt, int>",
            code
        );
        Assert.Contains(
            "items is global::System.Collections.Generic.List<BlittableInt> listItems",
            code
        );
        Assert.Contains("items is BlittableInt[] arrayItems", code);
    }

    [Fact]
    public void IncrementalGeneratorRunsOnComprehensiveModel()
    {
        string source = """
            using System;
            using Parquet.SourceGenerator;

            namespace TestApp;

            [ParquetSerializable]
            public partial class FullModel
            {
                [ParquetColumn("id")]
                public int Id { get; set; }

                [ParquetColumn("nullable_id")]
                public int? NullableId { get; set; }

                [ParquetColumn("name")]
                public string Name { get; set; } = string.Empty;

                [ParquetColumn("desc")]
                public string? Description { get; set; }

                [ParquetColumn("score")]
                public double Score { get; set; }

                [ParquetColumn("active")]
                public bool IsActive { get; set; }

                [ParquetColumn("uid")]
                public Guid Uid { get; set; }

                [ParquetColumn("created_at")]
                public DateTime CreatedAt { get; set; }

                [ParquetColumn("span")]
                public TimeSpan Span { get; set; }

                [ParquetColumn("date")]
                public DateOnly Date { get; set; }

                [ParquetColumn("time")]
                public TimeOnly Time { get; set; }

                [ParquetColumn("price")]
                [ParquetDecimal(18, 4)]
                public decimal Price { get; set; }

                [ParquetColumn("bytes")]
                public byte[] Bytes { get; set; } = Array.Empty<byte>();

                [ParquetColumn("status")]
                public ItemStatus Status { get; set; }

                [ParquetIgnore]
                public string Ignored { get; set; } = string.Empty;
            }

            public enum ItemStatus { None, Active, Deleted }
            """;

        var (diagnostics, trees) = RunLegacyGeneratorWithOutput(source);

        Assert.Empty(diagnostics);
        Assert.Single(trees);
        string generatedSource = trees[0].ToString();
        Assert.Contains("FullModelParquetLegacyExtensions", generatedSource);
        Assert.Contains("WriteRowGroupAsync", generatedSource);
        Assert.Contains("ReadParquetArrayAsync", generatedSource);
    }

    [Fact]
    public void IncrementalGeneratorRunsOnRecordAndStructDeclarations()
    {
        string source = """
            using Parquet.SourceGenerator;

            namespace TestApp;

            [ParquetSerializable]
            public partial struct StructModel
            {
                [ParquetColumn("val")]
                public int Val { get; set; }
            }

            [ParquetSerializable]
            public partial record RecordModel
            {
                [ParquetColumn("id")]
                public int Id { get; init; }
            }
            """;

        var (diagnostics, trees) = RunLegacyGeneratorWithOutput(source);

        Assert.Empty(diagnostics);
        Assert.Equal(2, trees.Length);
    }

    [Fact]
    public void IncrementalGeneratorReportsDiagnosticErrors()
    {
        string notPartial = """
            using Parquet.SourceGenerator;
            [ParquetSerializable]
            public class NotPartial { public int Id { get; set; } }
            """;
        Assert.Contains(
            RunLegacyGenerator(notPartial),
            d => d.Id == DiagnosticDescriptors.MustBePartial.Id
        );

        string duplicateProp = """
            using Parquet.SourceGenerator;
            [ParquetSerializable]
            public partial class DuplicateProp
            {
                [ParquetColumn("id")]
                public int A { get; set; }
                [ParquetColumn("id")]
                public int B { get; set; }
            }
            """;
        Assert.Contains(
            RunLegacyGenerator(duplicateProp),
            d => d.Id == DiagnosticDescriptors.DuplicateColumnName.Id
        );

        string generic = """
            using Parquet.SourceGenerator;
            [ParquetSerializable]
            public partial class GenericClass<T> { public T? Value { get; set; } }
            """;
        Assert.Contains(
            RunLegacyGenerator(generic),
            d => d.Id == DiagnosticDescriptors.GenericTypeNotSupported.Id
        );
    }

    // ──────────────────────────────────────────────────────────
    //  HELPERS
    // ──────────────────────────────────────────────────────────

    private static LegacyModels::PropertyModel Prop(
        string name,
        string columnName,
        string typeName,
        LegacyModels::PropertyKind kind,
        bool isNullable,
        string? enumUnderlyingTypeName = null
    ) =>
        new(
            name,
            columnName,
            typeName,
            null,
            enumUnderlyingTypeName,
            1,
            null,
            null,
            kind,
            isNullable
        );

    private static LegacyModels::PropertyModel[] AllSupportedLegacyProperties() =>
        [
            Prop("IntCol", "int_col", "int", LegacyModels::PropertyKind.Primitive, false),
            Prop(
                "NullableIntCol",
                "nullable_int_col",
                "int?",
                LegacyModels::PropertyKind.Primitive,
                true
            ),
            Prop("LongCol", "long_col", "long", LegacyModels::PropertyKind.Primitive, false),
            Prop(
                "NullableLongCol",
                "nullable_long_col",
                "long?",
                LegacyModels::PropertyKind.Primitive,
                true
            ),
            Prop("FloatCol", "float_col", "float", LegacyModels::PropertyKind.Primitive, false),
            Prop(
                "NullableFloatCol",
                "nullable_float_col",
                "float?",
                LegacyModels::PropertyKind.Primitive,
                true
            ),
            Prop("DoubleCol", "double_col", "double", LegacyModels::PropertyKind.Primitive, false),
            Prop(
                "NullableDoubleCol",
                "nullable_double_col",
                "double?",
                LegacyModels::PropertyKind.Primitive,
                true
            ),
            Prop("BoolCol", "bool_col", "bool", LegacyModels::PropertyKind.Primitive, false),
            Prop(
                "NullableBoolCol",
                "nullable_bool_col",
                "bool?",
                LegacyModels::PropertyKind.Primitive,
                true
            ),
            Prop(
                "GuidCol",
                "guid_col",
                "global::System.Guid",
                LegacyModels::PropertyKind.Guid,
                false
            ),
            Prop(
                "NullableGuidCol",
                "nullable_guid_col",
                "global::System.Guid?",
                LegacyModels::PropertyKind.Guid,
                true
            ),
            Prop(
                "DateTimeCol",
                "datetime_col",
                "global::System.DateTime",
                LegacyModels::PropertyKind.DateTime,
                false
            ),
            Prop(
                "NullableDateTimeCol",
                "nullable_datetime_col",
                "global::System.DateTime?",
                LegacyModels::PropertyKind.DateTime,
                true
            ),
            Prop(
                "DateTimeOffsetCol",
                "dto_col",
                "global::System.DateTimeOffset",
                LegacyModels::PropertyKind.DateTime,
                false
            ),
            Prop(
                "NullableDateTimeOffsetCol",
                "nullable_dto_col",
                "global::System.DateTimeOffset?",
                LegacyModels::PropertyKind.DateTime,
                true
            ),
            Prop(
                "TimeSpanCol",
                "timespan_col",
                "global::System.TimeSpan",
                LegacyModels::PropertyKind.TimeSpan,
                false
            ),
            Prop(
                "NullableTimeSpanCol",
                "nullable_timespan_col",
                "global::System.TimeSpan?",
                LegacyModels::PropertyKind.TimeSpan,
                true
            ),
            Prop(
                "DateOnlyCol",
                "date_col",
                "global::System.DateOnly",
                LegacyModels::PropertyKind.Primitive,
                false
            ),
            Prop(
                "NullableDateOnlyCol",
                "nullable_date_col",
                "global::System.DateOnly?",
                LegacyModels::PropertyKind.Primitive,
                true
            ),
            Prop(
                "TimeOnlyCol",
                "time_col",
                "global::System.TimeOnly",
                LegacyModels::PropertyKind.TimeOnly,
                false
            ),
            Prop(
                "NullableTimeOnlyCol",
                "nullable_time_col",
                "global::System.TimeOnly?",
                LegacyModels::PropertyKind.TimeOnly,
                true
            ),
            Prop("DecimalCol", "decimal_col", "decimal", LegacyModels::PropertyKind.Decimal, false),
            Prop(
                "NullableDecimalCol",
                "nullable_decimal_col",
                "decimal?",
                LegacyModels::PropertyKind.Decimal,
                true
            ),
            Prop("StringCol", "string_col", "string", LegacyModels::PropertyKind.Primitive, false),
            Prop(
                "NullableStringCol",
                "nullable_string_col",
                "string?",
                LegacyModels::PropertyKind.Primitive,
                true
            ),
            Prop(
                "ByteArrayCol",
                "bytes_col",
                "byte[]",
                LegacyModels::PropertyKind.ByteArray,
                false
            ),
            Prop(
                "NullableByteArrayCol",
                "nullable_bytes_col",
                "byte[]?",
                LegacyModels::PropertyKind.ByteArray,
                true
            ),
            Prop(
                "EnumCol",
                "enum_col",
                "global::MyApp.Status",
                LegacyModels::PropertyKind.Enum,
                false,
                "int"
            ),
            Prop(
                "NullableEnumCol",
                "nullable_enum_col",
                "global::MyApp.Status?",
                LegacyModels::PropertyKind.Enum,
                true,
                "int"
            ),
        ];

    private static string Emit(params LegacyModels::PropertyModel[] properties)
    {
        var model = new LegacyModels::TargetClassModel(
            "TestNamespace",
            "TestModel",
            new LegacyModels::EquatableArray<LegacyModels::PropertyModel>(properties)
        );

        return LegacyGenerator::Parquet.SourceGenerator.Legacy.Emitter.LegacyCodeEmitter.EmitSource(
            model
        );
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (
            int i = haystack.IndexOf(needle, StringComparison.Ordinal);
            i >= 0;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }

    private static ImmutableArray<Diagnostic> RunLegacyGenerator(string source) =>
        RunLegacyGeneratorWithOutput(source).Diagnostics;

    private static (
        ImmutableArray<Diagnostic> Diagnostics,
        ImmutableArray<SyntaxTree> GeneratedTrees
    ) RunLegacyGeneratorWithOutput(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(ParquetSerializableAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime.Numerics").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
        };

        CSharpCompilation compilation = CSharpCompilation.Create(
            "LegacyTestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        var generator =
            new LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        GeneratorDriverRunResult runResult = driver.GetRunResult();

        return (diagnostics, runResult.GeneratedTrees);
    }
}
