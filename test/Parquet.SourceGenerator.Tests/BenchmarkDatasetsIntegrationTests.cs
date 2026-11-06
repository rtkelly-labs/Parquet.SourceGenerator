using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Parquet.Serialization;
using Shouldly;
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
    public async Task ToArrayAsyncDeserializesTpchLineitemDataset()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet");
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        await using var stream = System.IO.File.OpenRead(filePath);
        var records = await TpchLineItemRecordParquet.From(stream).ToArrayAsync();

        records.Length.ShouldBe(60175);

        // Verify first record values from known TPC-H SF 0.01 ground truth
        var r0 = records[0];
        r0.OrderKey.ShouldBe(1L);
        r0.PartKey.ShouldBe(1552L);
        r0.SuppKey.ShouldBe(93L);
        r0.LineNumber.ShouldBe(1L);
        r0.Quantity.ShouldBe(17.00m);
        r0.ExtendedPrice.ShouldBe(24710.35m);
        r0.Discount.ShouldBe(0.04m);
        r0.Tax.ShouldBe(0.02m);
        r0.ReturnFlag.ShouldBe("N");
        r0.LineStatus.ShouldBe("O");
        r0.ShipDate!.Value.Date.ShouldBe(new DateTime(1996, 3, 13));
        r0.CommitDate!.Value.Date.ShouldBe(new DateTime(1996, 2, 12));
        r0.ReceiptDate!.Value.Date.ShouldBe(new DateTime(1996, 3, 22));
        r0.ShipInstruct.ShouldBe("DELIVER IN PERSON");
        r0.ShipMode.ShouldBe("TRUCK");
        r0.Comment.ShouldBe("to beans x-ray carefull");

        // Verify last record
        var rLast = records[60174];
        rLast.OrderKey.ShouldBe(60000L);
    }

    [Fact]
    public async Task ToArrayAsyncDeserializesAdultCensusIncomeDataset()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "adult_census_income.parquet");
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        await using var stream = System.IO.File.OpenRead(filePath);
        var records = await AdultCensusRecordParquet.From(stream).ToArrayAsync();

        records.Length.ShouldBe(32561);

        var r0 = records[0];
        r0.Age.ShouldBe(90L);
        r0.Workclass.ShouldBe("?");
        r0.Fnlwgt.ShouldBe(77053L);
        r0.Education.ShouldBe("HS-grad");
        r0.EducationNum.ShouldBe(9L);
        r0.MaritalStatus.ShouldBe("Widowed");
        r0.Occupation.ShouldBe("?");
        r0.Relationship.ShouldBe("Not-in-family");
        r0.Race.ShouldBe("White");
        r0.Sex.ShouldBe("Female");
        r0.CapitalGain.ShouldBe(0L);
        r0.CapitalLoss.ShouldBe(4356L);
        r0.HoursPerWeek.ShouldBe(40L);
        r0.NativeCountry.ShouldBe("United-States");
        r0.Income.ShouldBe("<=50K");
    }

    [Fact]
    public async Task ToArrayAsyncDeserializesDiamondsDataset()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "diamonds.parquet");
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        await using var stream = System.IO.File.OpenRead(filePath);
        var records = await DiamondRecordParquet.From(stream).ToArrayAsync();

        records.Length.ShouldBe(53940);

        var r0 = records[0];
        r0.Carat!.Value.ShouldBe(0.23, 0.005);
        r0.Cut.ShouldBe(2L);
        r0.Color.ShouldBe(1L);
        r0.Clarity.ShouldBe(3L);
        r0.Depth!.Value.ShouldBe(61.5, 0.05);
        r0.Table!.Value.ShouldBe(55.0, 0.05);
        r0.X!.Value.ShouldBe(3.95, 0.005);
        r0.Y!.Value.ShouldBe(3.98, 0.005);
        r0.Z!.Value.ShouldBe(2.43, 0.005);
    }

    [Fact]
    public async Task TpchLineItemRoundtripsWithSnappyAndZstd()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet");
        await using var stream = System.IO.File.OpenRead(filePath);
        var original = await TpchLineItemRecordParquet.From(stream).ToArrayAsync();

        // Round-trip with Snappy
        using var snappyStream = new MemoryStream();
        var snappyOptions = new ParquetSerializerOptions
        {
            CompressionMethod = ParquetCompressionMethod.Snappy,
            RowGroupSize = 20_000,
        };
        await original.WriteParquetAsync(snappyStream, options: snappyOptions);
        snappyStream.Position = 0;

        var roundtrippedSnappy = await TpchLineItemRecordParquet.From(snappyStream).ToArrayAsync();
        roundtrippedSnappy.Length.ShouldBe(original.Length);
        roundtrippedSnappy[0].OrderKey.ShouldBe(original[0].OrderKey);
        roundtrippedSnappy[0].Quantity.ShouldBe(original[0].Quantity);
        roundtrippedSnappy[0].Comment.ShouldBe(original[0].Comment);

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

        var roundtrippedZstd = await TpchLineItemRecordParquet.From(zstdStream).ToArrayAsync();
        roundtrippedZstd.Length.ShouldBe(original.Length);
        roundtrippedZstd[100].ExtendedPrice.ShouldBe(original[100].ExtendedPrice);
        roundtrippedZstd[100].ShipInstruct.ShouldBe(original[100].ShipInstruct);
    }

    [Fact]
    public async Task AdultCensusRoundtripsWithParallelReader()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "adult_census_income.parquet");
        await using var stream = System.IO.File.OpenRead(filePath);
        var original = await AdultCensusRecordParquet.From(stream).ToArrayAsync();

        using var outputStream = new MemoryStream();
        await original.WriteParquetBatchedAsync(
            outputStream,
            new ParquetSerializerOptions { RowGroupSize = 5_000 }
        );
        byte[] bytes = outputStream.ToArray();

        var roundtripped = await AdultCensusRecordParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
            .Parallel()
            .ToArrayAsync();

        roundtripped.Length.ShouldBe(original.Length);
        for (int i = 0; i < 50; i++)
        {
            roundtripped[i].Workclass.ShouldBe(original[i].Workclass);
            roundtripped[i].Education.ShouldBe(original[i].Education);
            roundtripped[i].Income.ShouldBe(original[i].Income);
        }
    }

    [Fact]
    public async Task ParquetSerializerDeserializesTpchLineitemDatasetIdenticalToSourceGenerator()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet");
        await using var stream1 = System.IO.File.OpenRead(filePath);
        var sgRecords = await TpchLineItemRecordParquet.From(stream1).ToArrayAsync();

        await using var stream2 = System.IO.File.OpenRead(filePath);
        var reflectionResult = await ParquetSerializer.DeserializeAsync<TpchLineItemRecord>(
            stream2
        );
        var refRecords = reflectionResult.Data;

        refRecords.Count.ShouldBe(sgRecords.Length);
        for (int i = 0; i < 100; i++)
        {
            refRecords[i].OrderKey.ShouldBe(sgRecords[i].OrderKey);
            refRecords[i].PartKey.ShouldBe(sgRecords[i].PartKey);
            refRecords[i].SuppKey.ShouldBe(sgRecords[i].SuppKey);
            refRecords[i].LineNumber.ShouldBe(sgRecords[i].LineNumber);
            refRecords[i].Quantity.ShouldBe(sgRecords[i].Quantity);
            refRecords[i].ExtendedPrice.ShouldBe(sgRecords[i].ExtendedPrice);
            refRecords[i].Discount.ShouldBe(sgRecords[i].Discount);
            refRecords[i].Tax.ShouldBe(sgRecords[i].Tax);
            refRecords[i].ReturnFlag.ShouldBe(sgRecords[i].ReturnFlag);
            refRecords[i].LineStatus.ShouldBe(sgRecords[i].LineStatus);
            refRecords[i].ShipDate.ShouldBe(sgRecords[i].ShipDate);
            refRecords[i].CommitDate.ShouldBe(sgRecords[i].CommitDate);
            refRecords[i].ReceiptDate.ShouldBe(sgRecords[i].ReceiptDate);
            refRecords[i].ShipInstruct.ShouldBe(sgRecords[i].ShipInstruct);
            refRecords[i].ShipMode.ShouldBe(sgRecords[i].ShipMode);
            refRecords[i].Comment.ShouldBe(sgRecords[i].Comment);
        }
    }

    [Fact]
    public async Task ParquetSerializerDeserializesAdultCensusIncomeDatasetIdenticalToSourceGenerator()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "adult_census_income.parquet");
        await using var stream1 = System.IO.File.OpenRead(filePath);
        var sgRecords = await AdultCensusRecordParquet.From(stream1).ToArrayAsync();

        await using var stream2 = System.IO.File.OpenRead(filePath);
        var reflectionResult = await ParquetSerializer.DeserializeAsync<AdultCensusRecord>(stream2);
        var refRecords = reflectionResult.Data;

        refRecords.Count.ShouldBe(sgRecords.Length);
        for (int i = 0; i < 100; i++)
        {
            refRecords[i].Age.ShouldBe(sgRecords[i].Age);
            refRecords[i].Workclass.ShouldBe(sgRecords[i].Workclass);
            refRecords[i].Fnlwgt.ShouldBe(sgRecords[i].Fnlwgt);
            refRecords[i].Education.ShouldBe(sgRecords[i].Education);
            refRecords[i].EducationNum.ShouldBe(sgRecords[i].EducationNum);
            refRecords[i].MaritalStatus.ShouldBe(sgRecords[i].MaritalStatus);
            refRecords[i].Occupation.ShouldBe(sgRecords[i].Occupation);
            refRecords[i].Relationship.ShouldBe(sgRecords[i].Relationship);
            refRecords[i].Race.ShouldBe(sgRecords[i].Race);
            refRecords[i].Sex.ShouldBe(sgRecords[i].Sex);
            refRecords[i].CapitalGain.ShouldBe(sgRecords[i].CapitalGain);
            refRecords[i].CapitalLoss.ShouldBe(sgRecords[i].CapitalLoss);
            refRecords[i].HoursPerWeek.ShouldBe(sgRecords[i].HoursPerWeek);
            refRecords[i].NativeCountry.ShouldBe(sgRecords[i].NativeCountry);
            refRecords[i].Income.ShouldBe(sgRecords[i].Income);
        }
    }

    [Fact]
    public async Task ParquetSerializerDeserializesDiamondsDatasetIdenticalToSourceGenerator()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "diamonds.parquet");
        await using var stream1 = System.IO.File.OpenRead(filePath);
        var sgRecords = await DiamondRecordParquet.From(stream1).ToArrayAsync();

        await using var stream2 = System.IO.File.OpenRead(filePath);
        var reflectionResult = await ParquetSerializer.DeserializeAsync<DiamondRecord>(stream2);
        var refRecords = reflectionResult.Data;

        refRecords.Count.ShouldBe(sgRecords.Length);
        for (int i = 0; i < 100; i++)
        {
            refRecords[i].Carat.ShouldBe(sgRecords[i].Carat);
            refRecords[i].Cut.ShouldBe(sgRecords[i].Cut);
            refRecords[i].Color.ShouldBe(sgRecords[i].Color);
            refRecords[i].Clarity.ShouldBe(sgRecords[i].Clarity);
            refRecords[i].Depth.ShouldBe(sgRecords[i].Depth);
            refRecords[i].Table.ShouldBe(sgRecords[i].Table);
            refRecords[i].X.ShouldBe(sgRecords[i].X);
            refRecords[i].Y.ShouldBe(sgRecords[i].Y);
            refRecords[i].Z.ShouldBe(sgRecords[i].Z);
            refRecords[i].Price.ShouldBe(sgRecords[i].Price);
        }
    }
}
