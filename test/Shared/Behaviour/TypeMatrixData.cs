using System;
using System.Collections.Generic;
using System.Linq;
using Parquet.File.Values.Primitives;

namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// One column of the type matrix: how to read it off either model, and whether it is optional.
/// </summary>
public sealed class MatrixColumn
{
    public MatrixColumn(
        string name,
        Func<TypeMatrixRow, object?> fromRow,
        Func<TypeMatrixRequiredOnly, object?>? fromRequiredOnly
    )
    {
        Name = name;
        FromRow = fromRow;
        FromRequiredOnly = fromRequiredOnly;
    }

    public string Name { get; }

    public bool Optional => FromRequiredOnly is null;

    public Func<TypeMatrixRow, object?> FromRow { get; }

    /// <summary>Null for an optional column, which the required-only model does not carry.</summary>
    public Func<TypeMatrixRequiredOnly, object?>? FromRequiredOnly { get; }
}

/// <summary>
/// Values for the type matrix. Each type cycles through its edge values (minimum, maximum, zero,
/// empty, non-ASCII, NaN and so on); each optional column is null for the whole first row group
/// (the all-null group) and then for every third row.
/// </summary>
public static class TypeMatrixData
{
    /// <summary>Rows per row group in the multi-row-group cells.</summary>
    public const int RowGroupSize = 4;

    /// <summary>Six row groups, the last of them short.</summary>
    public const int RowCount = (RowGroupSize * 5) + 2;

    private static readonly DateTime[] DateTimes =
    [
        new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2024, 6, 15, 12, 30, 45, 123, DateTimeKind.Utc),
        new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
    ];

    private static readonly DateTime[] MicroDateTimes =
    [
        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Utc).AddTicks(1_234_560),
        new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(10),
        new DateTime(2262, 4, 11, 23, 47, 16, DateTimeKind.Utc),
    ];

    private static readonly TimeSpan[] TimeSpans =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(1),
        new TimeSpan(0, 12, 34, 56, 789),
        new TimeSpan(0, 23, 59, 59, 999),
    ];

    private static readonly Guid[] Guids =
    [
        Guid.Empty,
        new("0f8fad5b-d9cb-469f-a165-70867728950e"),
        new("ffffffff-ffff-ffff-ffff-ffffffffffff"),
        new("00000000-0000-0000-0000-000000000001"),
    ];

    private static readonly string[] Strings =
    [
        string.Empty,
        "plain",
        "café \U0001F600 日本",
        new string('x', 1_000),
    ];

    // No empty array here: an empty binary value is its own test (EmptyBinaryValueRoundTrips),
    // because Parquet.Net 4.25 cannot read one back and it would take every other column with it.
    private static readonly byte[][] Payloads =
    [
        [byte.MaxValue],
        [0],
        [0, 255, 1, 254],
        Enumerable.Range(0, 1_000).Select(i => (byte)i).ToArray(),
    ];

    private static readonly decimal[] Decimals =
    [
        0m,
        -1.5m,
        1234567890.123456789m,
        -9999999999.999999999m,
        0.000000000000000001m,
    ];

    private static readonly decimal[] NarrowDecimals =
    [
        0m,
        99999999999999.9999m,
        -1.2345m,
        -99999999999999.9999m,
    ];

    private static readonly float[] Floats =
    [
        float.MinValue,
        -0.5f,
        0f,
        float.Epsilon,
        float.MaxValue,
        float.NaN,
        float.PositiveInfinity,
    ];

    private static readonly double[] Doubles =
    [
        double.MinValue,
        -0.5,
        0d,
        double.Epsilon,
        double.MaxValue,
        double.NaN,
        double.NegativeInfinity,
    ];

    private static readonly Interval[] Intervals =
    [
        new(0, 0, 0),
        new(1, 2, 3),
        new(12, 30, 86_399_999),
        new(int.MaxValue, int.MaxValue, int.MaxValue),
    ];

#if NET6_0_OR_GREATER
    private static readonly DateOnly[] DateOnlys =
    [
        new(1970, 1, 1),
        new(2024, 2, 29),
        DateOnly.MinValue,
        DateOnly.MaxValue,
    ];

    private static readonly TimeOnly[] TimeOnlys =
    [
        TimeOnly.MinValue,
        new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(1_234_560)),
        new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(9_999_990)),
        new TimeOnly(0, 0, 0).Add(TimeSpan.FromTicks(10)),
    ];
#endif

    /// <summary>Every column of <see cref="TypeMatrixRow"/>, in declaration order.</summary>
    public static IReadOnlyList<MatrixColumn> Columns { get; } = BuildColumns();

    public static MatrixColumn Column(string name) => Columns.Single(c => c.Name == name);

    /// <summary>Whether row <paramref name="index"/> leaves its optional columns null.</summary>
    public static bool IsNullRow(int index) => index < RowGroupSize || index % 3 == 0;

    public static List<TypeMatrixRow> BuildRows(int count = RowCount)
    {
        var rows = new List<TypeMatrixRow>(count);
        for (int i = 0; i < count; i++)
        {
            bool nil = IsNullRow(i);
            rows.Add(
                new TypeMatrixRow
                {
                    Id = i,
                    Bool = i % 2 == 0,
                    BoolOpt = nil ? null : i % 2 == 1,
                    Byte = Pick(i, new byte[] { 0, 1, 127, byte.MaxValue }),
                    ByteOpt = nil ? null : Pick(i + 1, new byte[] { 0, 1, 127, byte.MaxValue }),
                    SByte = Pick(i, new sbyte[] { sbyte.MinValue, -1, 0, sbyte.MaxValue }),
                    SByteOpt = nil
                        ? null
                        : Pick(i + 1, new sbyte[] { sbyte.MinValue, -1, 0, sbyte.MaxValue }),
                    ShortValue = Pick(i, new short[] { short.MinValue, -1, 0, short.MaxValue }),
                    ShortOpt = nil
                        ? null
                        : Pick(i + 1, new short[] { short.MinValue, -1, 0, short.MaxValue }),
                    UShortValue = Pick(i, new ushort[] { 0, 1, 32_768, ushort.MaxValue }),
                    UShortOpt = nil ? null : Pick(i + 1, new ushort[] { 0, 1, ushort.MaxValue }),
                    IntValue = Pick(i, new[] { int.MinValue, -1, 0, int.MaxValue }),
                    IntOpt = nil ? null : Pick(i + 1, new[] { int.MinValue, -1, 0, int.MaxValue }),
                    UIntValue = Pick(i, new uint[] { 0, 1, 2_147_483_648u, uint.MaxValue }),
                    UIntOpt = nil ? null : Pick(i + 1, new uint[] { 0, 1, uint.MaxValue }),
                    LongValue = Pick(i, new[] { long.MinValue, -1L, 0L, long.MaxValue }),
                    LongOpt = nil
                        ? null
                        : Pick(i + 1, new[] { long.MinValue, -1L, 0L, long.MaxValue }),
                    ULongValue = Pick(
                        i,
                        new ulong[] { 0, 1, 9_223_372_036_854_775_808ul, ulong.MaxValue }
                    ),
                    ULongOpt = nil ? null : Pick(i + 1, new ulong[] { 0, 1, ulong.MaxValue }),
                    FloatValue = Pick(i, Floats),
                    FloatOpt = nil ? null : Pick(i + 1, Floats),
                    DoubleValue = Pick(i, Doubles),
                    DoubleOpt = nil ? null : Pick(i + 1, Doubles),
                    DecimalValue = Pick(i, Decimals),
                    DecimalOpt = nil ? null : Pick(i + 1, Decimals),
                    DecimalNarrow = Pick(i, NarrowDecimals),
                    DateTime = Pick(i, DateTimes),
                    DateTimeOpt = nil ? null : Pick(i + 1, DateTimes),
                    DateTimeMicros = Pick(i, MicroDateTimes),
                    TimeSpan = Pick(i, TimeSpans),
                    TimeSpanOpt = nil ? null : Pick(i + 1, TimeSpans),
                    GuidValue = Pick(i, Guids),
                    GuidOpt = nil ? null : Pick(i + 1, Guids),
                    StringValue = Pick(i, Strings),
                    StringOpt = nil ? null : Pick(i + 1, Strings),
                    Bytes = Pick(i, Payloads),
                    BytesOpt = nil ? null : Pick(i + 1, Payloads),
                    Enum = Pick(
                        i,
                        new[]
                        {
                            MatrixIntLevel.Zero,
                            MatrixIntLevel.One,
                            MatrixIntLevel.Max,
                            MatrixIntLevel.Min,
                        }
                    ),
                    EnumOpt = nil
                        ? null
                        : Pick(i + 1, new[] { MatrixIntLevel.One, MatrixIntLevel.Min }),
                    EnumByte = Pick(i, new[] { MatrixByteLevel.Low, MatrixByteLevel.High }),
                    EnumByteOpt = nil
                        ? null
                        : Pick(i + 1, new[] { MatrixByteLevel.Low, MatrixByteLevel.High }),
                    EnumLong = Pick(
                        i,
                        new[] { MatrixLongLevel.Low, MatrixLongLevel.Mid, MatrixLongLevel.High }
                    ),
                    EnumLongOpt = nil
                        ? null
                        : Pick(
                            i + 1,
                            new[] { MatrixLongLevel.Low, MatrixLongLevel.Mid, MatrixLongLevel.High }
                        ),
                    Interval = Pick(i, Intervals),
                    IntervalOpt = nil ? null : Pick(i + 1, Intervals),
#if NET6_0_OR_GREATER
                    DateOnly = Pick(i, DateOnlys),
                    DateOnlyOpt = nil ? null : Pick(i + 1, DateOnlys),
                    TimeOnly = Pick(i, TimeOnlys),
                    TimeOnlyOpt = nil ? null : Pick(i + 1, TimeOnlys),
#endif
                }
            );
        }

        return rows;
    }

    /// <summary>The required-only projection of <see cref="BuildRows"/>.</summary>
    public static List<TypeMatrixRequiredOnly> BuildRequiredOnlyRows(int count = RowCount) =>
        BuildRows(count)
            .Select(r => new TypeMatrixRequiredOnly
            {
                Id = r.Id,
                Bool = r.Bool,
                Byte = r.Byte,
                SByte = r.SByte,
                ShortValue = r.ShortValue,
                UShortValue = r.UShortValue,
                IntValue = r.IntValue,
                UIntValue = r.UIntValue,
                LongValue = r.LongValue,
                ULongValue = r.ULongValue,
                FloatValue = r.FloatValue,
                DoubleValue = r.DoubleValue,
                DecimalValue = r.DecimalValue,
                DecimalNarrow = r.DecimalNarrow,
                DateTime = r.DateTime,
                DateTimeMicros = r.DateTimeMicros,
                TimeSpan = r.TimeSpan,
                GuidValue = r.GuidValue,
                StringValue = r.StringValue,
                Bytes = r.Bytes,
                Enum = r.Enum,
                EnumByte = r.EnumByte,
                EnumLong = r.EnumLong,
                Interval = r.Interval,
#if NET6_0_OR_GREATER
                DateOnly = r.DateOnly,
                TimeOnly = r.TimeOnly,
#endif
            })
            .ToList();

    /// <summary>
    /// Value equality for the matrix: arrays by content, floating point by bit pattern class
    /// (<c>NaN</c> equals <c>NaN</c>), timestamps by ticks (the kind is not stored), intervals
    /// by their three parts.
    /// </summary>
    public static bool ValuesEqual(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return (expected, actual) switch
        {
            (byte[] e, byte[] a) => e.SequenceEqual(a),
            (DateTime e, DateTime a) => e.Ticks == a.Ticks,
            (Interval e, Interval a) => e.Months == a.Months
                && e.Days == a.Days
                && e.Millis == a.Millis,
            _ => expected.Equals(actual),
        };
    }

    public static string Describe(object? value) =>
        value switch
        {
            null => "null",
            byte[] bytes => $"byte[{bytes.Length}]",
            string text when text.Length > 40 => $"string[{text.Length}]",
            DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Interval iv => $"Interval({iv.Months}, {iv.Days}, {iv.Millis})",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "?",
        };

    private static T Pick<T>(int index, T[] values) => values[index % values.Length];

    private static List<MatrixColumn> BuildColumns()
    {
        var columns = new List<MatrixColumn>
        {
            new("id", r => r.Id, r => r.Id),
            new("bool", r => r.Bool, r => r.Bool),
            new("bool_opt", r => r.BoolOpt, null),
            new("byte", r => r.Byte, r => r.Byte),
            new("byte_opt", r => r.ByteOpt, null),
            new("sbyte", r => r.SByte, r => r.SByte),
            new("sbyte_opt", r => r.SByteOpt, null),
            new("short", r => r.ShortValue, r => r.ShortValue),
            new("short_opt", r => r.ShortOpt, null),
            new("ushort", r => r.UShortValue, r => r.UShortValue),
            new("ushort_opt", r => r.UShortOpt, null),
            new("int", r => r.IntValue, r => r.IntValue),
            new("int_opt", r => r.IntOpt, null),
            new("uint", r => r.UIntValue, r => r.UIntValue),
            new("uint_opt", r => r.UIntOpt, null),
            new("long", r => r.LongValue, r => r.LongValue),
            new("long_opt", r => r.LongOpt, null),
            new("ulong", r => r.ULongValue, r => r.ULongValue),
            new("ulong_opt", r => r.ULongOpt, null),
            new("float", r => r.FloatValue, r => r.FloatValue),
            new("float_opt", r => r.FloatOpt, null),
            new("double", r => r.DoubleValue, r => r.DoubleValue),
            new("double_opt", r => r.DoubleOpt, null),
            new("decimal", r => r.DecimalValue, r => r.DecimalValue),
            new("decimal_opt", r => r.DecimalOpt, null),
            new("decimal_18_4", r => r.DecimalNarrow, r => r.DecimalNarrow),
            new("datetime", r => r.DateTime, r => r.DateTime),
            new("datetime_opt", r => r.DateTimeOpt, null),
            new("datetime_micros", r => r.DateTimeMicros, r => r.DateTimeMicros),
            new("timespan", r => r.TimeSpan, r => r.TimeSpan),
            new("timespan_opt", r => r.TimeSpanOpt, null),
            new("guid", r => r.GuidValue, r => r.GuidValue),
            new("guid_opt", r => r.GuidOpt, null),
            new("string", r => r.StringValue, r => r.StringValue),
            new("string_opt", r => r.StringOpt, null),
            new("bytes", r => r.Bytes, r => r.Bytes),
            new("bytes_opt", r => r.BytesOpt, null),
            new("enum", r => r.Enum, r => r.Enum),
            new("enum_opt", r => r.EnumOpt, null),
            new("enum_byte", r => r.EnumByte, r => r.EnumByte),
            new("enum_byte_opt", r => r.EnumByteOpt, null),
            new("enum_long", r => r.EnumLong, r => r.EnumLong),
            new("enum_long_opt", r => r.EnumLongOpt, null),
            new("interval", r => r.Interval, r => r.Interval),
            new("interval_opt", r => r.IntervalOpt, null),
        };
#if NET6_0_OR_GREATER
        columns.Add(new("dateonly", r => r.DateOnly, r => r.DateOnly));
        columns.Add(new("dateonly_opt", r => r.DateOnlyOpt, null));
        columns.Add(new("timeonly", r => r.TimeOnly, r => r.TimeOnly));
        columns.Add(new("timeonly_opt", r => r.TimeOnlyOpt, null));
#endif
        return columns;
    }
}
