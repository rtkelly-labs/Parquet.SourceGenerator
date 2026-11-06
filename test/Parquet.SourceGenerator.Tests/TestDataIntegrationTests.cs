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

    [Fact]
    public async Task ToListAsyncDeserializesPyArrowV1Dataset()
    {
        string filePath = Path.Combine(TestDataRoot, "v1", "01_small_flat_primitives.parquet");
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        using var stream = System.IO.File.OpenRead(filePath);
        var records = await TestUserRecordParquet.From(stream).ToListAsync();

        records.Count.ShouldBe(100);
        records[0].Id.ShouldBe(0);
        records[0].Name.ShouldBe("user_0");
        records[0].Score.ShouldBe(0.0);
        records[0].IsActive.ShouldBeTrue();
        records[0].CreatedAtMs.ShouldBe(1700000000000L);

        records[99].Id.ShouldBe(99);
        records[99].Name.ShouldBe("user_99");
        records[99].IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task ToListAsyncDeserializesPyArrowV2Dataset()
    {
        string filePath = Path.Combine(TestDataRoot, "v2", "01_small_flat_primitives.parquet");
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        using var stream = System.IO.File.OpenRead(filePath);
        var records = await TestUserRecordParquet.From(stream).ToListAsync();

        records.Count.ShouldBe(100);
        records[50].Id.ShouldBe(50);
        records[50].Name.ShouldBe("user_50");
        records[50].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task ToListAsyncDeserializesCSharpV3Dataset()
    {
        string filePath = Path.Combine(
            TestDataCSharpRoot,
            "v3",
            "01_small_flat_primitives.parquet"
        );
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        using var stream = System.IO.File.OpenRead(filePath);
        var records = await TestUserRecordParquet.From(stream).ToListAsync();

        records.Count.ShouldBe(100);
        records[10].Id.ShouldBe(10);
        records[10].Name.ShouldBe("user_10");
        records[10].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task ToListAsyncDeserializesNullableDataset()
    {
        string filePath = Path.Combine(
            TestDataCSharpRoot,
            "v3",
            "02_medium_nullable_types.parquet"
        );
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        using var stream = System.IO.File.OpenRead(filePath);
        var records = await TestNullableRecordParquet.From(stream).ToListAsync();

        records.Count.ShouldBe(10000);
        records[0].NullableInt.ShouldBeNull();
        records[0].NullableString.ShouldBeNull();

        records[1].NullableInt.ShouldBe(10);
        records[1].NullableString.ShouldBe("str_val_1");
    }

    [Fact]
    public async Task ToListAsyncDeserializesLargeScaleDataset()
    {
        string filePath = Path.Combine(TestDataCSharpRoot, "v3", "05_large_scale_flat.parquet");
        System.IO.File.Exists(filePath).ShouldBeTrue($"File not found: {filePath}");

        using var stream = System.IO.File.OpenRead(filePath);
        var records = await TestLargeFlatRecordParquet.From(stream).ToListAsync();

        records.Count.ShouldBe(100000);
        records[0].Id.ShouldBe(0L);
        records[99999].Id.ShouldBe(99999L);
        records[99999].ValA.ShouldBe(699993);
    }
}
