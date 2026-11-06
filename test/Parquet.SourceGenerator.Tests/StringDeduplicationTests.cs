using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record CategoricalRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("category")]
    public string Category { get; init; } = string.Empty;

    [ParquetColumn("tag")]
    public string? Tag { get; init; }
}

[ParquetSerializable]
public partial record ExplicitDeduplicateRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("department", Deduplicate = true)]
    public string Department { get; init; } = string.Empty;

    [ParquetColumn("notes")]
    public string Notes { get; init; } = string.Empty;
}

public sealed class StringDeduplicationTests
{
    private static async Task<byte[]> CreateCategoricalParquetBytesAsync(int count = 200)
    {
        var items = new List<CategoricalRecord>(count);
        for (int i = 0; i < count; i++)
        {
            items.Add(
                new CategoricalRecord
                {
                    Id = i,
                    Category = (i % 2 == 0) ? "Electronics" : "Clothing",
                    Tag = (i % 3 == 0) ? "OnSale" : (i % 3 == 1 ? "Clearance" : null),
                }
            );
        }

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> CreateExplicitDeduplicateParquetBytesAsync(int count = 200)
    {
        var items = new List<ExplicitDeduplicateRecord>(count);
        for (int i = 0; i < count; i++)
        {
            items.Add(
                new ExplicitDeduplicateRecord
                {
                    Id = i,
                    Department = (i % 2 == 0) ? "Engineering" : "Marketing",
                    Notes = "Standard Note",
                }
            );
        }

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task DeduplicateStringsWhenTrueCanonicalizesIdenticalStrings()
    {
        byte[] bytes = await CreateCategoricalParquetBytesAsync(200);

        var options = new ParquetSerializerOptions { DeduplicateStrings = true };
        var results = await CategoricalRecordParquetExtensions.ReadParquetArrayAsync(
            bytes,
            options
        );

        results.Length.ShouldBe(200);

        // Find items in the same category
        var electronics1 = results[0];
        var electronics2 = results[2];
        electronics1.Category.ShouldBe("Electronics");
        electronics2.Category.ShouldBe("Electronics");
        electronics1.Category.ShouldBeSameAs(
            electronics2.Category,
            "Identical strings should share reference equality when DeduplicateStrings = true"
        );

        var clothing1 = results[1];
        var clothing2 = results[3];
        clothing1.Category.ShouldBe("Clothing");
        clothing2.Category.ShouldBe("Clothing");
        clothing1.Category.ShouldBeSameAs(
            clothing2.Category,
            "Identical strings should share reference equality when DeduplicateStrings = true"
        );

        // Nullable strings
        var sale1 = results[0];
        var sale2 = results[6];
        sale1.Tag.ShouldBe("OnSale");
        sale2.Tag.ShouldBe("OnSale");
        sale1.Tag.ShouldBeSameAs(
            sale2.Tag,
            "Nullable identical strings should share reference equality when DeduplicateStrings = true"
        );
    }

    [Fact]
    public async Task DeduplicateStringsWhenFalseAllocatesIndependentStrings()
    {
        byte[] bytes = await CreateCategoricalParquetBytesAsync(200);

        var options = new ParquetSerializerOptions { DeduplicateStrings = false };
        var results = await CategoricalRecordParquetExtensions.ReadParquetArrayAsync(
            bytes,
            options
        );

        results.Length.ShouldBe(200);

        var electronics1 = results[0];
        var electronics2 = results[2];
        electronics2.Category.ShouldBe(electronics1.Category);
        electronics2.Category.ShouldNotBeSameAs(
            electronics1.Category,
            "Without deduplication, Parquet.Net emits distinct allocated string instances"
        );
    }

    [Fact]
    public async Task ParquetColumnAttributeDeduplicateTrueDeduplicatesEvenWhenGlobalOptionIsFalse()
    {
        byte[] bytes = await CreateExplicitDeduplicateParquetBytesAsync(200);

        // Global DeduplicateStrings = false
        var options = new ParquetSerializerOptions { DeduplicateStrings = false };
        var results = await ExplicitDeduplicateRecordParquetExtensions.ReadParquetArrayAsync(
            bytes,
            options
        );

        results.Length.ShouldBe(200);

        // Department has [ParquetColumn(Deduplicate = true)]
        var dept1 = results[0];
        var dept2 = results[2];
        dept1.Department.ShouldBe("Engineering");
        dept2.Department.ShouldBe("Engineering");
        dept1.Department.ShouldBeSameAs(
            dept2.Department,
            "Column decorated with [ParquetColumn(Deduplicate = true)] MUST be deduplicated even if global option is false"
        );

        // Notes does NOT have Deduplicate = true, so with global option = false, strings remain separate instances
        dept2.Notes.ShouldBe(dept1.Notes);
        dept2.Notes.ShouldNotBeSameAs(
            dept1.Notes,
            "Undecorated column should NOT be deduplicated when global option is false"
        );
    }

    [Fact]
    public async Task DeduplicateStringsWorksWithStreamingAsyncEnumerable()
    {
        byte[] bytes = await CreateCategoricalParquetBytesAsync(100);

        using var stream = new MemoryStream(bytes);
        var options = new ParquetSerializerOptions { DeduplicateStrings = true };

        var items = new List<CategoricalRecord>();
        await foreach (
            var item in CategoricalRecordParquetExtensions.ReadParquetStreamAsync(stream, options)
        )
        {
            items.Add(item);
        }

        items.Count.ShouldBe(100);
        items[0]
            .Category.ShouldBeSameAs(
                items[2].Category,
                "Streaming reader should deduplicate strings across row group items"
            );
    }

    [Fact]
    public async Task DeduplicateStringsWorksWithParallelArrayReader()
    {
        byte[] bytes = await CreateCategoricalParquetBytesAsync(100);

        var options = new ParquetSerializerOptions
        {
            DeduplicateStrings = true,
            MaxDegreeOfParallelism = 2,
        };
        var results = await CategoricalRecordParquetExtensions.ReadParquetParallelArrayAsync(
            bytes,
            options
        );

        results.Length.ShouldBe(100);
        results[0]
            .Category.ShouldBeSameAs(
                results[2].Category,
                "Parallel reader should deduplicate strings within workers"
            );
    }
}
