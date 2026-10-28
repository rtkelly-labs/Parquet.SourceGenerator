using System.Text.Json.Serialization;
using DuckDB.NET.Data;

namespace Parquet.SourceGenerator.CrossLibraryBenchmarks;

public enum Dataset
{
    Tpch,
    Census,
    Diamonds,
}

internal static class Datasets
{
    public static string FileName(Dataset dataset) =>
        dataset switch
        {
            Dataset.Tpch => "tpch_lineitem_sf001.parquet",
            Dataset.Census => "adult_census_income.parquet",
            Dataset.Diamonds => "diamonds.parquet",
            _ => throw new ArgumentOutOfRangeException(nameof(dataset)),
        };

    public static long RowCount(Dataset dataset) =>
        dataset switch
        {
            Dataset.Tpch => 60_175,
            Dataset.Census => 32_561,
            Dataset.Diamonds => 53_940,
            _ => throw new ArgumentOutOfRangeException(nameof(dataset)),
        };

    public static string PathOf(Dataset dataset)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "data", FileName(dataset));
        return System.IO.File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Cannot locate benchmark dataset: {path}");
    }

    /// <summary>
    /// Every comparand reads the file from disk (page cache warm after the first iteration):
    /// DuckDB has no in-memory buffer source, so a byte[] input would favour the managed readers.
    /// </summary>
    public static FileStream OpenRead(Dataset dataset) =>
        new(PathOf(dataset), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

    /// <summary>
    /// An in-process DuckDB connection. <paramref name="threads"/> = 1 pins the scan to one core,
    /// matching the single-threaded managed readers; 0 leaves DuckDB's default (all cores).
    /// </summary>
    public static DuckDBConnection OpenDuckDb(int threads)
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        if (threads > 0)
        {
            using DuckDBCommand command = connection.CreateCommand();
            command.CommandText = $"SET threads TO {threads}";
            command.ExecuteNonQuery();
        }

        return connection;
    }

    public static string SelectAll(Dataset dataset) =>
        $"SELECT * FROM read_parquet('{PathOf(dataset).Replace("'", "''", StringComparison.Ordinal)}')";
}

// The models mirror Parquet.SourceGenerator.Benchmarks' provenanced records. They are redeclared
// here rather than shared because that project is an executable with its own generated readers.

[ParquetSerializable]
public partial record TpchLineItem
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

    public static readonly string[] ColumnNames =
    [
        "l_orderkey",
        "l_partkey",
        "l_suppkey",
        "l_linenumber",
        "l_quantity",
        "l_extendedprice",
        "l_discount",
        "l_tax",
        "l_returnflag",
        "l_linestatus",
        "l_shipdate",
        "l_commitdate",
        "l_receiptdate",
        "l_shipinstruct",
        "l_shipmode",
        "l_comment",
    ];

    public static readonly string[] PropertyNames =
    [
        nameof(OrderKey),
        nameof(PartKey),
        nameof(SuppKey),
        nameof(LineNumber),
        nameof(Quantity),
        nameof(ExtendedPrice),
        nameof(Discount),
        nameof(Tax),
        nameof(ReturnFlag),
        nameof(LineStatus),
        nameof(ShipDate),
        nameof(CommitDate),
        nameof(ReceiptDate),
        nameof(ShipInstruct),
        nameof(ShipMode),
        nameof(Comment),
    ];
}

[ParquetSerializable]
public partial record AdultCensus
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

    public static readonly string[] ColumnNames =
    [
        "age",
        "workclass",
        "fnlwgt",
        "education",
        "education.num",
        "marital.status",
        "occupation",
        "relationship",
        "race",
        "sex",
        "capital.gain",
        "capital.loss",
        "hours.per.week",
        "native.country",
        "income",
    ];

    public static readonly string[] PropertyNames =
    [
        nameof(Age),
        nameof(Workclass),
        nameof(Fnlwgt),
        nameof(Education),
        nameof(EducationNum),
        nameof(MaritalStatus),
        nameof(Occupation),
        nameof(Relationship),
        nameof(Race),
        nameof(Sex),
        nameof(CapitalGain),
        nameof(CapitalLoss),
        nameof(HoursPerWeek),
        nameof(NativeCountry),
        nameof(Income),
    ];
}

[ParquetSerializable]
public partial record Diamonds
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

    public static readonly string[] ColumnNames =
    [
        "carat",
        "cut",
        "color",
        "clarity",
        "depth",
        "table",
        "x",
        "y",
        "z",
        "price",
    ];

    public static readonly string[] PropertyNames =
    [
        nameof(Carat),
        nameof(Cut),
        nameof(Color),
        nameof(Clarity),
        nameof(Depth),
        nameof(Table),
        nameof(X),
        nameof(Y),
        nameof(Z),
        nameof(Price),
    ];
}
