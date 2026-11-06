using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Serialization;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record BaselineRecord
{
    [ParquetColumn("Id")]
    public int Id { get; init; }

    [ParquetColumn("Name")]
    public string? Name { get; init; }

    [ParquetColumn("Score")]
    public double Score { get; init; }

    [ParquetColumn("IsActive")]
    public bool IsActive { get; init; }
}

public class BaselineDifferentialTests
{
    [Fact]
    public async Task SourceGeneratorCanReadDataWrittenByParquetSerializerReflection()
    {
        // 1. Arrange baseline records
        var expected = Enumerable
            .Range(1, 100)
            .Select(i => new BaselineRecord
            {
                Id = i,
                Name = $"user_{i}",
                Score = i * 2.5,
                IsActive = i % 2 == 0,
            })
            .ToList();

        // 2. Serialize using v6 ParquetSerializer (reflection / Dremel expression-tree implementation)
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(expected, stream);

        // 3. Deserialize using Parquet.SourceGenerator stream reader (AOT-safe, source generated)
        stream.Position = 0;
        List<BaselineRecord> actual = await BaselineRecordParquet.From(stream).ToListAsync();

        // 4. Assert symmetric identity
        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Id.ShouldBe(expected[i].Id);
            actual[i].Name.ShouldBe(expected[i].Name);
            actual[i].Score.ShouldBe(expected[i].Score);
            actual[i].IsActive.ShouldBe(expected[i].IsActive);
        }
    }

    [Fact]
    public async Task ParquetSerializerReflectionCanReadDataWrittenBySourceGenerator()
    {
        // 1. Arrange baseline records
        var expected = Enumerable
            .Range(1, 100)
            .Select(i => new BaselineRecord
            {
                Id = i,
                Name = $"user_{i}",
                Score = i * 2.5,
                IsActive = i % 2 == 0,
            })
            .ToList();

        // 2. Serialize using Parquet.SourceGenerator stream writer (AOT-safe, source generated)
        using var stream = new MemoryStream();
        await expected.WriteParquetAsync(stream);

        // 3. Deserialize using v6 ParquetSerializer (reflection / Dremel expression-tree implementation)
        stream.Position = 0;
        var result = await ParquetSerializer.DeserializeAsync<BaselineRecord>(stream);
        IList<BaselineRecord> actual = result.Data;

        // 4. Assert symmetric identity
        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Id.ShouldBe(expected[i].Id);
            actual[i].Name.ShouldBe(expected[i].Name);
            actual[i].Score.ShouldBe(expected[i].Score);
            actual[i].IsActive.ShouldBe(expected[i].IsActive);
        }
    }
}
