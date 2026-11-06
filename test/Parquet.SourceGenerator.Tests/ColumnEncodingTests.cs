using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet;
using Parquet.Meta;
using Parquet.SourceGenerator;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record DeltaEncodedRecord
{
    [ParquetColumn("id", Encoding = ParquetColumnEncoding.DeltaBinaryPacked)]
    public long Id { get; init; }

    [ParquetColumn("seq", Encoding = ParquetColumnEncoding.DeltaBinaryPacked)]
    public int Seq { get; init; }
}

[ParquetSerializable]
public partial record DictionaryEncodedRecord
{
    [ParquetColumn("category", Encoding = ParquetColumnEncoding.Dictionary)]
    public string Category { get; init; } = string.Empty;

    [ParquetColumn("value")]
    public int Value { get; init; }
}

[ParquetSerializable]
public partial record ByteSplitRecord
{
    [ParquetColumn("measurement", Encoding = ParquetColumnEncoding.ByteSplitStream)]
    public double Measurement { get; init; }

    [ParquetColumn("ratio", Encoding = ParquetColumnEncoding.ByteSplitStream)]
    public float Ratio { get; init; }
}

[ParquetSerializable]
public partial record UnannotatedEncodingRecord
{
    [ParquetColumn("id")]
    public long Id { get; init; }

    [ParquetColumn("metric")]
    public double Metric { get; init; }
}

public sealed class ColumnEncodingTests
{
    [Fact]
    public async Task DeltaBinaryPackedEncodingAppliedAndRoundtrips()
    {
        var written = Enumerable
            .Range(1, 1_000)
            .Select(i => new DeltaEncodedRecord { Id = 10_000_000L + i, Seq = i * 2 })
            .ToList();

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        await using (var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true))
        {
            reader.Metadata.ShouldNotBeNull();
            reader.Metadata.RowGroups.ShouldNotBeEmpty();
            var rg = reader.Metadata.RowGroups[0];

            var col0 = rg.Columns[0];
            col0.MetaData.ShouldNotBeNull();
            col0.MetaData.Encodings.ShouldContain(
                global::Parquet.Meta.Encoding.DELTA_BINARY_PACKED
            );

            var col1 = rg.Columns[1];
            col1.MetaData.ShouldNotBeNull();
            col1.MetaData.Encodings.ShouldContain(
                global::Parquet.Meta.Encoding.DELTA_BINARY_PACKED
            );
        }

        stream.Position = 0;
        List<DeltaEncodedRecord> read = await DeltaEncodedRecordParquetExtensions.ReadParquetAsync(
            stream
        );
        read.Count.ShouldBe(written.Count);
        for (int i = 0; i < written.Count; i++)
        {
            read[i].Id.ShouldBe(written[i].Id);
            read[i].Seq.ShouldBe(written[i].Seq);
        }
    }

    [Fact]
    public async Task DictionaryEncodingAppliedAndRoundtrips()
    {
        string[] categories = ["Alpha", "Beta", "Gamma", "Delta", "Epsilon"];
        var written = Enumerable
            .Range(0, 1_000)
            .Select(i => new DictionaryEncodedRecord
            {
                Category = categories[i % categories.Length],
                Value = i,
            })
            .ToList();

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        await using (var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true))
        {
            reader.Metadata.ShouldNotBeNull();
            reader.Metadata.RowGroups.ShouldNotBeEmpty();
            var rg = reader.Metadata.RowGroups[0];

            var col0 = rg.Columns[0];
            col0.MetaData.ShouldNotBeNull();
            bool hasDictionary =
                col0.MetaData.Encodings.Contains(global::Parquet.Meta.Encoding.PLAIN_DICTIONARY)
                || col0.MetaData.Encodings.Contains(global::Parquet.Meta.Encoding.RLE_DICTIONARY);
            hasDictionary.ShouldBeTrue(
                $"Expected dictionary encoding in column 0 encodings: {string.Join(", ", col0.MetaData.Encodings)}"
            );
        }

        stream.Position = 0;
        List<DictionaryEncodedRecord> read =
            await DictionaryEncodedRecordParquetExtensions.ReadParquetAsync(stream);
        read.Count.ShouldBe(written.Count);
        for (int i = 0; i < written.Count; i++)
        {
            read[i].Category.ShouldBe(written[i].Category);
            read[i].Value.ShouldBe(written[i].Value);
        }
    }

    [Fact]
    public async Task ByteSplitStreamEncodingAppliedAndRoundtrips()
    {
        var written = Enumerable
            .Range(0, 1_000)
            .Select(i => new ByteSplitRecord
            {
                Measurement = 3.1415926535 * (i + 1),
                Ratio = 0.12345f * (i + 1),
            })
            .ToList();

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        await using (var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true))
        {
            reader.Metadata.ShouldNotBeNull();
            reader.Metadata.RowGroups.ShouldNotBeEmpty();
            var rg = reader.Metadata.RowGroups[0];

            var col0 = rg.Columns[0];
            col0.MetaData.ShouldNotBeNull();
            col0.MetaData.Encodings.ShouldContain(global::Parquet.Meta.Encoding.BYTE_STREAM_SPLIT);

            var col1 = rg.Columns[1];
            col1.MetaData.ShouldNotBeNull();
            col1.MetaData.Encodings.ShouldContain(global::Parquet.Meta.Encoding.BYTE_STREAM_SPLIT);
        }

        stream.Position = 0;
        List<ByteSplitRecord> read = await ByteSplitRecordParquetExtensions.ReadParquetAsync(
            stream
        );
        read.Count.ShouldBe(written.Count);
        for (int i = 0; i < written.Count; i++)
        {
            read[i].Measurement.ShouldBe(written[i].Measurement);
            read[i].Ratio.ShouldBe(written[i].Ratio);
        }
    }

    [Fact]
    public async Task RuntimeOptionsColumnEncodingHintsOverridesDefault()
    {
        var written = Enumerable
            .Range(1, 500)
            .Select(i => new UnannotatedEncodingRecord { Id = i * 100L, Metric = i * 1.5 })
            .ToList();

        var options = new ParquetSerializerOptions
        {
            ColumnEncodingHints =
            {
                ["id"] = ParquetColumnEncoding.DeltaBinaryPacked,
                ["metric"] = ParquetColumnEncoding.ByteSplitStream,
            },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream, options);
        stream.Position = 0;

        await using (var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true))
        {
            reader.Metadata.ShouldNotBeNull();
            reader.Metadata.RowGroups.ShouldNotBeEmpty();
            var rg = reader.Metadata.RowGroups[0];

            var col0 = rg.Columns[0];
            col0.MetaData.ShouldNotBeNull();
            col0.MetaData.Encodings.ShouldContain(
                global::Parquet.Meta.Encoding.DELTA_BINARY_PACKED
            );

            var col1 = rg.Columns[1];
            col1.MetaData.ShouldNotBeNull();
            col1.MetaData.Encodings.ShouldContain(global::Parquet.Meta.Encoding.BYTE_STREAM_SPLIT);
        }

        stream.Position = 0;
        List<UnannotatedEncodingRecord> read =
            await UnannotatedEncodingRecordParquetExtensions.ReadParquetAsync(stream);
        read.Count.ShouldBe(written.Count);
        for (int i = 0; i < written.Count; i++)
        {
            read[i].Id.ShouldBe(written[i].Id);
            read[i].Metric.ShouldBe(written[i].Metric);
        }
    }

    [Fact]
    public async Task RuntimeOptionsOverridesCompileTimeHint()
    {
        var written = Enumerable
            .Range(1, 500)
            .Select(i => new DeltaEncodedRecord { Id = i, Seq = i * 3 })
            .ToList();

        // DeltaEncodedRecord has DeltaBinaryPacked on "id" at compile time.
        // We override "id" at runtime to Default.
        var options = new ParquetSerializerOptions
        {
            ColumnEncodingHints = { ["id"] = ParquetColumnEncoding.Default },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream, options);
        stream.Position = 0;

        await using (var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true))
        {
            reader.Metadata.ShouldNotBeNull();
            reader.Metadata.RowGroups.ShouldNotBeEmpty();
            var rg = reader.Metadata.RowGroups[0];

            var col0 = rg.Columns[0];
            col0.MetaData.ShouldNotBeNull();
            col0.MetaData.Encodings.ShouldNotContain(
                global::Parquet.Meta.Encoding.DELTA_BINARY_PACKED
            );

            // "seq" was not overridden, so compile-time DeltaBinaryPacked remains
            var col1 = rg.Columns[1];
            col1.MetaData.ShouldNotBeNull();
            col1.MetaData.Encodings.ShouldContain(
                global::Parquet.Meta.Encoding.DELTA_BINARY_PACKED
            );
        }

        stream.Position = 0;
        List<DeltaEncodedRecord> read = await DeltaEncodedRecordParquetExtensions.ReadParquetAsync(
            stream
        );
        read.Count.ShouldBe(written.Count);
    }

    [Fact]
    public async Task DictionaryEncodingThresholdAndSampleSizeOptionsRoundtrip()
    {
        var written = Enumerable
            .Range(1, 200)
            .Select(i => new DictionaryEncodedRecord
            {
                Category = i % 2 == 0 ? "Even" : "Odd",
                Value = i,
            })
            .ToList();

        var options = new ParquetSerializerOptions
        {
            DictionaryEncodingThreshold = 0.9,
            DictionaryEncodingSampleSize = 100,
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream, options);
        stream.Position = 0;

        List<DictionaryEncodedRecord> read =
            await DictionaryEncodedRecordParquetExtensions.ReadParquetAsync(stream);
        read.Count.ShouldBe(written.Count);
    }

    [Fact]
    public void TargetParserExtractsEncodingNamedArgumentFromAttribute()
    {
        string source = """
            using Parquet.SourceGenerator;

            [ParquetSerializable]
            public partial class ParserEncodingTestClass
            {
                [ParquetColumn("num", Encoding = ParquetColumnEncoding.DeltaBinaryPacked)]
                public long Num { get; init; }

                [ParquetColumn("str", Encoding = ParquetColumnEncoding.Dictionary)]
                public string Str { get; init; } = "";

                [ParquetColumn("flt", Encoding = ParquetColumnEncoding.ByteSplitStream)]
                public float Flt { get; init; }
            }
            """;

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(ParquetSerializableAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        };

        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestCompilation",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        var generator = new ParquetIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out var diagnostics
        );

        diagnostics.ShouldBeEmpty();
        var generatedTrees = outputCompilation.SyntaxTrees.ToList();
        var generatedSource = generatedTrees
            .First(t => t.FilePath.Contains("ParserEncodingTestClass"))
            .ToString();

        generatedSource.ShouldContain(
            "formatOptions.ColumnEncodingHints[\"num\"] = global::Parquet.EncodingHint.DeltaBinaryPacked;"
        );
        generatedSource.ShouldContain(
            "formatOptions.ColumnEncodingHints[\"str\"] = global::Parquet.EncodingHint.Dictionary;"
        );
        generatedSource.ShouldContain(
            "formatOptions.ColumnEncodingHints[\"flt\"] = global::Parquet.EncodingHint.ByteSplitStream;"
        );
    }
}
