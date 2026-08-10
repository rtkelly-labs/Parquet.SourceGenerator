extern alias V5Generator;

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Xunit;

using V5Models = V5Generator::Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Tests;

public class V5EmitterTests
{
    [Fact]
    public void V5CodeEmitterEmitsValidDataColumnBasedCode()
    {
        string code = Emit(
            Prop("Id", "id", "int", V5Models::PropertyKind.Primitive, isNullable: false),
            Prop("Name", "name", "string", V5Models::PropertyKind.Primitive, isNullable: true));

        Assert.Contains("namespace TestNamespace;", code);
        Assert.Contains("public static partial class TestModelParquetV5Extensions", code);
        Assert.Contains("public static readonly global::Parquet.Schema.ParquetSchema Schema = new global::Parquet.Schema.ParquetSchema(", code);
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
        string code = Emit(Prop("Payload", "payload", "byte[]", V5Models::PropertyKind.ByteArray, isNullable: true));

        Assert.Contains("var colArray_0 = new byte[count][];", code);
        Assert.DoesNotContain("new byte[][count]", code);

        // The read cast has to name the jagged type, where the rank does belong at the end.
        Assert.Contains("(byte[][])col_0.Data;", code);
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
        string code = Emit(Prop(
            "Grade",
            "grade",
            "global::MyApp.Grade?",
            V5Models::PropertyKind.Enum,
            isNullable: true,
            enumUnderlyingTypeName: "int"));

        Assert.Contains("var colArray_0 = new int?[count];", code);
        Assert.Contains("item.Grade is null ? (int?)null : (int)item.Grade.Value", code);
        Assert.Contains("data_0[k] is null ? (global::MyApp.Grade?)null : (global::MyApp.Grade)data_0[k]!", code);
    }

    [Fact]
    public void NonNullableEnumColumnUsesTheBareUnderlyingType()
    {
        string code = Emit(Prop(
            "Grade",
            "grade",
            "global::MyApp.Grade",
            V5Models::PropertyKind.Enum,
            isNullable: false,
            enumUnderlyingTypeName: "int"));

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
        string code = Emit(Prop("Id", "id", "int", V5Models::PropertyKind.Primitive, isNullable: false));

        Assert.Contains("writer.CompressionMethod = options.CompressionMethod switch", code);
        Assert.Contains("writer.CompressionLevel = global::System.IO.Compression.CompressionLevel.Fastest;", code);

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
        string code = Emit(Prop("Id", "id", "int", V5Models::PropertyKind.Primitive, isNullable: false));

        int guardStart = code.IndexOf("#if NET6_0_OR_GREATER", StringComparison.Ordinal);
        int guardEnd = code.IndexOf("#endif", StringComparison.Ordinal);
        int smallestSize = code.IndexOf(
            "global::System.IO.Compression.CompressionLevel.SmallestSize",
            StringComparison.Ordinal);

        Assert.True(guardStart >= 0, "The emitted code should guard the .NET 6+ compression level.");
        Assert.InRange(smallestSize, guardStart, guardEnd);
    }

    /// <summary>
    /// The reader was created without options at all — the defect audit item 3.1 closed on the v6
    /// side, reintroduced by the classic backend.
    /// </summary>
    [Fact]
    public void ReaderReceivesTheFormatOptions()
    {
        string code = Emit(Prop("Id", "id", "int", V5Models::PropertyKind.Primitive, isNullable: false));

        Assert.Contains(
            "global::Parquet.ParquetReader.CreateAsync(stream, BuildFormatOptions(options), cancellationToken: cancellationToken)",
            code);
    }

    /// <summary>
    /// Field resolution is a property of the file, not of a row group. Doing it per row group also
    /// re-invoked <c>GetDataFields()</c>, which allocates a fresh array on every call.
    /// </summary>
    [Fact]
    public void SchemaFieldsAreResolvedOncePerFileRatherThanPerRowGroup()
    {
        string code = Emit(
            Prop("Id", "id", "int", V5Models::PropertyKind.Primitive, isNullable: false),
            Prop("Name", "name", "string", V5Models::PropertyKind.Primitive, isNullable: true));

        Assert.Equal(1, CountOccurrences(code, "reader.Schema.GetDataFields()"));
        Assert.Equal(1, CountOccurrences(code, "ResolveSchemaField(fileFields, 0, _field_0, ref fieldsByName)"));

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

        Assert.Contains(RunV5Generator(source), d => d.Id == DiagnosticDescriptors.TypeUnsupportedOnClassicApi.Id);
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

        IReadOnlyList<Diagnostic> diagnostics = RunV5Generator(source);

        Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticDescriptors.TypeUnsupportedOnClassicApi.Id);
        Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id);
    }

    // ──────────────────────────────────────────────────────────
    //  HELPERS
    // ──────────────────────────────────────────────────────────

    private static V5Models::PropertyModel Prop(
        string name,
        string columnName,
        string typeName,
        V5Models::PropertyKind kind,
        bool isNullable,
        string? enumUnderlyingTypeName = null) =>
        new(name, columnName, typeName, null, enumUnderlyingTypeName, 1, null, null, kind, isNullable);

    private static string Emit(params V5Models::PropertyModel[] properties)
    {
        var model = new V5Models::TargetClassModel(
            "TestNamespace",
            "TestModel",
            new V5Models::EquatableArray<V5Models::PropertyModel>(properties));

        return V5Generator::Parquet.SourceGenerator.V5.Emitter.V5CodeEmitter.EmitSource(model);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static IReadOnlyList<Diagnostic> RunV5Generator(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ParquetSerializableAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime.Numerics").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
        };

        CSharpCompilation compilation = CSharpCompilation.Create(
            "V5TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var generator = new V5Generator::Parquet.SourceGenerator.V5.ParquetV5IncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        return diagnostics;
    }
}
