using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record JsonAnnotatedModel
{
    [JsonPropertyName("custom_id")]
    [JsonPropertyOrder(1)]
    public int Id { get; init; }

    [JsonPropertyName("display_name")]
    [JsonPropertyOrder(2)]
    public string? Name { get; init; } = "";

    [JsonIgnore]
    public string InternalSecret { get; init; } = "hidden";

    [JsonPropertyOrder(3)]
    public double Score { get; init; }
}

[ParquetSerializable]
public partial record MixedAnnotationPrecedenceModel
{
    // [ParquetColumn] name takes precedence over [JsonPropertyName]
    [ParquetColumn("parquet_id_wins")]
    [JsonPropertyName("json_id_ignored")]
    public int Id { get; init; }

    // [JsonPropertyName] used when [ParquetColumn] only specifies Order
    [ParquetColumn(Order = 1)]
    [JsonPropertyName("json_description")]
    public string Description { get; init; } = "";

    // [ParquetColumn(Order)] takes precedence over [JsonPropertyOrder]
    [ParquetColumn(Order = 3)]
    [JsonPropertyOrder(1)]
    public double Metric { get; init; }

    // [JsonPropertyOrder] used when [ParquetColumn] does not specify Order
    [JsonPropertyOrder(2)]
    public string Category { get; init; } = "";
}

public sealed class JsonSerializationAttributesTests
{
    [Fact]
    public void JsonPropertyNameAndOrderSetSchemaColumnsCorrectly()
    {
        var fields = JsonAnnotatedModelParquetExtensions.Schema.DataFields;

        // Exactly 3 fields should be in the schema because InternalSecret is decorated with [JsonIgnore]
        Assert.Equal(3, fields.Length);

        // Fields should be ordered according to [JsonPropertyOrder] and named with [JsonPropertyName]
        Assert.Equal("custom_id", fields[0].Name);
        Assert.Equal("display_name", fields[1].Name);
        Assert.Equal("Score", fields[2].Name);
    }

    [Fact]
    public void MixedAnnotationPrecedenceHonorsParquetColumnFirst()
    {
        var fields = MixedAnnotationPrecedenceModelParquetExtensions.Schema.DataFields;

        Assert.Equal(4, fields.Length);

        // Order 1: Description (via [ParquetColumn(Order = 1)] and [JsonPropertyName("json_description")])
        Assert.Equal("json_description", fields[0].Name);

        // Order 2: Category (via [JsonPropertyOrder(2)])
        Assert.Equal("Category", fields[1].Name);

        // Order 3: Metric (via [ParquetColumn(Order = 3)] overriding [JsonPropertyOrder(1)])
        Assert.Equal("Metric", fields[2].Name);

        // Order unspecified (-1): placed last, name is "parquet_id_wins" overriding [JsonPropertyName]
        Assert.Equal("parquet_id_wins", fields[3].Name);
    }

    [Fact]
    public async Task JsonAnnotatedModelRoundtripsThroughSourceGenerator()
    {
        const int count = 500;
        var items = Enumerable.Range(1, count)
            .Select(i => new JsonAnnotatedModel
            {
                Id = i,
                Name = $"Item_{i}",
                Score = i * 1.5,
                InternalSecret = "sensitive_data",
            })
            .ToList();

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        // Read list
        ms.Position = 0;
        var readList = await JsonAnnotatedModelParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(count, readList.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(items[i].Id, readList[i].Id);
            Assert.Equal(items[i].Name, readList[i].Name);
            Assert.Equal(items[i].Score, readList[i].Score);
            // Ignored property should take default constructor value ("hidden"), not written "sensitive_data"
            Assert.Equal("hidden", readList[i].InternalSecret);
        }

        // Read array
        ms.Position = 0;
        var readArray = await JsonAnnotatedModelParquetExtensions.ReadParquetArrayAsync(ms);
        Assert.Equal(count, readArray.Length);
        Assert.Equal(items[0].Name, readArray[0].Name);

        // Read parallel
        byte[] bytes = ms.ToArray();
        var parallelList = await JsonAnnotatedModelParquetExtensions.ReadParquetParallelAsync(bytes);
        Assert.Equal(count, parallelList.Count);
        Assert.Equal(items[10].Score, parallelList[10].Score);

        // Read stream
        using var streamMs = new MemoryStream(bytes);
        int streamCount = 0;
        await foreach (var item in JsonAnnotatedModelParquetExtensions.ReadParquetStreamAsync(streamMs))
        {
            Assert.Equal(items[streamCount].Id, item.Id);
            streamCount++;
        }
        Assert.Equal(count, streamCount);
    }

    [Fact]
    public async Task JsonAnnotatedModelInteroperatesWithParquetNetReflectionSerializer()
    {
        // Parquet.Net's ParquetSerializer recognizes [JsonPropertyName], [JsonPropertyOrder], and [JsonIgnore]
        // This test proves bit-for-bit, column-for-column cross-compatibility.
        var data = new List<JsonAnnotatedModel>
        {
            new() { Id = 101, Name = "Alice", Score = 99.5, InternalSecret = "shh" },
            new() { Id = 102, Name = "Bob", Score = 88.0, InternalSecret = "secret" },
        };

        // 1. Write with Parquet.SourceGenerator, Read with ParquetSerializer reflection
        using var msGenerated = new MemoryStream();
        await data.WriteParquetAsync(msGenerated);
        msGenerated.Position = 0;

        var reflectionResult = await Parquet.Serialization.ParquetSerializer.DeserializeAsync<JsonAnnotatedModel>(msGenerated);
        Assert.Equal(2, reflectionResult.Data.Count);
        Assert.Equal(101, reflectionResult.Data[0].Id);
        Assert.Equal("Alice", reflectionResult.Data[0].Name);
        Assert.Equal(99.5, reflectionResult.Data[0].Score);
        Assert.Equal("hidden", reflectionResult.Data[0].InternalSecret);

        // 2. Write with ParquetSerializer reflection, Read with Parquet.SourceGenerator
        using var msReflection = new MemoryStream();
        await Parquet.Serialization.ParquetSerializer.SerializeAsync(data, msReflection);
        msReflection.Position = 0;

        var generatorResult = await JsonAnnotatedModelParquetExtensions.ReadParquetAsync(msReflection);
        Assert.Equal(2, generatorResult.Count);
        Assert.Equal(102, generatorResult[1].Id);
        Assert.Equal("Bob", generatorResult[1].Name);
        Assert.Equal(88.0, generatorResult[1].Score);
        Assert.Equal("hidden", generatorResult[1].InternalSecret);
    }
}
