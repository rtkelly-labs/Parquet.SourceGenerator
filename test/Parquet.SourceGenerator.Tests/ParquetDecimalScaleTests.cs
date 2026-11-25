using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Parquet.SourceGenerator.Tests.PropertyBased;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The arithmetic behind <see cref="ParquetDecimalScale"/>, on every runtime. On .NET 10 and
/// earlier Parquet.Net's factor is already exact and the public entry points do nothing, so these
/// drive the internal entry point with the factor .NET 11 produces: the <c>double</c>'s exact
/// binary value rounded to 28 decimal places (<see cref="Net11Factor"/>, pinned to values measured
/// on the .NET 11 RC3 runtime). The end-to-end cases are in <see cref="DecimalScaleRoundTripTests"/>.
/// </summary>
public sealed class ParquetDecimalScaleTests
{
    private static readonly decimal[] NamedUnscaled =
    [
        0m,
        1m,
        7m,
        1_999m,
        1_900m,
        999_999_999m,
        int.MaxValue,
        (decimal)int.MinValue,
        9_007_199_254_740_992m, // 2^53
        9_007_199_254_740_993m,
        123_456_789_012_345_678m,
        999_999_999_999_999_999m,
        long.MaxValue,
        long.MinValue,
    ];

    private static readonly int[] Scales = [1, 2, 3, 4, 5, 6, 8, 9, 10, 12, 14, 15, 16, 17, 18];

    public static TheoryData<int, string> MatrixCases
    {
        get
        {
            var data = new TheoryData<int, string>();
            foreach (int scale in Scales)
            {
                foreach (decimal unscaled in NamedUnscaled)
                {
                    data.Add(scale, unscaled.ToString(CultureInfo.InvariantCulture));
                    if (unscaled != 0m && unscaled != long.MinValue)
                    {
                        data.Add(scale, (-unscaled).ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(MatrixCases))]
    public void NoisyDecodeIsRestoredToTheExactValueAndScale(int scale, string unscaledText)
    {
        decimal unscaled = decimal.Parse(unscaledText, CultureInfo.InvariantCulture);
        decimal[] values = [Decode(unscaled, Net11Factor(scale))];

        ParquetDecimalScale.Restore(values, scale, Net11Factor(scale));

        decimal expected = Exact(unscaled, scale);
        values[0].ShouldBe(expected);
        // Parquet.Net on earlier runtimes returns the value at the column's scale (19.99, not
        // 19.990); the restored value keeps that representation too.
        values[0].Scale.ShouldBe(expected.Scale);
    }

    public static TheoryData<long> Seeds
    {
        get
        {
            var data = new TheoryData<long>();
            foreach (long seed in FuzzConfig.Seeds().Take(8))
            {
                data.Add(seed);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomNoisyDecodesAreRestoredToTheExactValueAndScale(long seed)
    {
        var rng = new FuzzRandom(seed);
        for (int n = 0; n < 2_000; n++)
        {
            int scale = rng.Next(1, 29);
            decimal factor = Net11Factor(scale);
            decimal unscaled = NextUnscaled(rng);
            decimal[] values = [Decode(unscaled, factor)];

            ParquetDecimalScale.Restore(values, scale, factor);

            string context = $"seed {seed}, scale {scale}, unscaled {unscaled}";
            decimal expected = Exact(unscaled, scale);
            values[0].ShouldBe(expected, context);
            values[0].Scale.ShouldBe(expected.Scale, context);
        }
    }

    [Fact]
    public void NoiseCanReachTheColumnsOwnScaleSoNoisyValuesCanLookExact()
    {
        // Why the correction cannot skip values that look exact: at scale 10 an 18-digit unscaled
        // value decodes 36 units of the column's scale away, a well-formed decimal(18,10).
        decimal noisy = Decode(999_999_999_999_999_999m, Net11Factor(10));
        noisy.ShouldBe(100_000_000.000_000_003_5m);
        decimal.Round(noisy, 10).ShouldBe(noisy);

        decimal[] values = [noisy];
        ParquetDecimalScale.Restore(values, 10, Net11Factor(10));
        values[0].ShouldBe(99_999_999.999_999_999_9m);
    }

    [Fact]
    public void ExactValuesWithManyDigitsAreCorruptedSoOnlyIntegerChunksArePassed()
    {
        // A fixed-length decimal decodes exactly. Inverting the double's factor on one with a
        // 19-digit unscaled value moves it by tens of units, which is why the emitted reader and the
        // fuzz oracle pass only INT32- and INT64-backed chunks: this pins the hazard that
        // physical-type check prevents. (Unscaled values past the INT64 range are skipped as stale
        // slots and so happen to be safe.)
        decimal exact = 1_234_567_890.123_456_789m;
        decimal[] values = [exact];

        ParquetDecimalScale.Restore(values, 9, Net11Factor(9));

        values[0].ShouldNotBe(exact);
    }

    [Fact]
    public void NullableOverloadRestoresValuesAndKeepsNulls()
    {
        int scale = 2;
        decimal factor = Net11Factor(scale);
        decimal?[] values = [Decode(1_999m, factor), null, Decode(-5m, factor), null];
        decimal[] asValues = [values[0]!.Value, values[2]!.Value];

        ParquetDecimalScale.Restore(asValues, scale, factor);
        RestoreNullableWith(values, scale, factor);

        values.ShouldBe(new decimal?[] { 19.99m, null, -0.05m, null });
        asValues.ShouldBe(new[] { 19.99m, -0.05m });
    }

    [Fact]
    public void StaleSlotsBeyondTheInt64RangeAreLeftAloneWithoutThrowing()
    {
        // Pooled buffers can hold values from an earlier, wider column past the written range.
        decimal[] values = [decimal.MaxValue, decimal.MinValue, 1e27m, -1e27m];
        decimal[] before = values.ToArray();

        Should.NotThrow(() => ParquetDecimalScale.Restore(values, 2, Net11Factor(2)));

        values.ShouldBe(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(29)]
    public void ScalesWithNoFactorToCorrectAreANoOp(int scale)
    {
        ParquetDecimalScale.TryGetCorrection(scale, out _).ShouldBeFalse();
        decimal[] values = [0.0100000000000000002081668171m];
        ParquetDecimalScale.RestoreIntegerScaled(values, scale);
        values[0].ShouldBe(0.0100000000000000002081668171m);
    }

    [Theory]
    [InlineData(1, "0.1000000000000000055511151231")]
    [InlineData(2, "0.0100000000000000002081668171")]
    [InlineData(4, "0.0001000000000000000047921736")]
    [InlineData(10, "0.0000000001000000000000000036")]
    public void EmulatedFactorMatchesWhatDotNet11Produced(int scale, string measured)
    {
        // Measured on .NET 11.0.0-rc.3.26522.148: (decimal)Math.Pow(10, -scale).
        Net11Factor(scale).ShouldBe(decimal.Parse(measured, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheRuntimeFactorIsExactBeforeDotNet11AndTheEmulatedOneFrom11()
    {
        for (int scale = 1; scale <= 28; scale++)
        {
            bool corrects = ParquetDecimalScale.TryGetCorrection(scale, out decimal factor);
            if (Environment.Version.Major < 11)
            {
                corrects.ShouldBeFalse($"scale {scale}");
            }
            else if (corrects)
            {
                factor.ShouldBe(Net11Factor(scale), $"scale {scale}");
            }
        }
    }

    /// <summary>What Parquet.Net returns: <c>unscaled * (decimal)Math.Pow(10, -scale)</c>.</summary>
    private static decimal Decode(decimal unscaled, decimal factor) => unscaled * factor;

    private static decimal Exact(decimal unscaled, int scale) =>
        unscaled * new decimal(1, 0, 0, false, (byte)scale);

    private static void RestoreNullableWith(decimal?[] values, int scale, decimal factor)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is decimal value)
            {
                decimal[] one = [value];
                ParquetDecimalScale.Restore(one, scale, factor);
                values[i] = one[0];
            }
        }
    }

    /// <summary>An INT64-range unscaled value with a uniformly drawn digit count, or a limit.</summary>
    private static decimal NextUnscaled(FuzzRandom rng)
    {
        if (rng.Chance(0.05))
        {
            return rng.Pick([(decimal)long.MaxValue, long.MinValue, 0m]);
        }

        int digits = rng.Next(0, 19);
        decimal value = 0m;
        for (int i = 0; i < digits; i++)
        {
            value = (value * 10m) + rng.Next(0, 10);
        }

        return rng.Chance(0.5) ? -value : value;
    }

    /// <summary>
    /// The factor .NET 11 produces for <c>(decimal)Math.Pow(10, -scale)</c>: the double's exact
    /// binary value rounded to 28 decimal places, computed with integers so it is the same on every
    /// runtime.
    /// </summary>
    private static decimal Net11Factor(int scale)
    {
        double d = Math.Pow(10, -scale);
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        // d = mantissa * 2^(exponent - 1075), and exponent - 1075 is negative for every scale here.
        BigInteger numerator = mantissa * BigInteger.Pow(10, 28);
        BigInteger denominator = BigInteger.One << (1075 - exponent);
        BigInteger quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        if (remainder * 2 >= denominator)
        {
            quotient += 1;
        }

        byte[] bytes = quotient.ToByteArray();
        Array.Resize(ref bytes, 12);
        return new decimal(
            BitConverter.ToInt32(bytes, 0),
            BitConverter.ToInt32(bytes, 4),
            BitConverter.ToInt32(bytes, 8),
            false,
            28
        );
    }
}
