using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Parquet.Serialization;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record TpchLineItemRecord
{
    [JsonPropertyName("l_orderkey")]
    public long? OrderKey { get; init; }

    [JsonPropertyName("l_partkey")]
    public long? PartKey { get; init; }

    [JsonPropertyName("l_suppkey")]
    public long? SuppKey { get; init; }

    [JsonPropertyName("l_linenumber")]
    public long? LineNumber { get; init; }

    [JsonPropertyName("l_quantity")]
    [ParquetDecimal(15, 2)]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("l_extendedprice")]
    [ParquetDecimal(15, 2)]
    public decimal? ExtendedPrice { get; init; }

    [JsonPropertyName("l_discount")]
    [ParquetDecimal(15, 2)]
    public decimal? Discount { get; init; }

    [JsonPropertyName("l_tax")]
    [ParquetDecimal(15, 2)]
    public decimal? Tax { get; init; }

    [JsonPropertyName("l_returnflag")]
    public string? ReturnFlag { get; init; }

    [JsonPropertyName("l_linestatus")]
    public string? LineStatus { get; init; }

    [JsonPropertyName("l_shipdate")]
    public DateTime? ShipDate { get; init; }

    [JsonPropertyName("l_commitdate")]
    public DateTime? CommitDate { get; init; }

    [JsonPropertyName("l_receiptdate")]
    public DateTime? ReceiptDate { get; init; }

    [JsonPropertyName("l_shipinstruct")]
    public string? ShipInstruct { get; init; }

    [JsonPropertyName("l_shipmode")]
    public string? ShipMode { get; init; }

    [JsonPropertyName("l_comment")]
    public string? Comment { get; init; }
}

[ParquetSerializable]
public partial record AdultCensusRecord
{
    [JsonPropertyName("age")]
    public long? Age { get; init; }

    [JsonPropertyName("workclass")]
    public string? Workclass { get; init; }

    [JsonPropertyName("fnlwgt")]
    public long? Fnlwgt { get; init; }

    [JsonPropertyName("education")]
    public string? Education { get; init; }

    [JsonPropertyName("education.num")]
    public long? EducationNum { get; init; }

    [JsonPropertyName("marital.status")]
    public string? MaritalStatus { get; init; }

    [JsonPropertyName("occupation")]
    public string? Occupation { get; init; }

    [JsonPropertyName("relationship")]
    public string? Relationship { get; init; }

    [JsonPropertyName("race")]
    public string? Race { get; init; }

    [JsonPropertyName("sex")]
    public string? Sex { get; init; }

    [JsonPropertyName("capital.gain")]
    public long? CapitalGain { get; init; }

    [JsonPropertyName("capital.loss")]
    public long? CapitalLoss { get; init; }

    [JsonPropertyName("hours.per.week")]
    public long? HoursPerWeek { get; init; }

    [JsonPropertyName("native.country")]
    public string? NativeCountry { get; init; }

    [JsonPropertyName("income")]
    public string? Income { get; init; }
}

[ParquetSerializable]
public partial record DiamondRecord
{
    [JsonPropertyName("carat")]
    public double? Carat { get; init; }

    [JsonPropertyName("cut")]
    public long? Cut { get; init; }

    [JsonPropertyName("color")]
    public long? Color { get; init; }

    [JsonPropertyName("clarity")]
    public long? Clarity { get; init; }

    [JsonPropertyName("depth")]
    public double? Depth { get; init; }

    [JsonPropertyName("table")]
    public double? Table { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    [JsonPropertyName("z")]
    public double? Z { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }
}

public sealed class BenchmarkDatasetsIntegrationTests
{
    private static readonly string BenchmarkDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "benchmarks", "data")
    );

    [Fact]
    public async Task ReadParquetAsyncDeserializesTpchLineitemDataset()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet");
        Assert.True(System.IO.File.Exists(filePath), $"File not found: {filePath}");

        await using var stream = System.IO.File.OpenRead(filePath);
        var records = await TpchLineItemRecordParquetExtensions.ReadParquetAsync(stream);

        Assert.Equal(60175, records.Count);

        // Verify first record values from known TPC-H SF 0.01 ground truth
        var r0 = records[0];
        Assert.Equal(1L, r0.OrderKey);
        Assert.Equal(1552L, r0.PartKey);
        Assert.Equal(93L, r0.SuppKey);
        Assert.Equal(1L, r0.LineNumber);
        Assert.Equal(17.00m, r0.Quantity);
        Assert.Equal(24710.35m, r0.ExtendedPrice);
        Assert.Equal(0.04m, r0.Discount);
        Assert.Equal(0.02m, r0.Tax);
        Assert.Equal("N", r0.ReturnFlag);
        Assert.Equal("O", r0.LineStatus);
        Assert.Equal(new DateTime(1996, 3, 13), r0.ShipDate!.Value.Date);
        Assert.Equal(new DateTime(1996, 2, 12), r0.CommitDate!.Value.Date);
        Assert.Equal(new DateTime(1996, 3, 22), r0.ReceiptDate!.Value.Date);
        Assert.Equal("DELIVER IN PERSON", r0.ShipInstruct);
        Assert.Equal("TRUCK", r0.ShipMode);
        Assert.Equal("to beans x-ray carefull", r0.Comment);

        // Verify last record
        var rLast = records[60174];
        Assert.Equal(60000L, rLast.OrderKey);
    }

    [Fact]
    public async Task ReadParquetAsyncDeserializesAdultCensusIncomeDataset()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "adult_census_income.parquet");
        Assert.True(System.IO.File.Exists(filePath), $"File not found: {filePath}");

        await using var stream = System.IO.File.OpenRead(filePath);
        var records = await AdultCensusRecordParquetExtensions.ReadParquetAsync(stream);

        Assert.Equal(32561, records.Count);

        var r0 = records[0];
        Assert.Equal(90L, r0.Age);
        Assert.Equal("?", r0.Workclass);
        Assert.Equal(77053L, r0.Fnlwgt);
        Assert.Equal("HS-grad", r0.Education);
        Assert.Equal(9L, r0.EducationNum);
        Assert.Equal("Widowed", r0.MaritalStatus);
        Assert.Equal("?", r0.Occupation);
        Assert.Equal("Not-in-family", r0.Relationship);
        Assert.Equal("White", r0.Race);
        Assert.Equal("Female", r0.Sex);
        Assert.Equal(0L, r0.CapitalGain);
        Assert.Equal(4356L, r0.CapitalLoss);
        Assert.Equal(40L, r0.HoursPerWeek);
        Assert.Equal("United-States", r0.NativeCountry);
        Assert.Equal("<=50K", r0.Income);
    }

    [Fact]
    public async Task ReadParquetAsyncDeserializesDiamondsDataset()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "diamonds.parquet");
        Assert.True(System.IO.File.Exists(filePath), $"File not found: {filePath}");

        await using var stream = System.IO.File.OpenRead(filePath);
        var records = await DiamondRecordParquetExtensions.ReadParquetAsync(stream);

        Assert.Equal(53940, records.Count);

        var r0 = records[0];
        Assert.Equal(0.23, r0.Carat!.Value, precision: 2);
        Assert.Equal(2L, r0.Cut);
        Assert.Equal(1L, r0.Color);
        Assert.Equal(3L, r0.Clarity);
        Assert.Equal(61.5, r0.Depth!.Value, precision: 1);
        Assert.Equal(55.0, r0.Table!.Value, precision: 1);
        Assert.Equal(3.95, r0.X!.Value, precision: 2);
        Assert.Equal(3.98, r0.Y!.Value, precision: 2);
        Assert.Equal(2.43, r0.Z!.Value, precision: 2);
    }

    [Fact]
    public async Task TpchLineItemRoundtripsWithSnappyAndZstd()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet");
        await using var stream = System.IO.File.OpenRead(filePath);
        var original = await TpchLineItemRecordParquetExtensions.ReadParquetAsync(stream);

        // Round-trip with Snappy
        using var snappyStream = new MemoryStream();
        var snappyOptions = new ParquetSerializerOptions
        {
            CompressionMethod = ParquetCompressionMethod.Snappy,
            RowGroupSize = 20_000,
        };
        await original.WriteParquetAsync(snappyStream, options: snappyOptions);
        snappyStream.Position = 0;

        var roundtrippedSnappy = await TpchLineItemRecordParquetExtensions.ReadParquetAsync(
            snappyStream
        );
        Assert.Equal(original.Count, roundtrippedSnappy.Count);
        Assert.Equal(original[0].OrderKey, roundtrippedSnappy[0].OrderKey);
        Assert.Equal(original[0].Quantity, roundtrippedSnappy[0].Quantity);
        Assert.Equal(original[0].Comment, roundtrippedSnappy[0].Comment);

        // Round-trip with Zstd
        using var zstdStream = new MemoryStream();
        var zstdOptions = new ParquetSerializerOptions
        {
            CompressionMethod = ParquetCompressionMethod.Zstd,
            CompressionLevel = ParquetCompressionLevel.Optimal,
            RowGroupSize = 20_000,
        };
        await original.WriteParquetAsync(zstdStream, options: zstdOptions);
        zstdStream.Position = 0;

        var roundtrippedZstd = await TpchLineItemRecordParquetExtensions.ReadParquetAsync(
            zstdStream
        );
        Assert.Equal(original.Count, roundtrippedZstd.Count);
        Assert.Equal(original[100].ExtendedPrice, roundtrippedZstd[100].ExtendedPrice);
        Assert.Equal(original[100].ShipInstruct, roundtrippedZstd[100].ShipInstruct);
    }

    [Fact]
    public async Task AdultCensusRoundtripsWithParallelReader()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "adult_census_income.parquet");
        await using var stream = System.IO.File.OpenRead(filePath);
        var original = await AdultCensusRecordParquetExtensions.ReadParquetAsync(stream);

        using var outputStream = new MemoryStream();
        await original.WriteParquetBatchedAsync(
            outputStream,
            new ParquetSerializerOptions { RowGroupSize = 5_000 }
        );
        byte[] bytes = outputStream.ToArray();

        var roundtripped = await AdultCensusRecordParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(bytes),
            new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 }
        );

        Assert.Equal(original.Count, roundtripped.Count);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(original[i].Workclass, roundtripped[i].Workclass);
            Assert.Equal(original[i].Education, roundtripped[i].Education);
            Assert.Equal(original[i].Income, roundtripped[i].Income);
        }
    }

    [Fact]
    public async Task ParquetSerializerDeserializesTpchLineitemDatasetIdenticalToSourceGenerator()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet");
        await using var stream1 = System.IO.File.OpenRead(filePath);
        var sgRecords = await TpchLineItemRecordParquetExtensions.ReadParquetAsync(stream1);

        await using var stream2 = System.IO.File.OpenRead(filePath);
        var reflectionResult = await ParquetSerializer.DeserializeAsync<TpchLineItemRecord>(
            stream2
        );
        var refRecords = reflectionResult.Data;

        Assert.Equal(sgRecords.Count, refRecords.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(sgRecords[i].OrderKey, refRecords[i].OrderKey);
            Assert.Equal(sgRecords[i].PartKey, refRecords[i].PartKey);
            Assert.Equal(sgRecords[i].SuppKey, refRecords[i].SuppKey);
            Assert.Equal(sgRecords[i].LineNumber, refRecords[i].LineNumber);
            Assert.Equal(sgRecords[i].Quantity, refRecords[i].Quantity);
            Assert.Equal(sgRecords[i].ExtendedPrice, refRecords[i].ExtendedPrice);
            Assert.Equal(sgRecords[i].Discount, refRecords[i].Discount);
            Assert.Equal(sgRecords[i].Tax, refRecords[i].Tax);
            Assert.Equal(sgRecords[i].ReturnFlag, refRecords[i].ReturnFlag);
            Assert.Equal(sgRecords[i].LineStatus, refRecords[i].LineStatus);
            Assert.Equal(sgRecords[i].ShipDate, refRecords[i].ShipDate);
            Assert.Equal(sgRecords[i].CommitDate, refRecords[i].CommitDate);
            Assert.Equal(sgRecords[i].ReceiptDate, refRecords[i].ReceiptDate);
            Assert.Equal(sgRecords[i].ShipInstruct, refRecords[i].ShipInstruct);
            Assert.Equal(sgRecords[i].ShipMode, refRecords[i].ShipMode);
            Assert.Equal(sgRecords[i].Comment, refRecords[i].Comment);
        }
    }

    [Fact]
    public async Task ParquetSerializerDeserializesAdultCensusIncomeDatasetIdenticalToSourceGenerator()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "adult_census_income.parquet");
        await using var stream1 = System.IO.File.OpenRead(filePath);
        var sgRecords = await AdultCensusRecordParquetExtensions.ReadParquetAsync(stream1);

        await using var stream2 = System.IO.File.OpenRead(filePath);
        var reflectionResult = await ParquetSerializer.DeserializeAsync<AdultCensusRecord>(stream2);
        var refRecords = reflectionResult.Data;

        Assert.Equal(sgRecords.Count, refRecords.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(sgRecords[i].Age, refRecords[i].Age);
            Assert.Equal(sgRecords[i].Workclass, refRecords[i].Workclass);
            Assert.Equal(sgRecords[i].Fnlwgt, refRecords[i].Fnlwgt);
            Assert.Equal(sgRecords[i].Education, refRecords[i].Education);
            Assert.Equal(sgRecords[i].EducationNum, refRecords[i].EducationNum);
            Assert.Equal(sgRecords[i].MaritalStatus, refRecords[i].MaritalStatus);
            Assert.Equal(sgRecords[i].Occupation, refRecords[i].Occupation);
            Assert.Equal(sgRecords[i].Relationship, refRecords[i].Relationship);
            Assert.Equal(sgRecords[i].Race, refRecords[i].Race);
            Assert.Equal(sgRecords[i].Sex, refRecords[i].Sex);
            Assert.Equal(sgRecords[i].CapitalGain, refRecords[i].CapitalGain);
            Assert.Equal(sgRecords[i].CapitalLoss, refRecords[i].CapitalLoss);
            Assert.Equal(sgRecords[i].HoursPerWeek, refRecords[i].HoursPerWeek);
            Assert.Equal(sgRecords[i].NativeCountry, refRecords[i].NativeCountry);
            Assert.Equal(sgRecords[i].Income, refRecords[i].Income);
        }
    }

    [Fact]
    public async Task ParquetSerializerDeserializesDiamondsDatasetIdenticalToSourceGenerator()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "diamonds.parquet");
        await using var stream1 = System.IO.File.OpenRead(filePath);
        var sgRecords = await DiamondRecordParquetExtensions.ReadParquetAsync(stream1);

        await using var stream2 = System.IO.File.OpenRead(filePath);
        var reflectionResult = await ParquetSerializer.DeserializeAsync<DiamondRecord>(stream2);
        var refRecords = reflectionResult.Data;

        Assert.Equal(sgRecords.Count, refRecords.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(sgRecords[i].Carat, refRecords[i].Carat);
            Assert.Equal(sgRecords[i].Cut, refRecords[i].Cut);
            Assert.Equal(sgRecords[i].Color, refRecords[i].Color);
            Assert.Equal(sgRecords[i].Clarity, refRecords[i].Clarity);
            Assert.Equal(sgRecords[i].Depth, refRecords[i].Depth);
            Assert.Equal(sgRecords[i].Table, refRecords[i].Table);
            Assert.Equal(sgRecords[i].X, refRecords[i].X);
            Assert.Equal(sgRecords[i].Y, refRecords[i].Y);
            Assert.Equal(sgRecords[i].Z, refRecords[i].Z);
            Assert.Equal(sgRecords[i].Price, refRecords[i].Price);
        }
    }
}
