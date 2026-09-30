using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Flat model exercising every buffer shape the columnar hand-off has to express: fixed-width
/// values, a nullable value column (packed payload + definition levels), a string column and a
/// binary column.
/// </summary>
[ParquetSerializable]
public partial record ColumnarHandoffModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string? Name { get; init; }

    [ParquetColumn("score")]
    public double Score { get; init; }

    [ParquetColumn("optional_score")]
    public double? OptionalScore { get; init; }

    [ParquetColumn("flag")]
    public bool Flag { get; init; }

    [ParquetColumn("correlation_id")]
    public Guid CorrelationId { get; init; }

    [ParquetColumn("created_at")]
    public DateTime CreatedAt { get; init; }

    [ParquetColumn("optional_count")]
    public long? OptionalCount { get; init; }

    [ParquetColumn("payload")]
    public byte[]? Payload { get; init; }
}

/// <summary>
/// Model with no nullable value columns — checks the generated surface degrades to the simple
/// shape (no definition-level parameters at all).
/// </summary>
[ParquetSerializable]
public partial record ColumnarDenseModel
{
    [ParquetColumn("a")]
    public long A { get; init; }

    [ParquetColumn("b")]
    public double B { get; init; }
}

/// <summary>
/// Columns whose camel-cased names land on the fixed parameters of the generated writers
/// (<c>writer</c>, <c>rowCount</c>, <c>cancellationToken</c>) or on a C# keyword.
/// </summary>
[ParquetSerializable]
public partial record ColumnarCollisionModel
{
    [ParquetColumn("row_count")]
    public int RowCount { get; init; }

    [ParquetColumn("writer")]
    public long Writer { get; init; }

    [ParquetColumn("cancellation_token")]
    public double CancellationToken { get; init; }

    [ParquetColumn("event")]
    public int Event { get; init; }
}

/// <summary>
/// Direct columnar hand-off (issue #137). The generated <c>WriteParquetRowGroupAsync(batch)</c> /
/// <c>WriteParquetRowGroupColumnarAsync(...)</c> pair must produce output indistinguishable from
/// the row-oriented path for the same logical data, while renting nothing.
/// </summary>
public sealed class ColumnarHandoffTests
{
    private const int RowCount = 512;

    private static List<ColumnarHandoffModel> BuildRows(int count)
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<ColumnarHandoffModel>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(
                new ColumnarHandoffModel
                {
                    Id = i,
                    // Every third row nulls the string, so the null lane is exercised too.
                    Name = i % 3 == 0 ? null : "row-" + i.ToString(CultureInfo.InvariantCulture),
                    Score = i * 1.5,
                    OptionalScore = i % 4 == 0 ? null : i * 0.25,
                    Flag = i % 2 == 0,
                    CorrelationId = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                    CreatedAt = baseTime.AddMilliseconds(i),
                    OptionalCount = i % 5 == 0 ? null : i * 1000L,
                    Payload = i % 7 == 0 ? null : new byte[] { (byte)i, (byte)(i + 1) },
                }
            );
        }

        return rows;
    }

    /// <summary>
    /// Transposes a POCO list into the exact buffer shapes the generated batch struct expects.
    /// A real caller would already hold these; the test builds them so the two write paths can be
    /// compared on identical data.
    /// </summary>
    private static ColumnarHandoffModelColumnarBatch Transpose(List<ColumnarHandoffModel> rows)
    {
        int count = rows.Count;
        var id = new int[count];
        var name = new ReadOnlyMemory<char>?[count];
        var score = new double[count];
        var optionalScore = new double[count];
        var optionalScoreDefs = new int[count];
        var flag = new bool[count];
        var correlationId = new Guid[count];
        var createdAt = new DateTime[count];
        var optionalCount = new long[count];
        var optionalCountDefs = new int[count];
        var payload = new ReadOnlyMemory<byte>?[count];

        int scorePacked = 0;
        int countPacked = 0;
        for (int i = 0; i < count; i++)
        {
            ColumnarHandoffModel row = rows[i];
            id[i] = row.Id;
            // The generated converter, not a bare conditional: `cond ? null : x.AsMemory()` has
            // natural type ReadOnlyMemory<char>, so the null branch would store a
            // present-but-empty value instead of a null.
            name[i] = ColumnarHandoffModelParquetExtensions.AsColumnarText(row.Name);
            score[i] = row.Score;
            flag[i] = row.Flag;
            correlationId[i] = row.CorrelationId;
            createdAt[i] = row.CreatedAt;
            payload[i] = ColumnarHandoffModelParquetExtensions.AsColumnarBinary(row.Payload);

            if (row.OptionalScore.HasValue)
            {
                optionalScore[scorePacked++] = row.OptionalScore.Value;
                optionalScoreDefs[i] = 1;
            }

            if (row.OptionalCount.HasValue)
            {
                optionalCount[countPacked++] = row.OptionalCount.Value;
                optionalCountDefs[i] = 1;
            }
        }

        return new ColumnarHandoffModelColumnarBatch(
            rowCount: count,
            id: id,
            name: name,
            score: score,
            optionalScore: optionalScore.AsMemory(0, scorePacked),
            optionalScoreDefinitionLevels: optionalScoreDefs,
            flag: flag,
            correlationId: correlationId,
            createdAt: createdAt,
            optionalCount: optionalCount.AsMemory(0, countPacked),
            optionalCountDefinitionLevels: optionalCountDefs,
            payload: payload
        );
    }

    [Fact]
    public async Task ColumnarWriteRoundTripsToTheSameValuesAsTheRowOrientedWrite()
    {
        List<ColumnarHandoffModel> rows = BuildRows(RowCount);

        using var rowStream = new MemoryStream();
        await rows.WriteParquetAsync(rowStream);

        using var columnarStream = new MemoryStream();
        await Transpose(rows).WriteParquetAsync(columnarStream);

        rowStream.Position = 0;
        columnarStream.Position = 0;
        List<ColumnarHandoffModel> fromRows = await ColumnarHandoffModelParquet
            .From(rowStream)
            .ToListAsync();
        List<ColumnarHandoffModel> fromColumns = await ColumnarHandoffModelParquet
            .From(columnarStream)
            .ToListAsync();

        fromColumns.Count.ShouldBe(RowCount);
        fromColumns.Count.ShouldBe(fromRows.Count);
        for (int i = 0; i < fromRows.Count; i++)
        {
            fromColumns[i].Id.ShouldBe(fromRows[i].Id);
            fromColumns[i].Name.ShouldBe(fromRows[i].Name);
            fromColumns[i].Score.ShouldBe(fromRows[i].Score);
            fromColumns[i].OptionalScore.ShouldBe(fromRows[i].OptionalScore);
            fromColumns[i].Flag.ShouldBe(fromRows[i].Flag);
            fromColumns[i].CorrelationId.ShouldBe(fromRows[i].CorrelationId);
            fromColumns[i].CreatedAt.ShouldBe(fromRows[i].CreatedAt);
            fromColumns[i].OptionalCount.ShouldBe(fromRows[i].OptionalCount);
            fromColumns[i].Payload.ShouldBe(fromRows[i].Payload);
        }
    }

    [Fact]
    public async Task ColumnarWriteProducesByteIdenticalOutputToTheRowOrientedWrite()
    {
        List<ColumnarHandoffModel> rows = BuildRows(RowCount);

        using var rowStream = new MemoryStream();
        await rows.WriteParquetAsync(rowStream);

        using var columnarStream = new MemoryStream();
        await Transpose(rows).WriteParquetAsync(columnarStream);

        // The columnar path reaches the same encoder with the same buffers, so the file bytes are
        // the same too. This is a stronger statement than value equality: it says the hand-off is
        // not merely equivalent but literally the same write.
        columnarStream.ToArray().ShouldBe(rowStream.ToArray());
    }

    [Fact]
    public async Task PositionalOverloadMatchesTheBatchOverload()
    {
        List<ColumnarHandoffModel> rows = BuildRows(RowCount);
        ColumnarHandoffModelColumnarBatch batch = Transpose(rows);

        using var batchStream = new MemoryStream();
        await batch.WriteParquetAsync(batchStream);

        using var positionalStream = new MemoryStream();
        await using (
            var writer = await ParquetWriter.CreateAsync(
                ColumnarHandoffModelParquetExtensions.Schema,
                positionalStream
            )
        )
        {
            await writer.WriteParquetRowGroupColumnarAsync(
                batch.RowCount,
                batch.Id,
                batch.Name,
                batch.Score,
                batch.OptionalScore,
                batch.OptionalScoreDefinitionLevels,
                batch.Flag,
                batch.CorrelationId,
                batch.CreatedAt,
                batch.OptionalCount,
                batch.OptionalCountDefinitionLevels,
                batch.Payload
            );
        }

        positionalStream.ToArray().ShouldBe(batchStream.ToArray());
    }

    [Fact]
    public async Task OversizedBuffersAreSlicedToTheRowCount()
    {
        List<ColumnarHandoffModel> rows = BuildRows(RowCount);
        ColumnarHandoffModelColumnarBatch full = Transpose(rows);

        // A caller reusing a large scratch buffer supplies more entries than rows; only RowCount
        // of them may reach the file.
        const int shortCount = 100;
        int packedScores = 0;
        int packedCounts = 0;
        for (int i = 0; i < shortCount; i++)
        {
            packedScores += full.OptionalScoreDefinitionLevels.Span[i];
            packedCounts += full.OptionalCountDefinitionLevels.Span[i];
        }
        ColumnarHandoffModelColumnarBatch sliced = Rebuild(
            full,
            rowCount: shortCount,
            optionalScore: full.OptionalScore.Slice(0, packedScores),
            optionalCount: full.OptionalCount.Slice(0, packedCounts)
        );

        using var stream = new MemoryStream();
        await sliced.WriteParquetAsync(stream);
        stream.Position = 0;
        List<ColumnarHandoffModel> read = await ColumnarHandoffModelParquet
            .From(stream)
            .ToListAsync();

        read.Count.ShouldBe(shortCount);
        for (int i = 0; i < shortCount; i++)
        {
            read[i].Id.ShouldBe(rows[i].Id);
            read[i].OptionalScore.ShouldBe(rows[i].OptionalScore);
            read[i].OptionalCount.ShouldBe(rows[i].OptionalCount);
        }
    }

    [Fact]
    public async Task EmptyBatchWritesNoRowGroup()
    {
        var batch = new ColumnarHandoffModelColumnarBatch(
            rowCount: 0,
            id: default,
            name: default,
            score: default,
            optionalScore: default,
            optionalScoreDefinitionLevels: default,
            flag: default,
            correlationId: default,
            createdAt: default,
            optionalCount: default,
            optionalCountDefinitionLevels: default,
            payload: default
        );

        using var stream = new MemoryStream();
        await batch.WriteParquetAsync(stream);
        stream.Position = 0;
        List<ColumnarHandoffModel> read = await ColumnarHandoffModelParquet
            .From(stream)
            .ToListAsync();

        read.ShouldBeEmpty();
    }

    [Fact]
    public void ShortValueColumnIsRejectedAtConstructionWithTheColumnName()
    {
        ColumnarHandoffModelColumnarBatch valid = Transpose(BuildRows(RowCount));

        ArgumentException error = Should.Throw<ArgumentException>(() =>
            Rebuild(valid, score: valid.Score.Slice(0, RowCount - 1))
        );
        error.Message.ShouldContain("Score");
        error.ParamName.ShouldBe("score");
    }

    [Fact]
    public void ShortNullableStringLaneIsRejectedAtConstruction()
    {
        ColumnarHandoffModelColumnarBatch valid = Transpose(BuildRows(RowCount));

        ArgumentException error = Should.Throw<ArgumentException>(() =>
            Rebuild(valid, name: valid.Name.Slice(0, RowCount - 1))
        );
        error.Message.ShouldContain("Name");
        error.ParamName.ShouldBe("name");
    }

    [Fact]
    public void ShortDefinitionLevelColumnIsRejectedAtConstructionWithTheColumnName()
    {
        ColumnarHandoffModelColumnarBatch valid = Transpose(BuildRows(RowCount));

        ArgumentException error = Should.Throw<ArgumentException>(() =>
            Rebuild(
                valid,
                optionalScoreDefinitionLevels: valid.OptionalScoreDefinitionLevels.Slice(
                    0,
                    RowCount - 1
                )
            )
        );
        error.Message.ShouldContain("OptionalScore");
        error.Message.ShouldContain("definition levels");
        error.ParamName.ShouldBe("optionalScoreDefinitionLevels");
    }

    [Fact]
    public void RowCountLargerThanEveryLaneIsRejectedAtConstruction()
    {
        ColumnarHandoffModelColumnarBatch valid = Transpose(BuildRows(RowCount));

        // The "out of step" state #550 describes: RowCount claims more rows than the lanes hold.
        Should.Throw<ArgumentException>(() => Rebuild(valid, rowCount: RowCount + 1));
    }

    [Fact]
    public void NegativeRowCountIsRejectedAtConstruction()
    {
        ColumnarHandoffModelColumnarBatch valid = Transpose(BuildRows(RowCount));

        ArgumentOutOfRangeException error = Should.Throw<ArgumentOutOfRangeException>(() =>
            Rebuild(valid, rowCount: -1)
        );
        error.ParamName.ShouldBe("rowCount");
    }

    [Fact]
    public void ValidBatchExposesItsRowCountAndLanes()
    {
        List<ColumnarHandoffModel> rows = BuildRows(RowCount);
        ColumnarHandoffModelColumnarBatch batch = Transpose(rows);

        batch.RowCount.ShouldBe(RowCount);
        batch.Id.Length.ShouldBe(RowCount);
        batch.OptionalScoreDefinitionLevels.Length.ShouldBe(RowCount);
        batch.Id.Span[5].ShouldBe(5);
    }

    [Fact]
    public async Task DefaultBatchIsAnEmptyBatchThatWritesNoRowGroupAsync()
    {
        // default(T) never goes through the constructor. It has RowCount 0 and empty lanes, which is
        // a consistent (empty) batch, so the write is a no-op rather than an error.
        ColumnarHandoffModelColumnarBatch batch = default;
        batch.RowCount.ShouldBe(0);

        using var stream = new MemoryStream();
        await batch.WriteParquetAsync(stream);
        stream.Position = 0;
        List<ColumnarHandoffModel> read = await ColumnarHandoffModelParquet
            .From(stream)
            .ToListAsync();

        read.ShouldBeEmpty();
    }

    [Fact]
    public void BatchIsAReadOnlyStructWithGetOnlyPropertiesAndNoPublicFields()
    {
        Type type = typeof(ColumnarHandoffModelColumnarBatch);

        type.IsValueType.ShouldBeTrue();
        type.GetCustomAttributes(inherit: false)
            .Any(a => a.GetType().FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute")
            .ShouldBeTrue("the batch must be a readonly struct");
        type.GetFields(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();

        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        properties.Length.ShouldBe(12);
        foreach (PropertyInfo property in properties)
        {
            property.CanRead.ShouldBeTrue(property.Name);
            property.SetMethod.ShouldBeNull(property.Name);
        }
    }

    /// <summary>
    /// Builds a copy of <paramref name="batch"/> with some arguments replaced. Every copy goes
    /// through the validating constructor, which is the point: a mismatched lane cannot be built.
    /// </summary>
    private static ColumnarHandoffModelColumnarBatch Rebuild(
        ColumnarHandoffModelColumnarBatch batch,
        int? rowCount = null,
        ReadOnlyMemory<ReadOnlyMemory<char>?>? name = null,
        ReadOnlyMemory<double>? score = null,
        ReadOnlyMemory<double>? optionalScore = null,
        ReadOnlyMemory<int>? optionalScoreDefinitionLevels = null,
        ReadOnlyMemory<long>? optionalCount = null
    ) =>
        new(
            rowCount: rowCount ?? batch.RowCount,
            id: batch.Id,
            name: name ?? batch.Name,
            score: score ?? batch.Score,
            optionalScore: optionalScore ?? batch.OptionalScore,
            optionalScoreDefinitionLevels: optionalScoreDefinitionLevels
                ?? batch.OptionalScoreDefinitionLevels,
            flag: batch.Flag,
            correlationId: batch.CorrelationId,
            createdAt: batch.CreatedAt,
            optionalCount: optionalCount ?? batch.OptionalCount,
            optionalCountDefinitionLevels: batch.OptionalCountDefinitionLevels,
            payload: batch.Payload
        );

    [Fact]
    public async Task ColumnNamesThatCollideWithFixedParametersStillBuildAndWriteAsync()
    {
        int[] rowCounts = [1, 2, 3];
        long[] writers = [4, 5, 6];
        double[] tokens = [7.5, 8.5, 9.5];
        int[] events = [10, 11, 12];

        var batch = new ColumnarCollisionModelColumnarBatch(3, rowCounts, writers, tokens, events);

        // The model's own RowCount column cannot share the member name with the batch row count.
        batch.BatchRowCount.ShouldBe(3);
        batch.RowCount.ToArray().ShouldBe(rowCounts);

        using var stream = new MemoryStream();
        await batch.WriteParquetAsync(stream);
        stream.Position = 0;
        List<ColumnarCollisionModel> read = await ColumnarCollisionModelParquet
            .From(stream)
            .ToListAsync();

        read.Select(r => r.RowCount).ToArray().ShouldBe(rowCounts);
        read.Select(r => r.Writer).ToArray().ShouldBe(writers);
        read.Select(r => r.CancellationToken).ToArray().ShouldBe(tokens);
        read.Select(r => r.Event).ToArray().ShouldBe(events);
    }

    private static readonly long[] DenseA = [1, 2, 3];
    private static readonly double[] DenseB = [1.5, 2.5, 3.5];

    [Fact]
    public async Task DenseModelWithoutNullableColumnsExposesNoDefinitionLevelParameters()
    {
        var batch = new ColumnarDenseModelColumnarBatch(rowCount: 3, a: DenseA, b: DenseB);

        using var stream = new MemoryStream();
        await batch.WriteParquetAsync(stream);
        stream.Position = 0;
        List<ColumnarDenseModel> read = await ColumnarDenseModelParquet.From(stream).ToListAsync();

        read.Select(r => r.A).ToArray().ShouldBe(DenseA);
        read.Select(r => r.B).ToArray().ShouldBe(DenseB);
    }

    [Fact]
    public async Task MultipleBatchesAppendAsSeparateRowGroups()
    {
        List<ColumnarHandoffModel> rows = BuildRows(RowCount);
        ColumnarHandoffModelColumnarBatch batch = Transpose(rows);

        using var stream = new MemoryStream();
        await using (
            var writer = await ParquetWriter.CreateAsync(
                ColumnarHandoffModelParquetExtensions.Schema,
                stream
            )
        )
        {
            await writer.WriteParquetRowGroupAsync(batch);
            await writer.WriteParquetRowGroupAsync(batch);
        }

        stream.Position = 0;
        List<ColumnarHandoffModel> read = await ColumnarHandoffModelParquet
            .From(stream)
            .ToListAsync();
        read.Count.ShouldBe(RowCount * 2);
        read[RowCount + 7].Name.ShouldBe(rows[7].Name);
    }

    // ──────────────────────────────────────────────────────────
    //  Emitted-source guarantees
    // ──────────────────────────────────────────────────────────

    private static string EmitFlatSource() =>
        CodeEmitter.EmitSource(
            new TargetClassModel(
                Namespace: "TestNamespace",
                ClassName: "Widget",
                Properties: new EquatableArray<PropertyModel>(
                    new PropertyModel[]
                    {
                        new(
                            "Key",
                            "key",
                            "long",
                            null,
                            null,
                            1,
                            null,
                            null,
                            PropertyKind.Primitive,
                            false
                        ),
                        new(
                            "Label",
                            "label",
                            "string",
                            null,
                            null,
                            2,
                            null,
                            null,
                            PropertyKind.Primitive,
                            true
                        ),
                        new(
                            "Weight",
                            "weight",
                            "double?",
                            null,
                            null,
                            3,
                            null,
                            null,
                            PropertyKind.Primitive,
                            true
                        ),
                    }
                )
            )
        );

    [Fact]
    public void ColumnarPathRentsNothingFromArrayPool()
    {
        string source = EmitFlatSource();
        string columnar = ExtractColumnarRegion(source);

        // The whole point of the hand-off: caller-owned buffers reach Parquet.Net untouched.
        columnar.ShouldNotContain("ArrayPool");
        columnar.ShouldNotContain(".Rent(");
        // ...and nothing is copied into a scratch array either.
        columnar.ShouldNotContain(".CopyTo(");
        columnar.ShouldNotContain("new long[");
    }

    [Fact]
    public void NullableValueColumnGetsSeparateValuesAndDefinitionLevelParameters()
    {
        string source = EmitFlatSource();

        source.ShouldContain("public readonly struct WidgetColumnarBatch");
        source.ShouldContain("public global::System.ReadOnlyMemory<double> Weight { get; }");
        source.ShouldContain(
            "public global::System.ReadOnlyMemory<int> WeightDefinitionLevels { get; }"
        );
        source.ShouldContain("global::System.ReadOnlyMemory<double> weight,");
        source.ShouldContain("global::System.ReadOnlyMemory<int> weightDefinitionLevels");
        source.ShouldNotContain("public int RowCount;");
    }

    [Fact]
    public void CompoundModelsDoNotGetAColumnarSurface()
    {
        var nested = new PropertyModel(
            "City",
            "city",
            "string",
            null,
            null,
            1,
            null,
            null,
            PropertyKind.Primitive,
            true
        );
        var structProp = new PropertyModel(
            "Ship",
            "ship",
            "global::App.Address",
            null,
            null,
            1,
            null,
            null,
            PropertyKind.Struct,
            false
        )
        {
            Children = new EquatableArray<PropertyModel>(new[] { nested }),
        };

        string source = CodeEmitter.EmitSource(
            new TargetClassModel(
                Namespace: "TestNamespace",
                ClassName: "Shipment",
                Properties: new EquatableArray<PropertyModel>(new[] { structProp })
            )
        );

        // Struct/list members carry a definition ladder a caller cannot express as flat buffers,
        // so those models keep the row-oriented API only (documented limitation of #137).
        source.ShouldNotContain("ColumnarBatch");
        source.ShouldNotContain("WriteParquetRowGroupColumnarAsync");
    }

    private static string ExtractColumnarRegion(string source)
    {
        int start = source.IndexOf(
            "Writes one row group directly from caller-owned column buffers",
            StringComparison.Ordinal
        );
        (start >= 0).ShouldBeTrue("columnar writer was not emitted");
        int end = source.IndexOf("Asynchronously serializes all", start, StringComparison.Ordinal);
        (end > start).ShouldBeTrue("could not find the end of the columnar region");
        return source.Substring(start, end - start);
    }
}
