using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Tests.Fixtures;
using Shouldly;
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
        fields.Length.ShouldBe(3);

        // Fields should be ordered according to [JsonPropertyOrder] and named with [JsonPropertyName]
        fields[0].Name.ShouldBe("custom_id");
        fields[1].Name.ShouldBe("display_name");
        fields[2].Name.ShouldBe("Score");
    }

    [Fact]
    public void MixedAnnotationPrecedenceHonorsParquetColumnFirst()
    {
        var fields = MixedAnnotationPrecedenceModelParquetExtensions.Schema.DataFields;

        fields.Length.ShouldBe(4);

        // Order 1: Description (via [ParquetColumn(Order = 1)] and [JsonPropertyName("json_description")])
        fields[0].Name.ShouldBe("json_description");

        // Order 2: Category (via [JsonPropertyOrder(2)])
        fields[1].Name.ShouldBe("Category");

        // Order 3: Metric (via [ParquetColumn(Order = 3)] overriding [JsonPropertyOrder(1)])
        fields[2].Name.ShouldBe("Metric");

        // Order unspecified (-1): placed last, name is "parquet_id_wins" overriding [JsonPropertyName]
        fields[3].Name.ShouldBe("parquet_id_wins");
    }

    [Fact]
    public async Task JsonAnnotatedModelRoundtripsThroughSourceGenerator()
    {
        const int count = 500;
        var items = TestFakers.CreateJsonAnnotatedModelFaker().Generate(count);

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        // Read list
        ms.Position = 0;
        var readList = await JsonAnnotatedModelParquetExtensions.ReadParquetAsync(ms);
        readList.Count.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            readList[i].Id.ShouldBe(items[i].Id);
            readList[i].Name.ShouldBe(items[i].Name);
            readList[i].Score.ShouldBe(items[i].Score);
            // Ignored property should take default constructor value ("hidden"), not written "sensitive_data"
            readList[i].InternalSecret.ShouldBe("hidden");
        }

        // Read array
        ms.Position = 0;
        var readArray = await JsonAnnotatedModelParquetExtensions.ReadParquetArrayAsync(ms);
        readArray.Length.ShouldBe(count);
        readArray[0].Name.ShouldBe(items[0].Name);

        // Read parallel
        byte[] bytes = ms.ToArray();
        var parallelList = await JsonAnnotatedModelParquetExtensions.ReadParquetParallelAsync(
            bytes
        );
        parallelList.Count.ShouldBe(count);
        parallelList[10].Score.ShouldBe(items[10].Score);

        // Read stream
        using var streamMs = new MemoryStream(bytes);
        int streamCount = 0;
        await foreach (
            var item in JsonAnnotatedModelParquetExtensions.ReadParquetStreamAsync(streamMs)
        )
        {
            item.Id.ShouldBe(items[streamCount].Id);
            streamCount++;
        }
        streamCount.ShouldBe(count);
    }

    [Fact]
    public async Task JsonAnnotatedModelInteroperatesWithParquetNetReflectionSerializer()
    {
        // Parquet.Net's ParquetSerializer recognizes [JsonPropertyName], [JsonPropertyOrder], and [JsonIgnore]
        // This test proves bit-for-bit, column-for-column cross-compatibility.
        var data = new List<JsonAnnotatedModel>
        {
            new()
            {
                Id = 101,
                Name = "Alice",
                Score = 99.5,
                InternalSecret = "shh",
            },
            new()
            {
                Id = 102,
                Name = "Bob",
                Score = 88.0,
                InternalSecret = "secret",
            },
        };

        // 1. Write with Parquet.SourceGenerator, Read with ParquetSerializer reflection
        using var msGenerated = new MemoryStream();
        await data.WriteParquetAsync(msGenerated);
        msGenerated.Position = 0;

        var reflectionResult =
            await Parquet.Serialization.ParquetSerializer.DeserializeAsync<JsonAnnotatedModel>(
                msGenerated
            );
        reflectionResult.Data.Count.ShouldBe(2);
        reflectionResult.Data[0].Id.ShouldBe(101);
        reflectionResult.Data[0].Name.ShouldBe("Alice");
        reflectionResult.Data[0].Score.ShouldBe(99.5);
        reflectionResult.Data[0].InternalSecret.ShouldBe("hidden");

        // 2. Write with ParquetSerializer reflection, Read with Parquet.SourceGenerator
        using var msReflection = new MemoryStream();
        await Parquet.Serialization.ParquetSerializer.SerializeAsync(data, msReflection);
        msReflection.Position = 0;

        var generatorResult = await JsonAnnotatedModelParquetExtensions.ReadParquetAsync(
            msReflection
        );
        generatorResult.Count.ShouldBe(2);
        generatorResult[1].Id.ShouldBe(102);
        generatorResult[1].Name.ShouldBe("Bob");
        generatorResult[1].Score.ShouldBe(88.0);
        generatorResult[1].InternalSecret.ShouldBe("hidden");
    }
}
