using System.Data.Common;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using DuckDB.NET.Data;
using Parquet.Serialization;
using ParquetSharp;
using Polars.CSharp;
using Pl = Polars.CSharp.Polars;

namespace Parquet.SourceGenerator.CrossLibraryBenchmarks;

/// <summary>
/// Typed-row tier: every comparand materialises the whole file into the same record type, single
/// threaded. This is the question a user of this generator asks ("which library gets my file into
/// my objects fastest"), so wrappers pay for crossing the native boundary and for row assembly.
/// <see cref="Program"/>'s <c>--validate</c> mode asserts each one returns rows equal to the
/// generator's before any timing is trusted.
/// </summary>
/// <remarks>
/// [MemoryDiagnoser] counts managed allocations only. ParquetSharp, DuckDB and Polars decode into
/// native memory that does not appear in the Allocated column.
/// </remarks>
[MemoryDiagnoser]
[InProcess]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public abstract class TypedReadBenchmark<T>
    where T : class, new()
{
    private DuckDBConnection _duckDb = null!;

    protected abstract Dataset Dataset { get; }

    protected abstract string[] ColumnNames { get; }

    protected abstract string[] PropertyNames { get; }

    [GlobalSetup]
    public void Setup() => _duckDb = Datasets.OpenDuckDb(threads: 1);

    [GlobalCleanup]
    public void Cleanup() => _duckDb.Dispose();

    /// <summary>Parquet.Net's reflection-based <see cref="ParquetSerializer"/> (pure managed).</summary>
    [Benchmark(Baseline = true)]
    public async Task<IList<T>> ParquetNet()
    {
        await using FileStream stream = Datasets.OpenRead(Dataset);
        return (await ParquetSerializer.DeserializeAsync<T>(stream)).Data;
    }

    /// <summary>This repository's generated reader (pure managed, built on Parquet.Net's decoder).</summary>
    [Benchmark]
    public async Task<T[]> SourceGenerator()
    {
        await using FileStream stream = Datasets.OpenRead(Dataset);
        return await ReadSourceGeneratorAsync(stream);
    }

    /// <summary>ParquetSharp (Arrow C++) logical column readers, hand-mapped to rows.</summary>
    [Benchmark]
    public T[] ParquetSharp()
    {
        using var file = new ParquetFileReader(Datasets.PathOf(Dataset));
        var rows = new T[checked((int)file.FileMetaData.NumRows)];
        int offset = 0;
        for (int g = 0; g < file.FileMetaData.NumRowGroups; g++)
        {
            using RowGroupReader rowGroup = file.RowGroup(g);
            int count = checked((int)rowGroup.MetaData.NumRows);
            MapParquetSharp(rowGroup, count, rows.AsSpan(offset, count));
            offset += count;
        }

        file.Close();
        return rows;
    }

    /// <summary>DuckDB (C++) <c>read_parquet</c> through ADO.NET, one thread, hand-mapped.</summary>
    [Benchmark]
    public List<T> DuckDb()
    {
        using DuckDBCommand command = _duckDb.CreateCommand();
        command.CommandText = Datasets.SelectAll(Dataset);
        using DbDataReader reader = command.ExecuteReader();
        var rows = new List<T>(checked((int)Datasets.RowCount(Dataset)));
        while (reader.Read())
        {
            rows.Add(MapDuckDb(reader));
        }

        return rows;
    }

    /// <summary>Polars (Rust) <c>ScanParquet</c> with no parallelism, then its <c>Rows&lt;T&gt;</c> mapper.</summary>
    /// <remarks>
    /// Polars.NET 1.0.0 workarounds, both inside the lazy plan: its row mapper matches columns to
    /// properties by name, and an eager <c>DataFrame.Rename</c> leaves column lookup on the old
    /// names, so columns are aliased; and it cannot map a <c>Date</c> column to <c>DateTime?</c>, so
    /// date columns are cast to <c>Datetime</c>.
    /// </remarks>
    [Benchmark]
    public List<T> Polars()
    {
        var projection = new Expr[ColumnNames.Length];
        for (int i = 0; i < projection.Length; i++)
        {
            Expr column = Pl.Col(ColumnNames[i]);
            if (DateColumns.Contains(ColumnNames[i]))
            {
                column = column.Cast(DataType.Datetime());
            }

            projection[i] = column.Alias(PropertyNames[i]);
        }

        using DataFrame frame = LazyFrame
            .ScanParquet(Datasets.PathOf(Dataset), parallel: ParallelStrategy.None)
            .Select(projection)
            .Collect();
        var rows = new List<T>(checked((int)frame.Height));
        DataFrameRowEnumerator<T> enumerator = frame.Rows<T>();
        while (enumerator.MoveNext())
        {
            rows.Add(enumerator.Current);
        }

        return rows;
    }

    protected virtual IReadOnlySet<string> DateColumns { get; } = new HashSet<string>();

    protected abstract Task<T[]> ReadSourceGeneratorAsync(Stream stream);

    protected abstract void MapParquetSharp(RowGroupReader rowGroup, int count, Span<T> rows);

    protected abstract T MapDuckDb(DbDataReader reader);

    protected static TValue[] Column<TValue>(RowGroupReader rowGroup, int index, int count)
    {
        using ColumnReader column = rowGroup.Column(index);
        using LogicalColumnReader<TValue> reader = column.LogicalReader<TValue>();
        return reader.ReadAll(count);
    }

    protected static long? ReadInt64(DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetInt64(i);

    protected static double? ReadDouble(DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetDouble(i);

    protected static decimal? ReadDecimal(DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetDecimal(i);

    protected static DateTime? ReadDateTime(DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetDateTime(i);

    protected static string? ReadString(DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetString(i);
}

public class TpchTypedRead : TypedReadBenchmark<TpchLineItem>
{
    protected override Dataset Dataset => Dataset.Tpch;

    protected override string[] ColumnNames => TpchLineItem.ColumnNames;

    protected override string[] PropertyNames => TpchLineItem.PropertyNames;

    protected override IReadOnlySet<string> DateColumns { get; } =
        new HashSet<string> { "l_shipdate", "l_commitdate", "l_receiptdate" };

    protected override Task<TpchLineItem[]> ReadSourceGeneratorAsync(Stream stream) =>
        TpchLineItemParquet.From(stream).ToArrayAsync();

    protected override void MapParquetSharp(
        RowGroupReader rowGroup,
        int count,
        Span<TpchLineItem> rows
    )
    {
        long?[] orderKey = Column<long?>(rowGroup, 0, count);
        long?[] partKey = Column<long?>(rowGroup, 1, count);
        long?[] suppKey = Column<long?>(rowGroup, 2, count);
        long?[] lineNumber = Column<long?>(rowGroup, 3, count);
        decimal?[] quantity = Column<decimal?>(rowGroup, 4, count);
        decimal?[] extendedPrice = Column<decimal?>(rowGroup, 5, count);
        decimal?[] discount = Column<decimal?>(rowGroup, 6, count);
        decimal?[] tax = Column<decimal?>(rowGroup, 7, count);
        string?[] returnFlag = Column<string?>(rowGroup, 8, count);
        string?[] lineStatus = Column<string?>(rowGroup, 9, count);
        Date?[] shipDate = Column<Date?>(rowGroup, 10, count);
        Date?[] commitDate = Column<Date?>(rowGroup, 11, count);
        Date?[] receiptDate = Column<Date?>(rowGroup, 12, count);
        string?[] shipInstruct = Column<string?>(rowGroup, 13, count);
        string?[] shipMode = Column<string?>(rowGroup, 14, count);
        string?[] comment = Column<string?>(rowGroup, 15, count);
        for (int i = 0; i < count; i++)
        {
            rows[i] = new TpchLineItem
            {
                OrderKey = orderKey[i],
                PartKey = partKey[i],
                SuppKey = suppKey[i],
                LineNumber = lineNumber[i],
                Quantity = quantity[i],
                ExtendedPrice = extendedPrice[i],
                Discount = discount[i],
                Tax = tax[i],
                ReturnFlag = returnFlag[i],
                LineStatus = lineStatus[i],
                ShipDate = shipDate[i]?.DateTime,
                CommitDate = commitDate[i]?.DateTime,
                ReceiptDate = receiptDate[i]?.DateTime,
                ShipInstruct = shipInstruct[i],
                ShipMode = shipMode[i],
                Comment = comment[i],
            };
        }
    }

    protected override TpchLineItem MapDuckDb(DbDataReader reader) =>
        new()
        {
            OrderKey = ReadInt64(reader, 0),
            PartKey = ReadInt64(reader, 1),
            SuppKey = ReadInt64(reader, 2),
            LineNumber = ReadInt64(reader, 3),
            Quantity = ReadDecimal(reader, 4),
            ExtendedPrice = ReadDecimal(reader, 5),
            Discount = ReadDecimal(reader, 6),
            Tax = ReadDecimal(reader, 7),
            ReturnFlag = ReadString(reader, 8),
            LineStatus = ReadString(reader, 9),
            ShipDate = ReadDateTime(reader, 10),
            CommitDate = ReadDateTime(reader, 11),
            ReceiptDate = ReadDateTime(reader, 12),
            ShipInstruct = ReadString(reader, 13),
            ShipMode = ReadString(reader, 14),
            Comment = ReadString(reader, 15),
        };
}

public class CensusTypedRead : TypedReadBenchmark<AdultCensus>
{
    protected override Dataset Dataset => Dataset.Census;

    protected override string[] ColumnNames => AdultCensus.ColumnNames;

    protected override string[] PropertyNames => AdultCensus.PropertyNames;

    protected override Task<AdultCensus[]> ReadSourceGeneratorAsync(Stream stream) =>
        AdultCensusParquet.From(stream).ToArrayAsync();

    protected override void MapParquetSharp(
        RowGroupReader rowGroup,
        int count,
        Span<AdultCensus> rows
    )
    {
        long?[] age = Column<long?>(rowGroup, 0, count);
        string?[] workclass = Column<string?>(rowGroup, 1, count);
        long?[] fnlwgt = Column<long?>(rowGroup, 2, count);
        string?[] education = Column<string?>(rowGroup, 3, count);
        long?[] educationNum = Column<long?>(rowGroup, 4, count);
        string?[] maritalStatus = Column<string?>(rowGroup, 5, count);
        string?[] occupation = Column<string?>(rowGroup, 6, count);
        string?[] relationship = Column<string?>(rowGroup, 7, count);
        string?[] race = Column<string?>(rowGroup, 8, count);
        string?[] sex = Column<string?>(rowGroup, 9, count);
        long?[] capitalGain = Column<long?>(rowGroup, 10, count);
        long?[] capitalLoss = Column<long?>(rowGroup, 11, count);
        long?[] hoursPerWeek = Column<long?>(rowGroup, 12, count);
        string?[] nativeCountry = Column<string?>(rowGroup, 13, count);
        string?[] income = Column<string?>(rowGroup, 14, count);
        for (int i = 0; i < count; i++)
        {
            rows[i] = new AdultCensus
            {
                Age = age[i],
                Workclass = workclass[i],
                Fnlwgt = fnlwgt[i],
                Education = education[i],
                EducationNum = educationNum[i],
                MaritalStatus = maritalStatus[i],
                Occupation = occupation[i],
                Relationship = relationship[i],
                Race = race[i],
                Sex = sex[i],
                CapitalGain = capitalGain[i],
                CapitalLoss = capitalLoss[i],
                HoursPerWeek = hoursPerWeek[i],
                NativeCountry = nativeCountry[i],
                Income = income[i],
            };
        }
    }

    protected override AdultCensus MapDuckDb(DbDataReader reader) =>
        new()
        {
            Age = ReadInt64(reader, 0),
            Workclass = ReadString(reader, 1),
            Fnlwgt = ReadInt64(reader, 2),
            Education = ReadString(reader, 3),
            EducationNum = ReadInt64(reader, 4),
            MaritalStatus = ReadString(reader, 5),
            Occupation = ReadString(reader, 6),
            Relationship = ReadString(reader, 7),
            Race = ReadString(reader, 8),
            Sex = ReadString(reader, 9),
            CapitalGain = ReadInt64(reader, 10),
            CapitalLoss = ReadInt64(reader, 11),
            HoursPerWeek = ReadInt64(reader, 12),
            NativeCountry = ReadString(reader, 13),
            Income = ReadString(reader, 14),
        };
}

public class DiamondsTypedRead : TypedReadBenchmark<Diamonds>
{
    protected override Dataset Dataset => Dataset.Diamonds;

    protected override string[] ColumnNames => Diamonds.ColumnNames;

    protected override string[] PropertyNames => Diamonds.PropertyNames;

    protected override Task<Diamonds[]> ReadSourceGeneratorAsync(Stream stream) =>
        DiamondsParquet.From(stream).ToArrayAsync();

    protected override void MapParquetSharp(RowGroupReader rowGroup, int count, Span<Diamonds> rows)
    {
        double?[] carat = Column<double?>(rowGroup, 0, count);
        long?[] cut = Column<long?>(rowGroup, 1, count);
        long?[] color = Column<long?>(rowGroup, 2, count);
        long?[] clarity = Column<long?>(rowGroup, 3, count);
        double?[] depth = Column<double?>(rowGroup, 4, count);
        double?[] table = Column<double?>(rowGroup, 5, count);
        double?[] x = Column<double?>(rowGroup, 6, count);
        double?[] y = Column<double?>(rowGroup, 7, count);
        double?[] z = Column<double?>(rowGroup, 8, count);
        double?[] price = Column<double?>(rowGroup, 9, count);
        for (int i = 0; i < count; i++)
        {
            rows[i] = new Diamonds
            {
                Carat = carat[i],
                Cut = cut[i],
                Color = color[i],
                Clarity = clarity[i],
                Depth = depth[i],
                Table = table[i],
                X = x[i],
                Y = y[i],
                Z = z[i],
                Price = price[i],
            };
        }
    }

    protected override Diamonds MapDuckDb(DbDataReader reader) =>
        new()
        {
            Carat = ReadDouble(reader, 0),
            Cut = ReadInt64(reader, 1),
            Color = ReadInt64(reader, 2),
            Clarity = ReadInt64(reader, 3),
            Depth = ReadDouble(reader, 4),
            Table = ReadDouble(reader, 5),
            X = ReadDouble(reader, 6),
            Y = ReadDouble(reader, 7),
            Z = ReadDouble(reader, 8),
            Price = ReadDouble(reader, 9),
        };
}
