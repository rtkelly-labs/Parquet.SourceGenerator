using System;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public sealed class AttributesTests
{
    [Fact]
    public void ParquetSerializerOptionsCarriesCorrectDefaults()
    {
        var options = ParquetSerializerOptions.Default;

        options.ShouldNotBeNull();
        options.RowGroupSize.ShouldBe(50_000);
        options.MaxDegreeOfParallelism.ShouldBe(-1);
        options.CompressionMethod.ShouldBe(ParquetCompressionMethod.Snappy);
        options.CompressionLevel.ShouldBeNull();
        options.DeduplicateStrings.ShouldBeFalse();
        options.DictionaryEncodingThreshold.ShouldBeNull();
        options.DictionaryEncodingSampleSize.ShouldBeNull();
        options.ColumnEncodingHints.ShouldNotBeNull();
        options.ColumnEncodingHints.ShouldBeEmpty();

        // Default returns a fresh instance each time to prevent accidental mutation of shared state
        var options2 = ParquetSerializerOptions.Default;
        options.ShouldNotBeSameAs(options2);
    }

    [Fact]
    public void ParquetSerializerOptionsCanBeMutated()
    {
        var options = new ParquetSerializerOptions
        {
            RowGroupSize = 10_000,
            MaxDegreeOfParallelism = 4,
            CompressionMethod = ParquetCompressionMethod.Zstd,
            CompressionLevel = ParquetCompressionLevel.SmallestSize,
            DeduplicateStrings = true,
            DictionaryEncodingThreshold = 0.5,
            DictionaryEncodingSampleSize = 500,
            ColumnEncodingHints = { ["test_col"] = ParquetColumnEncoding.DeltaBinaryPacked },
        };

        options.RowGroupSize.ShouldBe(10_000);
        options.MaxDegreeOfParallelism.ShouldBe(4);
        options.CompressionMethod.ShouldBe(ParquetCompressionMethod.Zstd);
        options.CompressionLevel.ShouldBe(ParquetCompressionLevel.SmallestSize);
        options.DeduplicateStrings.ShouldBeTrue();
        options.DictionaryEncodingThreshold.ShouldBe(0.5);
        options.DictionaryEncodingSampleSize.ShouldBe(500);
        options.ColumnEncodingHints.Count.ShouldBe(1);
        options.ColumnEncodingHints["test_col"].ShouldBe(ParquetColumnEncoding.DeltaBinaryPacked);
    }

    [Theory]
    [InlineData(ParquetColumnEncoding.Default)]
    [InlineData(ParquetColumnEncoding.Dictionary)]
    [InlineData(ParquetColumnEncoding.DeltaBinaryPacked)]
    [InlineData(ParquetColumnEncoding.ByteSplitStream)]
    public void ParquetColumnEncodingEnumValuesAreValid(ParquetColumnEncoding encoding)
    {
        Enum.IsDefined(encoding).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ParquetCompressionMethod.None)]
    [InlineData(ParquetCompressionMethod.Snappy)]
    [InlineData(ParquetCompressionMethod.Gzip)]
    [InlineData(ParquetCompressionMethod.Lz4)]
    [InlineData(ParquetCompressionMethod.Brotli)]
    [InlineData(ParquetCompressionMethod.Zstd)]
    public void ParquetCompressionMethodEnumValuesAreValid(ParquetCompressionMethod method)
    {
        Enum.IsDefined(method).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ParquetCompressionLevel.Optimal)]
    [InlineData(ParquetCompressionLevel.Fastest)]
    [InlineData(ParquetCompressionLevel.NoCompression)]
    [InlineData(ParquetCompressionLevel.SmallestSize)]
    public void ParquetCompressionLevelEnumValuesAreValid(ParquetCompressionLevel level)
    {
        Enum.IsDefined(level).ShouldBeTrue();
    }

    [Fact]
    public void ParquetColumnAttributeStoresProperties()
    {
        var attr = new ParquetColumnAttribute("test_col")
        {
            Order = 42,
            Encoding = ParquetColumnEncoding.DeltaBinaryPacked,
        };

        attr.Name.ShouldBe("test_col");
        attr.Order.ShouldBe(42);
        attr.Encoding.ShouldBe(ParquetColumnEncoding.DeltaBinaryPacked);
    }

    [Fact]
    public void ParquetDecimalAttributeStoresProperties()
    {
        var attr = new ParquetDecimalAttribute(18, 4);

        attr.Precision.ShouldBe(18);
        attr.Scale.ShouldBe(4);
    }

    [Fact]
    public void ParquetTimestampAttributeStoresProperties()
    {
        var attrMs = new ParquetTimestampAttribute(ParquetTimestampUnit.Milliseconds);
        attrMs.Unit.ShouldBe(ParquetTimestampUnit.Milliseconds);

        var attrUs = new ParquetTimestampAttribute(ParquetTimestampUnit.Microseconds);
        attrUs.Unit.ShouldBe(ParquetTimestampUnit.Microseconds);
    }

    [Fact]
    public void ParquetIgnoreAttributeCanBeInstantiated()
    {
        var attr = new ParquetIgnoreAttribute();
        attr.ShouldNotBeNull();
    }

    [Fact]
    public void ParquetSerializableAttributeCanBeInstantiated()
    {
        var defaultAttr = new ParquetSerializableAttribute();
        defaultAttr.ShouldNotBeNull();
    }
}
