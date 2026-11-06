using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Parquet.Serialization;

namespace Parquet.SourceGenerator.Benchmarks;

[ParquetSerializable]
public partial record BenchmarkTpchLineItem
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
public partial record BenchmarkAdultCensus
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
public partial record BenchmarkDiamonds
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

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class TpchLineItemBenchmark
{
    private byte[] _rawBytes = null!;
    private List<BenchmarkTpchLineItem> _records = null!;

    private readonly ParquetSerializerOptions _snappyOptions = new()
    {
        CompressionMethod = ParquetCompressionMethod.Snappy,
        RowGroupSize = 20_000,
    };

    private readonly ParquetSerializerOptions _zstdFastestOptions = new()
    {
        CompressionMethod = ParquetCompressionMethod.Zstd,
        CompressionLevel = ParquetCompressionLevel.Fastest,
        RowGroupSize = 20_000,
    };

    private readonly ParquetSerializerOptions _zstdOptimalOptions = new()
    {
        CompressionMethod = ParquetCompressionMethod.Zstd,
        CompressionLevel = ParquetCompressionLevel.Optimal,
        RowGroupSize = 20_000,
    };

    private readonly ParquetSerializerOptions _uncompressedOptions = new()
    {
        CompressionMethod = ParquetCompressionMethod.None,
        RowGroupSize = 20_000,
    };

    [GlobalSetup]
    public void Setup()
    {
        string dataPath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "tpch_lineitem_sf001.parquet"
        );
        if (!System.IO.File.Exists(dataPath))
        {
            // Fallback for execution from source tree root
            dataPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "benchmarks",
                    "data",
                    "tpch_lineitem_sf001.parquet"
                )
            );
        }

        if (!System.IO.File.Exists(dataPath))
        {
            throw new FileNotFoundException($"Cannot locate benchmark dataset: {dataPath}");
        }

        _rawBytes = System.IO.File.ReadAllBytes(dataPath);

        using var ms = new MemoryStream(_rawBytes);
        _records = BenchmarkTpchLineItemParquetExtensions
            .ReadParquetAsync(ms)
            .GetAwaiter()
            .GetResult();

        if (_records.Count != 60175)
        {
            throw new InvalidOperationException(
                $"Expected 60,175 records in TPC-H dataset, but loaded {_records.Count}"
            );
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<IList<BenchmarkTpchLineItem>> ReflectionParquetSerializerTpchRead()
    {
        using var stream = new MemoryStream(_rawBytes);
        var result = await ParquetSerializer.DeserializeAsync<BenchmarkTpchLineItem>(stream);
        return result.Data;
    }

    [Benchmark]
    public async Task<List<BenchmarkTpchLineItem>> SourceGeneratorTpchReadAsync()
    {
        using var stream = new MemoryStream(_rawBytes);
        return await BenchmarkTpchLineItemParquetExtensions.ReadParquetAsync(stream);
    }

    [Benchmark]
    public async Task<List<BenchmarkTpchLineItem>> SourceGeneratorTpchReadParallelBufferAsync()
    {
        return await BenchmarkTpchLineItemParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(_rawBytes),
            new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 }
        );
    }

    [Benchmark]
    public async Task<int> SourceGeneratorTpchReadStreamAsync()
    {
        using var stream = new MemoryStream(_rawBytes);
        int count = 0;
        await foreach (
            var item in BenchmarkTpchLineItemParquetExtensions.ReadParquetStreamAsync(stream)
        )
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task WriteSnappyAsync()
    {
        using var stream = new MemoryStream();
        await _records.WriteParquetAsync(stream, options: _snappyOptions);
    }

    [Benchmark]
    public async Task WriteZstdFastestAsync()
    {
        using var stream = new MemoryStream();
        await _records.WriteParquetAsync(stream, options: _zstdFastestOptions);
    }

    [Benchmark]
    public async Task WriteZstdOptimalAsync()
    {
        using var stream = new MemoryStream();
        await _records.WriteParquetAsync(stream, options: _zstdOptimalOptions);
    }

    [Benchmark]
    public async Task WriteUncompressedAsync()
    {
        using var stream = new MemoryStream();
        await _records.WriteParquetAsync(stream, options: _uncompressedOptions);
    }
}

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class AdultCensusBenchmark
{
    private byte[] _rawBytes = null!;
    private List<BenchmarkAdultCensus> _records = null!;
    private ParquetSerializerOptions _snappyOptions = null!;

    [GlobalSetup]
    public void Setup()
    {
        string dataPath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "adult_census_income.parquet"
        );
        if (!System.IO.File.Exists(dataPath))
        {
            dataPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "benchmarks",
                    "data",
                    "adult_census_income.parquet"
                )
            );
        }

        if (!System.IO.File.Exists(dataPath))
        {
            throw new FileNotFoundException($"Cannot locate benchmark dataset: {dataPath}");
        }

        _rawBytes = System.IO.File.ReadAllBytes(dataPath);

        using var initStream = new MemoryStream(_rawBytes);
        _records = BenchmarkAdultCensusParquetExtensions
            .ReadParquetAsync(initStream)
            .GetAwaiter()
            .GetResult();
        _snappyOptions = new ParquetSerializerOptions
        {
            CompressionMethod = ParquetCompressionMethod.Snappy,
        };
    }

    [Benchmark(Baseline = true)]
    public async Task<IList<BenchmarkAdultCensus>> ReflectionParquetSerializerCensusRead()
    {
        using var stream = new MemoryStream(_rawBytes);
        var result = await ParquetSerializer.DeserializeAsync<BenchmarkAdultCensus>(stream);
        return result.Data;
    }

    [Benchmark]
    public async Task<List<BenchmarkAdultCensus>> SourceGeneratorCensusReadAsync()
    {
        using var stream = new MemoryStream(_rawBytes);
        return await BenchmarkAdultCensusParquetExtensions.ReadParquetAsync(stream);
    }

    [Benchmark]
    public async Task<List<BenchmarkAdultCensus>> SourceGeneratorCensusReadParallelBufferAsync()
    {
        return await BenchmarkAdultCensusParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(_rawBytes),
            new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 }
        );
    }

    [Benchmark]
    public async Task<int> SourceGeneratorCensusReadStreamAsync()
    {
        using var stream = new MemoryStream(_rawBytes);
        int count = 0;
        await foreach (
            var item in BenchmarkAdultCensusParquetExtensions.ReadParquetStreamAsync(stream)
        )
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task WriteSnappyAsync()
    {
        using var stream = new MemoryStream();
        await _records.WriteParquetAsync(stream, options: _snappyOptions);
    }
}

[MemoryDiagnoser]
[InProcess]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class DiamondsBenchmark
{
    private byte[] _rawBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        string dataPath = Path.Combine(AppContext.BaseDirectory, "data", "diamonds.parquet");
        if (!System.IO.File.Exists(dataPath))
        {
            dataPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "benchmarks",
                    "data",
                    "diamonds.parquet"
                )
            );
        }

        if (!System.IO.File.Exists(dataPath))
        {
            throw new FileNotFoundException($"Cannot locate benchmark dataset: {dataPath}");
        }

        _rawBytes = System.IO.File.ReadAllBytes(dataPath);
    }

    [Benchmark(Baseline = true)]
    public async Task<IList<BenchmarkDiamonds>> ReflectionParquetSerializerDiamondsRead()
    {
        using var stream = new MemoryStream(_rawBytes);
        var result = await ParquetSerializer.DeserializeAsync<BenchmarkDiamonds>(stream);
        return result.Data;
    }

    [Benchmark]
    public async Task<List<BenchmarkDiamonds>> SourceGeneratorDiamondsReadAsync()
    {
        using var stream = new MemoryStream(_rawBytes);
        return await BenchmarkDiamondsParquetExtensions.ReadParquetAsync(stream);
    }

    [Benchmark]
    public async Task<List<BenchmarkDiamonds>> SourceGeneratorDiamondsReadParallelBufferAsync()
    {
        return await BenchmarkDiamondsParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(_rawBytes),
            new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 }
        );
    }

    [Benchmark]
    public async Task<int> SourceGeneratorDiamondsReadStreamAsync()
    {
        using var stream = new MemoryStream(_rawBytes);
        int count = 0;
        await foreach (
            var item in BenchmarkDiamondsParquetExtensions.ReadParquetStreamAsync(stream)
        )
        {
            count++;
        }
        return count;
    }
}
