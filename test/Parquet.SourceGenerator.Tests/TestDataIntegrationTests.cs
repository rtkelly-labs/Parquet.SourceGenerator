using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record TestUserRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("score")]
    public double Score { get; init; }

    [ParquetColumn("is_active")]
    public bool IsActive { get; init; }

    [ParquetColumn("created_at_ms")]
    public long CreatedAtMs { get; init; }
}

[ParquetSerializable]
public partial record TestNullableRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("nullable_int")]
    public int? NullableInt { get; init; }

    [ParquetColumn("nullable_double")]
    public double? NullableDouble { get; init; }

    [ParquetColumn("nullable_string")]
    public string? NullableString { get; init; }

    [ParquetColumn("nullable_bool")]
    public bool? NullableBool { get; init; }
}

[ParquetSerializable]
public partial record TestLargeFlatRecord
{
    [ParquetColumn("id")]
    public long Id { get; init; }

    [ParquetColumn("payload")]
    public string Payload { get; init; } = string.Empty;

    [ParquetColumn("val_a")]
    public int ValA { get; init; }

    [ParquetColumn("val_b")]
    public double ValB { get; init; }

    [ParquetColumn("is_valid")]
    public bool IsValid { get; init; }
}

public sealed class TestDataIntegrationTests
{
    private static readonly string TestDataRoot =
        Environment.GetEnvironmentVariable("PARQUET_TEST_DATA_ROOT")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
    private static readonly string TestDataCSharpRoot =
        Environment.GetEnvironmentVariable("PARQUET_TEST_DATA_CSHARP_ROOT")
        ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data_csharp")
        );

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [InlineData("v3")]
    public async Task ToArrayAsyncDeserializesEveryPrimitiveValueAsync(string version)
    {
        string filePath = FixturePath(version, "01_small_flat_primitives.parquet");
        using var stream = System.IO.File.OpenRead(filePath);
        TestUserRecord[] records = await TestUserRecordParquet.From(stream).ToArrayAsync();

        AssertPrimitiveRows(records);
    }

    [Theory]
    [InlineData("v3")]
    public async Task ToArrayAsyncDeserializesEveryNullableValueAsync(string version)
    {
        string filePath = FixturePath(version, "02_medium_nullable_types.parquet");
        using var stream = System.IO.File.OpenRead(filePath);
        TestNullableRecord[] records = await TestNullableRecordParquet.From(stream).ToArrayAsync();

        records.Length.ShouldBe(10_000);
        for (int i = 0; i < records.Length; i++)
        {
            var expected = new TestNullableRecord
            {
                Id = i,
                NullableInt = i % 5 == 0 ? null : i * 10,
                NullableDouble = i % 5 == 0 ? null : (i * 3.14159) % 1000.0,
                NullableString = i % 5 == 0 ? null : $"str_val_{i}",
                NullableBool = i % 5 == 0 ? null : i % 3 == 0,
            };
            records[i].ShouldBe(expected, $"{version}: row {i}");
        }
    }

    [Theory]
    [InlineData("v3")]
    public async Task ToArrayAsyncDeserializesEveryLargeScaleValueAsync(string version)
    {
        string filePath = FixturePath(version, "05_large_scale_flat.parquet");
        using var stream = System.IO.File.OpenRead(filePath);
        TestLargeFlatRecord[] records = await TestLargeFlatRecordParquet
            .From(stream)
            .ToArrayAsync();

        records.Length.ShouldBe(100_000);
        for (int i = 0; i < records.Length; i++)
        {
            var expected = new TestLargeFlatRecord
            {
                Id = i,
                Payload = $"payload_data_string_buffer_segment_{i % 500}",
                ValA = i * 7,
                ValB = i * 0.123456789,
                IsValid = i % 7 != 0,
            };
            records[i].ShouldBe(expected, $"{version}: row {i}");
        }
    }

    [Fact]
    public async Task FixtureComparisonRejectsAChangedInteriorValueAsync()
    {
        // Keep the row count and endpoints intact, as the old sampled assertions did.
        using var fixture = System.IO.File.OpenRead(
            FixturePath("v1", "01_small_flat_primitives.parquet")
        );
        TestUserRecord[] rows = await TestUserRecordParquet.From(fixture).ToArrayAsync();
        AssertPrimitiveRows(rows);
        rows[37] = rows[37] with { Score = rows[37].Score + 1 };

        using var changed = new MemoryStream();
        await rows.WriteParquetAsync(changed);
        changed.Position = 0;
        TestUserRecord[] actual = await TestUserRecordParquet.From(changed).ToArrayAsync();

        actual[37].Score.ShouldBe(rows[37].Score);
        ShouldAssertException exception = Should.Throw<ShouldAssertException>(() =>
            AssertPrimitiveRows(actual)
        );
        exception.Message.ShouldContain("row 37");
    }

    private static string FixturePath(string version, string fileName)
    {
        string root = version == "v3" ? TestDataCSharpRoot : TestDataRoot;
        string path = Path.Combine(root, version, fileName);
        System.IO.File.Exists(path).ShouldBeTrue($"File not found: {path}");
        return path;
    }

    private static void AssertPrimitiveRows(TestUserRecord[] records)
    {
        // Independent expectations follow the deterministic fixture specification, not the reader.
        records.Length.ShouldBe(100);
        for (int i = 0; i < records.Length; i++)
        {
            var expected = new TestUserRecord
            {
                Id = i,
                Name = $"user_{i}",
                Score = (i * 1.5) % 100.0,
                IsActive = i % 2 == 0,
                CreatedAtMs = 1_700_000_000_000L + (i * 1000L),
            };
            records[i].ShouldBe(expected, $"row {i}");
        }
    }
}
