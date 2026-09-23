using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public sealed class ParserAndEmitterTests
{
    private static readonly string[] ClassArgs1 = new[] { "TestClass" };
    private static readonly string[] ClassArgs2 = new[] { "col", "TestClass" };

    private static readonly int[] SampleArray1 = new[] { 1, 2, 3 };
    private static readonly int[] SampleArray2 = new[] { 1, 2, 3 };
    private static readonly int[] SampleArray3 = new[] { 1, 2, 4 };

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
        int? decimalScale = null,
        ColumnEncoding encoding = ColumnEncoding.Default
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
            isNullable,
            Encoding: encoding
        );

    [Fact]
    public void CodeEmitterGeneratesValidSourceForComplexModel()
    {
        var properties = new[]
        {
            Prop("Id", "id", "int", 1),
            Prop("Name", "name", "string?", 2, isNullable: true),
            Prop(
                "Price",
                "price",
                "decimal",
                3,
                PropertyKind.Decimal,
                decimalPrecision: 18,
                decimalScale: 4
            ),
            Prop(
                "CreatedAt",
                "created_at",
                "System.DateTime",
                4,
                PropertyKind.DateTime,
                timestampUnit: "Microseconds"
            ),
            Prop("Duration", "duration", "System.TimeSpan", 5, PropertyKind.TimeSpan),
            Prop(
                "CorrelationId",
                "correlation_id",
                "System.Guid",
                6,
                PropertyKind.Guid,
                isNullable: true
            ),
            Prop(
                "Status",
                "status",
                "Parquet.SourceGenerator.Tests.EventStatus",
                7,
                PropertyKind.Enum,
                isNullable: true,
                enumUnderlyingTypeName: "int"
            ),
            Prop("Data", "data", "byte[]", 8, PropertyKind.ByteArray, isNullable: true),
        };

        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(properties)
        );

        string source = CodeEmitter.EmitSource(model);

        source.ShouldNotBeNull();
        source.ShouldContain("namespace TestNamespace;");
        source.ShouldContain("public static partial class TestEntityParquetExtensions");
        source.ShouldContain("DecimalDataField");
        source.ShouldContain("DateTimeDataField");
        source.ShouldContain("TimeDataField");
        source.ShouldContain("WriteParquetRowGroupAsync");
        source.ShouldContain("WriteAllPartsAsync");
        source.ShouldContain("ReadParallelArrayCoreAsync");
        source.ShouldContain("if (missing_1 || chunkStats_1?.NullCount == rowCount)");
        source.ShouldContain("global::System.Array.Clear(buffer_1, 0, rowCount);");
        source.ShouldContain(
            "else\n                {\n                    await groupReader.ReadAsync("
        );
    }

    [Fact]
    public void EquatableArrayValueEqualityAndMethods()
    {
        var array1 = new EquatableArray<int>(SampleArray1);
        var array2 = new EquatableArray<int>(SampleArray2);
        var array3 = new EquatableArray<int>(SampleArray3);

        array1.Length.ShouldBe(3);
        array1[1].ShouldBe(2);
        array1.Equals(array2).ShouldBeTrue();
        (array1 == array2).ShouldBeTrue();
        (array1 != array2).ShouldBeFalse();
        array1.Equals(array3).ShouldBeFalse();
        array1.Equals(null!).ShouldBeFalse();
        array1.GetHashCode().ShouldBe(array2.GetHashCode());
        EquatableArray<int>.Empty.Length.ShouldBe(0);

        int sum = 0;
        foreach (int item in array1)
        {
            sum += item;
        }
        sum.ShouldBe(6);
        array1.AsSpan().Length.ShouldBe(3);
    }

    [Fact]
    public void DiagnosticInfoValueEqualityAndMethods()
    {
        var diag1 = new DiagnosticInfo(
            DiagnosticDescriptors.MustBePartial,
            Location.None,
            ClassArgs1
        );
        var diag2 = new DiagnosticInfo(
            DiagnosticDescriptors.MustBePartial,
            Location.None,
            ClassArgs1
        );
        var diag3 = new DiagnosticInfo(
            DiagnosticDescriptors.DuplicateColumnName,
            Location.None,
            ClassArgs2
        );

        diag1.Equals(diag2).ShouldBeTrue();
        diag1.Equals(diag3).ShouldBeFalse();
        diag1.GetHashCode().ShouldBe(diag2.GetHashCode());

        Diagnostic diagnostic = diag1.ToDiagnostic();
        diagnostic.Id.ShouldBe("PARQ001");
    }

    [Fact]
    public void PropertyModelEncodingEqualityAndCodeEmission()
    {
        var propDefault = Prop("Id", "id", "int", 1);
        var propDelta = Prop("Id", "id", "int", 1, encoding: ColumnEncoding.DeltaBinaryPacked);
        var propDictionary = Prop("Tag", "tag", "string", 2, encoding: ColumnEncoding.Dictionary);
        var propByteSplit = Prop(
            "Value",
            "value",
            "double",
            3,
            encoding: ColumnEncoding.ByteSplitStream
        );

        propDefault.Encoding.ShouldBe(ColumnEncoding.Default);
        propDelta.Encoding.ShouldBe(ColumnEncoding.DeltaBinaryPacked);
        propDefault.ShouldNotBe(propDelta);

        var targetClass = new TargetClassModel(
            Namespace: "TestEncodingNamespace",
            ClassName: "TestEncodingEntity",
            Properties: new EquatableArray<PropertyModel>(
                new[] { propDelta, propDictionary, propByteSplit }
            )
        );

        string source = CodeEmitter.EmitSource(targetClass);
        source.ShouldContain(
            "formatOptions.ColumnEncodingHints[\"id\"] = global::Parquet.EncodingHint.DeltaBinaryPacked;"
        );
        source.ShouldContain(
            "formatOptions.ColumnEncodingHints[\"tag\"] = global::Parquet.EncodingHint.Dictionary;"
        );
        source.ShouldContain(
            "formatOptions.ColumnEncodingHints[\"value\"] = global::Parquet.EncodingHint.ByteSplitStream;"
        );
    }

    // ── Parquet.Net Contract Enforcement Tests ─────────────────────────────

    [Fact]
    public async Task WriteParquetAsyncNullStreamThrowsArgumentNullException()
    {
        var items = new List<TypeCoverageRecord> { new() };
        await Should.ThrowAsync<ArgumentNullException>(() => items.WriteParquetAsync(null!));
    }

    [Fact]
    public async Task WriteParquetAsyncNullItemsThrowsArgumentNullException()
    {
        IReadOnlyCollection<TypeCoverageRecord> items = null!;
        var stream = new MemoryStream();
        await Should.ThrowAsync<ArgumentNullException>(() => items.WriteParquetAsync(stream));
    }

    [Fact]
    public async Task WriteParquetBatchedAsyncInvalidRowGroupSizeThrowsArgumentOutOfRange()
    {
        var items = new List<TypeCoverageRecord> { new() };
        var stream = new MemoryStream();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            items.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = 0 }
            )
        );
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            items.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = -10 }
            )
        );
    }

    [Fact]
    public async Task ToArrayAsyncNullStreamThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            TypeCoverageRecordParquet.From((Stream)null!).ToArrayAsync()
        );
    }
}
