using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using DuckDB.NET.Data;
using Parquet.Data;
using Parquet.Schema;
using Polars.CSharp;

namespace Parquet.SourceGenerator.CrossLibraryBenchmarks;

/// <summary>
/// Columnar tier: decode every column of the file into each library's native columnar form and
/// stop. No row objects. This is closest to comparing the decoders themselves, and is the tier to
/// read when looking for decode optimisations the managed readers lack.
/// </summary>
/// <remarks>
/// The end states differ by design and the comparison should be read with that in mind:
/// the generator yields borrowed <c>&lt;Model&gt;Batch</c> spans; Parquet.Net yields
/// <see cref="RawColumnData"/> (strings as <c>ReadOnlyMemory&lt;char&gt;</c>); ParquetSharp yields
/// Arrow record batches; DuckDB converts its vectors to Arrow (an extra copy); Polars keeps a
/// native DataFrame and exports nothing. Each method returns the rows decoded, which
/// <c>--validate</c> checks against the file's row count. The <c>_1T</c> rows pin the native engines
/// to one thread for a like-for-like decoder comparison; <c>_AllCores</c> rows show what each engine
/// does when left alone.
/// </remarks>
[MemoryDiagnoser]
[InProcess]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ColumnarReadBenchmark
{
    private DuckDBConnection _duckDbSingle = null!;
    private DuckDBConnection _duckDbAll = null!;

    [Params(Dataset.Tpch, Dataset.Census, Dataset.Diamonds)]
    public Dataset Dataset { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _duckDbSingle = Datasets.OpenDuckDb(threads: 1);
        _duckDbAll = Datasets.OpenDuckDb(threads: 0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _duckDbSingle.Dispose();
        _duckDbAll.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<long> ParquetNet_RawColumns()
    {
        await using FileStream stream = Datasets.OpenRead(Dataset);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        DataField[] fields = reader.Schema.GetDataFields();
        long rows = 0;
        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            foreach (DataField field in fields)
            {
                using RawColumnData column = await rowGroup.ReadRawColumnDataBaseAsync(field);
            }

            rows += rowGroup.RowCount;
        }

        return rows;
    }

    [Benchmark]
    public async Task<long> SourceGenerator_Batches()
    {
        await using FileStream stream = Datasets.OpenRead(Dataset);
        long rows = 0;
        switch (Dataset)
        {
            case Dataset.Tpch:
                await foreach (
                    TpchLineItemBatch batch in TpchLineItemParquet.From(stream).AsBatches()
                )
                {
                    rows += batch.RowCount;
                }

                break;
            case Dataset.Census:
                await foreach (
                    AdultCensusBatch batch in AdultCensusParquet.From(stream).AsBatches()
                )
                {
                    rows += batch.RowCount;
                }

                break;
            case Dataset.Diamonds:
                await foreach (DiamondsBatch batch in DiamondsParquet.From(stream).AsBatches())
                {
                    rows += batch.RowCount;
                }

                break;
        }

        return rows;
    }

    [Benchmark]
    public async Task<long> ParquetSharp_Arrow_1T()
    {
        using var reader = new ParquetSharp.Arrow.FileReader(Datasets.PathOf(Dataset));
        using IArrowArrayStream batches = reader.GetRecordBatchReader();
        return await CountAsync(batches);
    }

    [Benchmark]
    public async Task<long> DuckDb_Arrow_1T() => await DuckDbArrowAsync(_duckDbSingle);

    [Benchmark]
    public async Task<long> DuckDb_Arrow_AllCores() => await DuckDbArrowAsync(_duckDbAll);

    [Benchmark]
    public long Polars_DataFrame_1T() => PolarsRead(ParallelStrategy.None);

    [Benchmark]
    public long Polars_DataFrame_AllCores() => PolarsRead(ParallelStrategy.Auto);

    private async Task<long> DuckDbArrowAsync(DuckDBConnection connection)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Datasets.SelectAll(Dataset);
        using IArrowArrayStream batches = command.ExecuteArrowStream();
        return await CountAsync(batches);
    }

    private long PolarsRead(ParallelStrategy parallel)
    {
        using DataFrame frame = DataFrame.ReadParquet(Datasets.PathOf(Dataset), parallel: parallel);
        return frame.Height;
    }

    private static async Task<long> CountAsync(IArrowArrayStream batches)
    {
        long rows = 0;
        while (await batches.ReadNextRecordBatchAsync() is { } batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }
}
