using System.Collections.Generic;
using System.Data.SqlTypes;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;
using Parquet;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>Default decimal mapping: Decimal128(38, 18), wider than System.Decimal can hold.</summary>
[ParquetSerializable]
public partial record ArrowWideDecimalRow
{
    [ParquetColumn("value")]
    public decimal Value { get; init; }
}

/// <summary>
/// The Arrow bridge must never round a Decimal128 value. Apache.Arrow's
/// <c>Decimal128Array.GetValue</c> silently rounds a value with more significant digits than
/// System.Decimal's 96-bit mantissa holds (found while building Arrow.SourceGenerator); the bridge
/// used it, so a 38-digit value reached the Parquet file rounded, with no error.
/// </summary>
public sealed class ArrowDecimalExactnessTests
{
    private static readonly Decimal128Type Wide = new(38, 18);

    private static RecordBatch Batch(Decimal128Array values) =>
        new(
            new Apache.Arrow.Schema.Builder()
                .Field(f => f.Name("value").DataType(Wide).Nullable(false))
                .Build(),
            new IArrowArray[] { values },
            values.Length
        );

    private static async Task<List<ArrowWideDecimalRow>> RoundTripAsync(RecordBatch batch)
    {
        using var stream = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(
                ArrowWideDecimalRowParquetExtensions.Schema,
                stream
            )
        )
        {
            await ArrowWideDecimalRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch);
        }

        stream.Position = 0;
        return await ArrowWideDecimalRowParquet.From(stream).ToListAsync();
    }

    [Fact]
    public async Task ValueWiderThanSystemDecimalIsRejectedNotRounded()
    {
        var builder = new Decimal128Array.Builder(Wide);
        builder.Append(SqlDecimal.Parse("99999999999999999999.999999999999999999"));
        using RecordBatch batch = Batch(builder.Build());

        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(async () =>
            await RoundTripAsync(batch)
        );

        error.Message.ShouldContain("value", Case.Sensitive);
        error.Message.ShouldContain("System.Decimal", Case.Sensitive);
    }

    /// <summary>
    /// Regression: an unscaled integer wider than 96 bits can still be exact once its trailing
    /// decimal zeros are stripped. At Decimal128(38, 18), 10^19 is stored as 10^37.
    /// </summary>
    [Theory]
    [InlineData("10000000000000000000.000000000000000000", "10000000000000000000")]
    [InlineData("-12345678901234567890.000000000000000000", "-12345678901234567890")]
    [InlineData("12345678901234567890.123400000000000000", "12345678901234567890.1234")]
    public async Task WideValueWithTrailingZerosIsAcceptedExactly(string stored, string expected)
    {
        var builder = new Decimal128Array.Builder(Wide);
        builder.Append(SqlDecimal.Parse(stored));
        using RecordBatch batch = Batch(builder.Build());

        List<ArrowWideDecimalRow> rows = await RoundTripAsync(batch);

        rows.Single()
            .Value.ShouldBe(
                decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)
            );
    }

    [Fact]
    public async Task ValueStillTooWideAfterStrippingZerosIsRejected()
    {
        // 30 significant digits once the trailing zeros go: one more than System.Decimal holds.
        var builder = new Decimal128Array.Builder(Wide);
        builder.Append(SqlDecimal.Parse("12345678901234567890.123456789100000000"));
        using RecordBatch batch = Batch(builder.Build());

        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(async () =>
            await RoundTripAsync(batch)
        );

        error.Message.ShouldContain("System.Decimal", Case.Sensitive);
    }

    [Fact]
    public async Task ValuesAtTheSystemDecimalLimitsRoundTripExactly()
    {
        decimal max = decimal.MaxValue / 1_000_000_000_000_000_000m;
        decimal min = decimal.MinValue / 1_000_000_000_000_000_000m;
        decimal tiny = 0.000000000000000001m;
        var builder = new Decimal128Array.Builder(Wide)
            .Append(max)
            .Append(min)
            .Append(-tiny)
            .Append(0m);
        using RecordBatch batch = Batch(builder.Build());

        List<ArrowWideDecimalRow> rows = await RoundTripAsync(batch);

        rows.Select(r => r.Value).ShouldBe(new[] { max, min, -tiny, 0m });
    }
}
