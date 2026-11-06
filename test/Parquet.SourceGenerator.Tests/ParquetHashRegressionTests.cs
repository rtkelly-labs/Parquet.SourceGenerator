using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Cryptographic hash-based regression suite for Parquet generation and dataset integrity.
/// Guarantees:
/// 1. Source-generated serialization (WriteParquetAsync, WriteParquetBatchedAsync) produces
///    strictly bit-for-bit deterministic output across repeated executions.
/// 2. Serialized output for canonical models matches pinned golden SHA-256 hashes, catching any
///    unintended codec, dictionary, schema, or structural binary changes.
/// 3. All tracked repository Parquet datasets (PyArrow v1/v2, C# v3, the pinned older-producer
///    fixtures under test/data_producers, and LFS benchmarks) maintain
///    cryptographic SHA-256 hash integrity.
/// </summary>
public sealed class ParquetHashRegressionTests
{
    private static readonly string SolutionRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );

    private static readonly string TestDataRoot = Path.Combine(SolutionRoot, "test", "data");
    private static readonly string TestDataCSharpRoot = Path.Combine(
        SolutionRoot,
        "test",
        "data_csharp"
    );
    private static readonly string TestDataProducersRoot = Path.Combine(
        SolutionRoot,
        "test",
        "data_producers"
    );
    private static readonly string BenchmarkDataRoot = Path.Combine(
        SolutionRoot,
        "benchmarks",
        "data"
    );
    private static readonly string FixtureManifestPath = Path.Combine(
        TestDataRoot,
        "fixture-manifest.json"
    );
    private static readonly JsonSerializerOptions FixtureManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string ComputeSha256(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static List<TestUserRecord> CreateDeterministicUserRecords(int count = 100)
    {
        return Enumerable
            .Range(0, count)
            .Select(i => new TestUserRecord
            {
                Id = i,
                Name = $"user_{i}",
                Score = (i * 1.5) % 100.0,
                IsActive = i % 2 == 0,
                CreatedAtMs = 1700000000000L + (i * 1000L),
            })
            .ToList();
    }

    private static List<TestNullableRecord> CreateDeterministicNullableRecords(int count = 1000)
    {
        return Enumerable
            .Range(0, count)
            .Select(i => new TestNullableRecord
            {
                Id = i,
                NullableInt = i % 5 == 0 ? null : i * 10,
                NullableDouble = i % 5 == 0 ? null : (i * 3.14159) % 1000.0,
                NullableString = i % 5 == 0 ? null : $"str_val_{i}",
                NullableBool = i % 5 == 0 ? null : (i % 3 == 0),
            })
            .ToList();
    }

    private static List<BenchmarkGuidModel> CreateDeterministicGuidRecords(int count = 500)
    {
        return Enumerable
            .Range(0, count)
            .Select(i => new BenchmarkGuidModel
            {
                Id = i,
                CorrelationId = new Guid(i + 1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8),
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
            })
            .ToList();
    }

    // =========================================================================
    // 1. In-Memory Determinism Tests (Bit-for-Bit Identity Across Repeated Runs)
    // =========================================================================

    [Theory]
    [InlineData(ParquetCompressionMethod.None)]
    [InlineData(ParquetCompressionMethod.Snappy)]
    [InlineData(ParquetCompressionMethod.Gzip)]
    [InlineData(ParquetCompressionMethod.Zstd)]
    public async Task WriteParquetAsyncIsBitForBitDeterministicAcrossCodecs(
        ParquetCompressionMethod compressionMethod
    )
    {
        var records = CreateDeterministicUserRecords(100);
        var options = new ParquetSerializerOptions { CompressionMethod = compressionMethod };

        using var stream1 = new MemoryStream();
        await records.WriteParquetAsync(stream1, options: options);
        byte[] bytes1 = stream1.ToArray();

        using var stream2 = new MemoryStream();
        await records.WriteParquetAsync(stream2, options: options);
        byte[] bytes2 = stream2.ToArray();

        string hash1 = ComputeSha256(bytes1);
        string hash2 = ComputeSha256(bytes2);

        Assert.Equal(hash1, hash2);
        Assert.True(bytes1.AsSpan().SequenceEqual(bytes2.AsSpan()));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task WriteParquetBatchedAsyncIsBitForBitDeterministicAcrossRowGroupSizes(
        int rowGroupSize
    )
    {
        var records = CreateDeterministicUserRecords(100);

        using var stream1 = new MemoryStream();
        await records.WriteParquetBatchedAsync(
            stream1,
            new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
        );
        byte[] bytes1 = stream1.ToArray();

        using var stream2 = new MemoryStream();
        await records.WriteParquetBatchedAsync(
            stream2,
            new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
        );
        byte[] bytes2 = stream2.ToArray();

        string hash1 = ComputeSha256(bytes1);
        string hash2 = ComputeSha256(bytes2);

        Assert.Equal(hash1, hash2);
        Assert.True(bytes1.AsSpan().SequenceEqual(bytes2.AsSpan()));
    }

    [Fact]
    public async Task NullableModelWriteParquetAsyncIsBitForBitDeterministic()
    {
        var records = CreateDeterministicNullableRecords(1000);

        using var stream1 = new MemoryStream();
        await records.WriteParquetAsync(stream1);
        byte[] bytes1 = stream1.ToArray();

        using var stream2 = new MemoryStream();
        await records.WriteParquetAsync(stream2);
        byte[] bytes2 = stream2.ToArray();

        string hash1 = ComputeSha256(bytes1);
        string hash2 = ComputeSha256(bytes2);

        Assert.Equal(hash1, hash2);
        Assert.True(bytes1.AsSpan().SequenceEqual(bytes2.AsSpan()));
    }

    [Fact]
    public async Task GuidModelWriteParquetAsyncIsBitForBitDeterministic()
    {
        var records = CreateDeterministicGuidRecords(500);

        using var stream1 = new MemoryStream();
        await records.WriteParquetAsync(stream1);
        byte[] bytes1 = stream1.ToArray();

        using var stream2 = new MemoryStream();
        await records.WriteParquetAsync(stream2);
        byte[] bytes2 = stream2.ToArray();

        string hash1 = ComputeSha256(bytes1);
        string hash2 = ComputeSha256(bytes2);

        Assert.Equal(hash1, hash2);
        Assert.True(bytes1.AsSpan().SequenceEqual(bytes2.AsSpan()));
    }

    // =========================================================================
    // 2. Golden SHA-256 Hash Regression Tests (Source Generator Output)
    // =========================================================================

    [Fact]
    public async Task UserRecordsWriteParquetAsyncMatchesGoldenHashSnappy()
    {
        var records = CreateDeterministicUserRecords(100);
        using var stream = new MemoryStream();
        await records.WriteParquetAsync(
            stream,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
            }
        );

        string actualHash = ComputeSha256(stream);
        Assert.Equal(
            "042c17fd3f6b782acf0eaf1d5b7fc82be239ad134695720f3921d72cd8a02518",
            actualHash
        );
    }

    [Fact]
    public async Task UserRecordsWriteParquetAsyncMatchesGoldenHashUncompressed()
    {
        var records = CreateDeterministicUserRecords(100);
        using var stream = new MemoryStream();
        await records.WriteParquetAsync(
            stream,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.None,
            }
        );

        string actualHash = ComputeSha256(stream);
        Assert.Equal(
            "5913c404a436b0c8502db61bbc2f201838ea13a5dc2c055718064129350b8b62",
            actualHash
        );
    }

    [Fact]
    public async Task UserRecordsWriteParquetBatchedAsyncMatchesGoldenHash()
    {
        var records = CreateDeterministicUserRecords(100);
        using var stream = new MemoryStream();
        await records.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 25 }
        );

        string actualHash = ComputeSha256(stream);
        Assert.Equal(
            "02738967a4061950a3881e82d2c3db63bdc1949dade0cbafead52347c2069af2",
            actualHash
        );
    }

    [Fact]
    public async Task NullableRecordsWriteParquetAsyncMatchesGoldenHash()
    {
        var records = CreateDeterministicNullableRecords(1000);
        using var stream = new MemoryStream();
        await records.WriteParquetAsync(stream);

        string actualHash = ComputeSha256(stream);
        Assert.Equal(
            "0dc304733a62f1ee46bedb8a2bca6afffd0598a3412cb14d0692ccb4a26f97a0",
            actualHash
        );
    }

    [Fact]
    public async Task GuidRecordsWriteParquetAsyncMatchesGoldenHash()
    {
        var records = CreateDeterministicGuidRecords(500);
        using var stream = new MemoryStream();
        await records.WriteParquetAsync(stream);

        string actualHash = ComputeSha256(stream);
        Assert.Equal(
            "78baabb5a2c8fc5334565d99574af8d550306c3ffa266cd1c882ecb88d91c3a2",
            actualHash
        );
    }

    [Fact]
    public async Task TpchLineItemSerializationMatchesGoldenHash()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet");
        await using var readStream = System.IO.File.OpenRead(filePath);
        var allRecords = await TpchLineItemRecordParquetExtensions.ReadParquetAsync(readStream);
        var slice = allRecords.Take(200).ToList();

        using var stream1 = new MemoryStream();
        await slice.WriteParquetAsync(
            stream1,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
            }
        );
        byte[] bytes1 = stream1.ToArray();

        using var stream2 = new MemoryStream();
        await slice.WriteParquetAsync(
            stream2,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
            }
        );
        byte[] bytes2 = stream2.ToArray();

        string hash1 = ComputeSha256(bytes1);
        string hash2 = ComputeSha256(bytes2);

        Assert.Equal(hash1, hash2);
        Assert.True(bytes1.AsSpan().SequenceEqual(bytes2.AsSpan()));
        Assert.Equal("5dd4a14e2b8ceaeed711a514e112f6cfcf3dffc36edf8698afb0cfc6db125b5b", hash1);
    }

    [Fact]
    public async Task AdultCensusSerializationMatchesGoldenHash()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "adult_census_income.parquet");
        await using var readStream = System.IO.File.OpenRead(filePath);
        var allRecords = await AdultCensusRecordParquetExtensions.ReadParquetAsync(readStream);
        var slice = allRecords.Take(200).ToList();

        using var stream1 = new MemoryStream();
        await slice.WriteParquetAsync(
            stream1,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
            }
        );
        byte[] bytes1 = stream1.ToArray();

        using var stream2 = new MemoryStream();
        await slice.WriteParquetAsync(
            stream2,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
            }
        );
        byte[] bytes2 = stream2.ToArray();

        string hash1 = ComputeSha256(bytes1);
        string hash2 = ComputeSha256(bytes2);

        Assert.Equal(hash1, hash2);
        Assert.True(bytes1.AsSpan().SequenceEqual(bytes2.AsSpan()));
        Assert.Equal("86dd82d1b8582da34878745ae5fb4314aba6105c9b1fdd6ca75598c5f4f0aff5", hash1);
    }

    [Fact]
    public async Task DiamondsSerializationMatchesGoldenHash()
    {
        string filePath = Path.Combine(BenchmarkDataRoot, "diamonds.parquet");
        await using var readStream = System.IO.File.OpenRead(filePath);
        var allRecords = await DiamondRecordParquetExtensions.ReadParquetAsync(readStream);
        var slice = allRecords.Take(200).ToList();

        using var stream1 = new MemoryStream();
        await slice.WriteParquetAsync(
            stream1,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
            }
        );
        byte[] bytes1 = stream1.ToArray();

        using var stream2 = new MemoryStream();
        await slice.WriteParquetAsync(
            stream2,
            options: new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
            }
        );
        byte[] bytes2 = stream2.ToArray();

        string hash1 = ComputeSha256(bytes1);
        string hash2 = ComputeSha256(bytes2);

        Assert.Equal(hash1, hash2);
        Assert.True(bytes1.AsSpan().SequenceEqual(bytes2.AsSpan()));
        Assert.Equal("c88ea3dacd504ac83ba8c09a4d2757c490ba8de6665f519d34998f1de0b7facf", hash1);
    }

    // =========================================================================
    // 3. Checked-In Test Datasets Cryptographic Hash Integrity Tests
    //
    // Trait-gated as Category=DatasetIntegrity. CI runs these against the
    // pristine LFS-hydrated checkout before compatibility datasets are generated
    // into a temporary directory. The main post-generation run filters them out
    // because the manifest describes the committed corpus, not generated output.
    // =========================================================================

    public static IEnumerable<object[]> CheckedInDatasetPaths =>
        LoadFixtureManifest().Fixtures.Select(fixture => new object[] { fixture.Path });

    [Fact]
    [Trait("Category", "DatasetIntegrity")]
    public void FixtureManifestCoversEveryCheckedInDataset()
    {
        FixtureManifest manifest = LoadFixtureManifest();
        string[] manifestPaths = manifest
            .Fixtures.Select(fixture => fixture.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] actualPaths = EnumerateDatasetPaths()
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(manifestPaths, actualPaths);
        Assert.All(
            manifest.Fixtures,
            fixture =>
            {
                Assert.False(string.IsNullOrWhiteSpace(fixture.Category));
                Assert.False(string.IsNullOrWhiteSpace(fixture.Producer));
                Assert.False(string.IsNullOrWhiteSpace(fixture.ProducerVersion));
                Assert.False(string.IsNullOrWhiteSpace(fixture.CreatedBy));
                Assert.True(
                    !string.IsNullOrWhiteSpace(fixture.GenerationScript)
                        || !string.IsNullOrWhiteSpace(fixture.SourceCommit),
                    $"Fixture '{fixture.Path}' has no provenance source."
                );
                Assert.False(string.IsNullOrWhiteSpace(fixture.FormatVersion));
                Assert.False(string.IsNullOrWhiteSpace(fixture.Compression));
                Assert.True(fixture.RowCount > 0);
                Assert.True(fixture.RowGroupCount > 0);
                Assert.True(fixture.ColumnCount > 0);
                Assert.False(string.IsNullOrWhiteSpace(fixture.Support));
                Assert.False(string.IsNullOrWhiteSpace(fixture.Sha256));
                Assert.Equal(64, fixture.Sha256.Length);
            }
        );
    }

    [Theory]
    [Trait("Category", "DatasetIntegrity")]
    [MemberData(nameof(CheckedInDatasetPaths))]
    public async Task CheckedInDatasetMatchesManifest(string relativePath)
    {
        FixtureManifest manifest = LoadFixtureManifest();
        FixtureEntry fixture = Assert.Single(
            manifest.Fixtures,
            candidate =>
                string.Equals(candidate.Path, relativePath, StringComparison.OrdinalIgnoreCase)
        );
        string fullPath = Path.Combine(SolutionRoot, relativePath);
        Assert.True(System.IO.File.Exists(fullPath), $"Dataset file does not exist: {fullPath}");

        var fileInfo = new FileInfo(fullPath);
        Assert.True(
            fileInfo.Length > 1024,
            $"File is smaller than 1KB ({fileInfo.Length} bytes), likely unhydrated LFS pointer: {fullPath}"
        );

        using var fs = System.IO.File.OpenRead(fullPath);
        string actualHash = ComputeSha256(fs);
        Assert.Equal(fixture.Sha256, actualHash);

        await using var metadataStream = System.IO.File.OpenRead(fullPath);
        await using var reader = await Parquet.ParquetReader.CreateAsync(metadataStream);
        Assert.Equal(fixture.CreatedBy, reader.Metadata?.CreatedBy);
        Assert.Equal(fixture.RowGroupCount, reader.RowGroups.Count);
        Assert.Equal(fixture.ColumnCount, reader.Schema.DataFields.Length);
        Assert.Equal(fixture.RowCount, reader.RowGroups.Sum(rowGroup => rowGroup.RowCount));
    }

    private static FixtureManifest LoadFixtureManifest()
    {
        string json = System.IO.File.ReadAllText(FixtureManifestPath);
        FixtureManifest? manifest = JsonSerializer.Deserialize<FixtureManifest>(
            json,
            FixtureManifestJsonOptions
        );
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.SchemaVersion);
        return manifest;
    }

    private static IEnumerable<string> EnumerateDatasetPaths()
    {
        foreach (
            string root in new[]
            {
                TestDataRoot,
                TestDataCSharpRoot,
                TestDataProducersRoot,
                BenchmarkDataRoot,
            }
        )
        {
            foreach (
                string path in Directory.EnumerateFiles(
                    root,
                    "*.parquet",
                    SearchOption.AllDirectories
                )
            )
            {
                yield return Path.GetRelativePath(SolutionRoot, path).Replace('\\', '/');
            }
        }
    }

    private sealed class FixtureManifest
    {
        public int SchemaVersion { get; set; }

        public List<FixtureEntry> Fixtures { get; set; } = new();
    }

    private sealed class FixtureEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Producer { get; set; } = string.Empty;
        public string ProducerVersion { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public string GenerationScript { get; set; } = string.Empty;
        public string SourceCommit { get; set; } = string.Empty;
        public string FormatVersion { get; set; } = string.Empty;
        public string Compression { get; set; } = string.Empty;
        public int RowCount { get; set; }
        public int RowGroupCount { get; set; }
        public int ColumnCount { get; set; }
        public string Support { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }
}
