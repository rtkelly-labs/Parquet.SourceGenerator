using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Tests.PropertyBased;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// One model per decimal shape, each with a required and an optional column. Precision decides the
// physical type Parquet.Net writes: up to 9 is INT32, up to 18 INT64, above that a fixed-length
// byte array. The integer-backed shapes are decoded through a factor derived from a double; the
// byte-array shape through BigInteger.

[ParquetSerializable]
public partial record DecimalShapeP9S0
{
    [ParquetColumn("value")]
    [ParquetDecimal(9, 0)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(9, 0)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP9S2
{
    [ParquetColumn("value")]
    [ParquetDecimal(9, 2)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(9, 2)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP9S9
{
    [ParquetColumn("value")]
    [ParquetDecimal(9, 9)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(9, 9)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP18S1
{
    [ParquetColumn("value")]
    [ParquetDecimal(18, 1)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(18, 1)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP18S2
{
    [ParquetColumn("value")]
    [ParquetDecimal(18, 2)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(18, 2)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP18S6
{
    [ParquetColumn("value")]
    [ParquetDecimal(18, 6)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(18, 6)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP18S10
{
    [ParquetColumn("value")]
    [ParquetDecimal(18, 10)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(18, 10)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP18S17
{
    [ParquetColumn("value")]
    [ParquetDecimal(18, 17)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(18, 17)]
    public decimal? Opt { get; init; }
}

[ParquetSerializable]
public partial record DecimalShapeP28S10
{
    [ParquetColumn("value")]
    [ParquetDecimal(28, 10)]
    public decimal Value { get; init; }

    [ParquetColumn("opt")]
    [ParquetDecimal(28, 10)]
    public decimal? Opt { get; init; }
}

/// <summary>
/// Pins exact decimal round trips across the shapes Parquet.Net decodes differently. Two layers: a
/// matrix of named boundary values per shape (zero, one, the int32/int64 and precision limits, the
/// double-integer limit 2^53, powers of ten and all-nines at each width, trailing zeros), and seeded
/// properties that fill the space between them with log-uniform values, nulls and row-group
/// layouts. Every case goes through both the POCO reader and the batch reader, which decode decimals
/// on separate paths.
/// </summary>
/// <remarks>
/// The integer-backed shapes depend on Parquet.Net's <c>unscaled * (decimal)Math.Pow(10, -scale)</c>,
/// which is exact only while the runtime's <c>double</c> to <c>decimal</c> conversion rounds to 15
/// significant digits. <see cref="DoubleToDecimalConversionRoundsPowersOfTenOnlyBeforeDotNet11"/>
/// records that fact; on .NET 11 the integer-backed rows here fail until the readers correct for it.
/// </remarks>
public sealed class DecimalScaleRoundTripTests
{
    private sealed record Shape(
        string Name,
        int Precision,
        int Scale,
        Func<IReadOnlyList<(decimal Value, decimal? Opt)>, int, Task<byte[]>> WriteAsync,
        Func<byte[], Task<(decimal Value, decimal? Opt)[]>> ReadPocoAsync,
        Func<byte[], Task<(decimal Value, decimal? Opt)[]>> ReadBatchesAsync
    );

    private static readonly Shape[] Shapes =
    [
        new(
            "P9S0",
            9,
            0,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP9S0 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP9S0Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP9S0Batch batch in DecimalShapeP9S0Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P9S2",
            9,
            2,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP9S2 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP9S2Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP9S2Batch batch in DecimalShapeP9S2Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P9S9",
            9,
            9,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP9S9 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP9S9Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP9S9Batch batch in DecimalShapeP9S9Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P18S1",
            18,
            1,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP18S1 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP18S1Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP18S1Batch batch in DecimalShapeP18S1Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P18S2",
            18,
            2,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP18S2 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP18S2Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP18S2Batch batch in DecimalShapeP18S2Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P18S6",
            18,
            6,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP18S6 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP18S6Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP18S6Batch batch in DecimalShapeP18S6Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P18S10",
            18,
            10,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP18S10 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP18S10Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP18S10Batch batch in DecimalShapeP18S10Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P18S17",
            18,
            17,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP18S17 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP18S17Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP18S17Batch batch in DecimalShapeP18S17Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
        new(
            "P28S10",
            28,
            10,
            (rows, groupSize) =>
                WriteAsync(
                    rows.Select(r => new DecimalShapeP28S10 { Value = r.Value, Opt = r.Opt }),
                    groupSize
                ),
            async b =>
                (await DecimalShapeP28S10Parquet.From(new MemoryStream(b)).ToArrayAsync())
                    .Select(r => (r.Value, r.Opt))
                    .ToArray(),
            async b =>
            {
                var read = new List<(decimal, decimal?)>();
                await foreach (
                    DecimalShapeP28S10Batch batch in DecimalShapeP28S10Parquet
                        .From(new MemoryStream(b))
                        .AsBatches()
                )
                {
                    var opt = new decimal?[batch.RowCount];
                    batch.FillOptNullable(opt);
                    decimal[] values = batch.Value.ToArray();
                    read.AddRange(values.Select((v, i) => (v, opt[i])));
                }

                return read.ToArray();
            }
        ),
    ];

    /// <summary>
    /// Unscaled magnitudes worth naming. Each shape takes the ones with at most its precision in
    /// digits; every case is also run negated.
    /// </summary>
    private static readonly decimal[] NamedUnscaled =
    [
        0m,
        1m,
        7m,
        10m,
        1_999m,
        1_900m, // trailing zeros
        12_345m,
        100_000m,
        999_999m,
        123_456_789m,
        999_999_999m, // INT32 decimal precision limit
        int.MaxValue,
        1_000_000_000m,
        9_007_199_254_740_992m, // 2^53: the last integer a double holds exactly
        9_007_199_254_740_993m,
        99_999_999_999_999_999m, // 17 digits
        100_000_000_000_000_000m, // 10^17
        123_456_789_012_345_678m,
        999_999_999_999_999_999m, // INT64 decimal precision limit
        1_000_000_000_000_000_000_000m, // 10^21
        9_999_999_999_999_999_999_999_999_999m, // 28 nines
    ];

    public static TheoryData<string, string> MatrixCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (Shape shape in Shapes)
            {
                foreach (decimal unscaled in NamedUnscaled.Where(u => Digits(u) <= shape.Precision))
                {
                    data.Add(shape.Name, unscaled.ToString(CultureInfo.InvariantCulture));
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(MatrixCases))]
    public async Task NamedValueRoundTripsExactlyThroughPocoAndBatchReaders(
        string shapeName,
        string unscaledText
    )
    {
        Shape shape = Shapes.Single(s => s.Name == shapeName);
        decimal value = Scaled(
            decimal.Parse(unscaledText, CultureInfo.InvariantCulture),
            shape.Scale
        );
        (decimal Value, decimal? Opt)[] rows = [(value, value), (-value, null), (value, -value)];

        await AssertRoundTripAsync(
            shape,
            rows,
            rowGroupSize: 2,
            $"{shape.Name} unscaled {unscaledText}"
        );
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
    public async Task RandomValuesRoundTripExactlyForEveryShape(long seed)
    {
        var rng = new FuzzRandom(seed);
        foreach (Shape shape in Shapes)
        {
            int count = rng.Next(1, 400);
            var rows = new (decimal Value, decimal? Opt)[count];
            for (int i = 0; i < count; i++)
            {
                decimal value = Scaled(NextUnscaled(rng, shape.Precision), shape.Scale);
                decimal? opt = rng.Chance(0.2)
                    ? null
                    : Scaled(NextUnscaled(rng, shape.Precision), shape.Scale);
                rows[i] = (value, opt);
            }

            int rowGroupSize = rng.Pick([1, 7, 64, 1_000]);
            await AssertRoundTripAsync(
                shape,
                rows,
                rowGroupSize,
                $"seed {seed}, {shape.Name}, {count} rows, row groups of {rowGroupSize}"
            );
        }
    }

    /// <summary>
    /// The runtime fact the integer-backed shapes rest on, kept executable: Parquet.Net's
    /// <c>(decimal)Math.Pow(10, -2)</c> is exactly 0.01 only while the conversion rounds to 15
    /// significant digits. .NET 11 keeps the double's value. If this flips, read the remarks on this
    /// class.
    /// </summary>
    [Fact]
    public void DoubleToDecimalConversionRoundsPowersOfTenOnlyBeforeDotNet11()
    {
        decimal factor = (decimal)Math.Pow(10, -2);
        if (Environment.Version.Major < 11)
        {
            factor.ShouldBe(0.01m);
        }
        else
        {
            factor.ShouldBe(0.0100000000000000002081668171m);
        }
    }

    private static async Task AssertRoundTripAsync(
        Shape shape,
        IReadOnlyList<(decimal Value, decimal? Opt)> rows,
        int rowGroupSize,
        string description
    )
    {
        byte[] bytes = await shape.WriteAsync(rows, rowGroupSize);
        AssertSame(shape, rows, await shape.ReadPocoAsync(bytes), $"POCO reader, {description}");
        AssertSame(
            shape,
            rows,
            await shape.ReadBatchesAsync(bytes),
            $"batch reader, {description}"
        );
    }

    private static void AssertSame(
        Shape shape,
        IReadOnlyList<(decimal Value, decimal? Opt)> expected,
        (decimal Value, decimal? Opt)[] actual,
        string description
    )
    {
        actual.Length.ShouldBe(expected.Count, description);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Value.ShouldBe(expected[i].Value, $"{description}: row {i} value");
            actual[i].Opt.ShouldBe(expected[i].Opt, $"{description}: row {i} opt");
            // Equal is not enough: 19.99 and 19.990 are equal but print differently, so the
            // representation is pinned too (see ExpectedScale).
            actual[i]
                .Value.Scale.ShouldBe(
                    ExpectedScale(shape, expected[i].Value),
                    $"{description}: row {i} value scale"
                );
            if (expected[i].Opt is decimal opt)
            {
                actual[i]
                    .Opt!.Value.Scale.ShouldBe(
                        ExpectedScale(shape, opt),
                        $"{description}: row {i} opt scale"
                    );
            }
        }
    }

    private static async Task<byte[]> WriteAsync<T>(IEnumerable<T> rows, int rowGroupSize)
    {
        using var stream = new MemoryStream();
        await WriteRowsAsync(rows.ToList(), stream, rowGroupSize);
        return stream.ToArray();
    }

    // The generated WriteParquetAsync is an extension on each model's own collection type, so the
    // generic helper dispatches to it by model.
    private static Task WriteRowsAsync<T>(List<T> rows, Stream stream, int rowGroupSize)
    {
        var options = new ParquetSerializerOptions { RowGroupSize = rowGroupSize };
        return rows switch
        {
            List<DecimalShapeP9S0> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP9S2> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP9S9> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP18S1> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP18S2> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP18S6> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP18S10> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP18S17> r => r.WriteParquetAsync(stream, options),
            List<DecimalShapeP28S10> r => r.WriteParquetAsync(stream, options),
            _ => throw new NotSupportedException(typeof(T).Name),
        };
    }

    /// <summary>
    /// A random unscaled value with a uniformly drawn digit count, so one-digit and full-precision
    /// values are equally likely, built digit by digit (never through a double).
    /// </summary>
    private static decimal NextUnscaled(FuzzRandom rng, int precision)
    {
        int digits = rng.Next(0, precision + 1);
        decimal value = 0m;
        for (int i = 0; i < digits; i++)
        {
            value = (value * 10m) + rng.Next(0, 10);
        }

        return rng.Chance(0.5) ? -value : value;
    }

    /// <summary>
    /// The scale a read value carries today. Integer-backed columns come back at the column's scale
    /// (19.99 at scale 2), as written. Fixed-length columns decode through BigInteger and come back
    /// with trailing zeros dropped (1900 unscaled at scale 10 reads as 190, not 190.0000000000).
    /// Both are pinned as they are, so a change to either is a decision rather than an accident.
    /// </summary>
    private static byte ExpectedScale(Shape shape, decimal written) =>
        shape.Precision <= 18 ? written.Scale : (written / 1.0000000000000000000000000000m).Scale;

    /// <summary>The exact value of <paramref name="unscaled"/> at <paramref name="scale"/>.</summary>
    private static decimal Scaled(decimal unscaled, int scale) =>
        unscaled * new decimal(1, 0, 0, false, (byte)scale);

    private static int Digits(decimal unscaled) =>
        Math.Abs(unscaled).ToString(CultureInfo.InvariantCulture).TrimStart('0').Length;
}
