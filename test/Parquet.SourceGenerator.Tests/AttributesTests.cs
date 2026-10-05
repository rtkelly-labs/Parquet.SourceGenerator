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
        options.RowGroupSize = 1;
        options.ColumnEncodingHints["id"] = ParquetColumnEncoding.DeltaBinaryPacked;
        options2.RowGroupSize.ShouldBe(50_000);
        options2.ColumnEncodingHints.ShouldBeEmpty();
    }
}
