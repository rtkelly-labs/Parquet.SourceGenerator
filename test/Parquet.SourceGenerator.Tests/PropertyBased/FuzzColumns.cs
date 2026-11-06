using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Parquet.SourceGenerator.Tests.PropertyBased;

/// <summary>
/// Value factories for the fuzzable columns. Each one is a pure function of the supplied
/// <see cref="FuzzRandom"/>, so a case is fully determined by its seed.
/// </summary>
public static class FuzzValues
{
    /// <summary>
    /// Strings that have historically broken UTF-8 round trips somewhere in the stack: surrogate
    /// pairs, combining marks, right-to-left text, zero-width joiners, an embedded NUL, and a
    /// string whose UTF-8 length differs from its UTF-16 length.
    /// </summary>
    private static readonly string[] UnicodeSamples =
    [
        "",
        " ",
        "ascii",
        "naïve café",
        "日本語テキスト",
        "Ω≈ç√∫˜µ≤≥÷",
        "👨‍👩‍👧‍👦 family",
        "🇬🇧🇯🇵",
        "ȩ́ combining",
        "‮reversed‬",
        "line1\nline2\ttab",
        "nul\0inside",
        "﻿bom-prefixed",
        "𝔘𝔫𝔦𝔠𝔬𝔡𝔢 𝔤𝔬𝔱𝔥𝔦𝔠",
        "אבג עברית",
    ];

    /// <summary>
    /// Parquet's Impala timestamp encoding and the microsecond encoding disagree about the far ends
    /// of <see cref="DateTime"/>'s range, so the fuzz envelope stays inside the window both can
    /// represent. Anything outside it is a documented format limit, not a generator defect.
    /// </summary>
    public static readonly DateTime MinTimestamp = new(1700, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc cref="MinTimestamp"/>
    public static readonly DateTime MaxTimestamp = new(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string NextString(FuzzRandom rng, FuzzValueProfile profile) =>
        profile switch
        {
            FuzzValueProfile.Constant => "constant",
            FuzzValueProfile.Boundary => rng.Pick([string.Empty, " ", "\0", new string('x', 1)]),
            FuzzValueProfile.Extreme => rng.Chance(0.15)
                ? new string('大', rng.Next(500, 2_000))
                : rng.Pick(UnicodeSamples),
            _ => rng.Chance(0.35)
                ? rng.Pick(UnicodeSamples)
                : "row-"
                    + rng.Next(0, 1_000)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    public static byte[] NextBytes(FuzzRandom rng, FuzzValueProfile profile)
    {
        int length = profile switch
        {
            FuzzValueProfile.Constant => 4,
            FuzzValueProfile.Boundary => rng.Pick([0, 1, 2]),
            FuzzValueProfile.Extreme => rng.Chance(0.15) ? rng.Next(2_000, 8_000) : rng.Next(0, 8),
            _ => rng.Next(0, 24),
        };

        byte[] buffer = new byte[length];
        if (profile == FuzzValueProfile.Constant)
        {
            buffer.AsSpan().Fill(0xAB);
        }
        else if (profile == FuzzValueProfile.Boundary && length > 0)
        {
            buffer.AsSpan().Fill(rng.Chance(0.5) ? (byte)0x00 : (byte)0xFF);
        }
        else
        {
            rng.NextBytes(buffer);
        }

        return buffer;
    }

    public static long NextInt64(FuzzRandom rng, FuzzValueProfile profile) =>
        profile switch
        {
            FuzzValueProfile.Constant => 42L,
            FuzzValueProfile.Boundary => rng.Pick([long.MinValue, long.MaxValue, 0L, -1L, 1L]),
            FuzzValueProfile.Extreme => rng.Pick([
                long.MinValue + 1,
                long.MaxValue - 1,
                int.MinValue,
                int.MaxValue,
                0L,
            ]),
            _ => unchecked((long)rng.NextULong()),
        };

    public static int NextInt32(FuzzRandom rng, FuzzValueProfile profile) =>
        profile switch
        {
            FuzzValueProfile.Constant => 7,
            FuzzValueProfile.Boundary => rng.Pick([int.MinValue, int.MaxValue, 0, -1, 1]),
            FuzzValueProfile.Extreme => rng.Pick([int.MinValue + 1, int.MaxValue - 1, 0]),
            _ => unchecked((int)rng.NextULong()),
        };

    public static double NextDouble(FuzzRandom rng, FuzzValueProfile profile) =>
        profile switch
        {
            FuzzValueProfile.Constant => 1.5,
            FuzzValueProfile.Boundary => rng.Pick([
                double.MinValue,
                double.MaxValue,
                double.Epsilon,
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                0.0,
                -0.0,
            ]),
            FuzzValueProfile.Extreme => rng.Pick([
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                -0.0,
            ]),
            _ => BitConverter.Int64BitsToDouble(unchecked((long)rng.NextULong())) is var d
            && double.IsFinite(d)
                ? d
                : rng.NextDouble() * 1e6,
        };

    public static float NextSingle(FuzzRandom rng, FuzzValueProfile profile) =>
        profile switch
        {
            FuzzValueProfile.Constant => 1.5f,
            FuzzValueProfile.Boundary => rng.Pick([
                float.MinValue,
                float.MaxValue,
                float.Epsilon,
                float.NaN,
                float.PositiveInfinity,
                float.NegativeInfinity,
                0f,
                -0f,
            ]),
            _ => (float)(rng.NextDouble() * 1e6 - 5e5),
        };

    /// <summary>
    /// Produces a decimal that fits the column's declared precision and scale. Values outside the
    /// declaration are a caller error rather than a round-trip bug, so the generator stays inside it.
    /// </summary>
    public static decimal NextDecimal(
        FuzzRandom rng,
        FuzzValueProfile profile,
        int precision,
        int scale
    )
    {
        int integerDigits = precision - scale;
        decimal maxInteger = 1m;
        for (int i = 0; i < Math.Min(integerDigits, 18); i++)
        {
            maxInteger *= 10m;
        }

        maxInteger -= 1m;

        decimal fraction = 0m;
        decimal unit = 1m;
        for (int i = 0; i < scale; i++)
        {
            unit /= 10m;
            fraction += rng.Next(0, 10) * unit;
        }

        decimal integer = profile switch
        {
            FuzzValueProfile.Constant => 1m,
            FuzzValueProfile.Boundary => rng.Pick([0m, maxInteger, 1m]),
            _ => Math.Truncate((decimal)(rng.NextDouble()) * maxInteger),
        };

        decimal value =
            profile == FuzzValueProfile.Boundary && rng.Chance(0.4) ? integer : integer + fraction;

        return rng.Chance(0.5) ? -value : value;
    }

    public static DateTime NextDateTime(FuzzRandom rng, FuzzValueProfile profile) =>
        profile switch
        {
            FuzzValueProfile.Constant => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FuzzValueProfile.Boundary => rng.Pick([
                MinTimestamp,
                MaxTimestamp,
                DateTime.UnixEpoch,
                new DateTime(1970, 1, 1, 0, 0, 0, 1, DateTimeKind.Utc),
            ]),
            _ => MinTimestamp.AddTicks(
                (long)(rng.NextULong() % (ulong)(MaxTimestamp - MinTimestamp).Ticks)
            ),
        };

    public static DateOnly NextDateOnly(FuzzRandom rng, FuzzValueProfile profile) =>
        DateOnly.FromDateTime(NextDateTime(rng, profile));

    /// <summary>
    /// Parquet's TIME logical type carries a time of day, so the fuzzed range is [0, 24h).
    /// </summary>
    public static TimeSpan NextTimeSpan(FuzzRandom rng, FuzzValueProfile profile) =>
        profile switch
        {
            FuzzValueProfile.Constant => TimeSpan.FromMinutes(90),
            FuzzValueProfile.Boundary => rng.Pick([
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromDays(1) - TimeSpan.FromMilliseconds(1),
            ]),
            _ => TimeSpan.FromMilliseconds(rng.Next(0, 86_400_000)),
        };

    public static Guid NextGuid(FuzzRandom rng, FuzzValueProfile profile)
    {
        if (profile == FuzzValueProfile.Constant)
        {
            return new Guid("11111111-2222-3333-4444-555555555555");
        }

        if (profile == FuzzValueProfile.Boundary && rng.Chance(0.5))
        {
            return rng.Chance(0.5)
                ? Guid.Empty
                : new Guid(Enumerable.Repeat((byte)0xFF, 16).ToArray());
        }

        byte[] bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <summary>Truncates to the precision the column's Parquet encoding can carry.</summary>
    public static DateTime TruncateTo(DateTime value, long ticksPerUnit) =>
        new(value.Ticks - (value.Ticks % ticksPerUnit), DateTimeKind.Unspecified);

    public static string Describe(object? value) =>
        value switch
        {
            null => "<null>",
            byte[] bytes => bytes.Length <= 16
                ? $"byte[{bytes.Length}]({Convert.ToHexString(bytes)})"
                : $"byte[{bytes.Length}]({Convert.ToHexString(bytes.AsSpan(0, 16))}...)",
            string s => $"\"{Escape(s)}\"",
            DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "<empty>",
        };

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsControl(c) ? $"\\u{(int)c:X4}" : c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// The fuzzable column table for <see cref="FuzzWideRecord"/>. Ordered to match the generated
/// schema, so a column index here is the same column index the writer emits.
/// </summary>
public static class FuzzColumns
{
    private const long TicksPerMicrosecond = 10L;

    /// <summary>Gets every fuzzable column, in schema order.</summary>
    public static IReadOnlyList<FuzzColumn> All { get; } = Build();

    /// <summary>Gets the columns indexed by Parquet column name.</summary>
    public static IReadOnlyDictionary<string, FuzzColumn> ByName { get; } =
        All.ToDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>Gets the columns that cannot hold nulls, and so must be present in any file.</summary>
    public static IReadOnlyList<FuzzColumn> Required { get; } =
        All.Where(c => !c.Optional).ToList();

    /// <summary>Gets the columns that may be absent from a file or hold nulls.</summary>
    public static IReadOnlyList<FuzzColumn> Nullable { get; } = All.Where(c => c.Optional).ToList();

    private static List<FuzzColumn> Build() =>
        new List<FuzzColumn>
        {
            Column(
                "row_index",
                typeof(int),
                (rng, profile, row) => row,
                (r, v) => r.RowIndex = (int)v!,
                r => r.RowIndex,
                Identity
            ),
            Column(
                "flag",
                typeof(bool),
                (rng, profile, row) => rng.Chance(0.5),
                (r, v) => r.Flag = (bool)v!,
                r => r.Flag,
                Identity
            ),
            OptionalColumn(
                "opt_flag",
                typeof(bool),
                (rng, profile, row) => rng.Chance(0.5),
                (r, v) => r.OptFlag = (bool?)v,
                r => r.OptFlag,
                Identity
            ),
            Column(
                "i8",
                typeof(sbyte),
                (rng, profile, row) =>
                    profile == FuzzValueProfile.Boundary
                        ? rng.Pick([sbyte.MinValue, sbyte.MaxValue, (sbyte)0, (sbyte)(-1)])
                        : unchecked((sbyte)FuzzValues.NextInt32(rng, profile)),
                (r, v) => r.I8 = (sbyte)v!,
                r => r.I8,
                Identity
            ),
            Column(
                "u8",
                typeof(byte),
                (rng, profile, row) =>
                    profile == FuzzValueProfile.Boundary
                        ? rng.Pick([byte.MinValue, byte.MaxValue, (byte)1])
                        : unchecked((byte)FuzzValues.NextInt32(rng, profile)),
                (r, v) => r.U8 = (byte)v!,
                r => r.U8,
                Identity
            ),
            Column(
                "i16",
                typeof(short),
                (rng, profile, row) =>
                    profile == FuzzValueProfile.Boundary
                        ? rng.Pick([short.MinValue, short.MaxValue, (short)0, (short)(-1)])
                        : unchecked((short)FuzzValues.NextInt32(rng, profile)),
                (r, v) => r.I16 = (short)v!,
                r => r.I16,
                Identity
            ),
            Column(
                "u16",
                typeof(ushort),
                (rng, profile, row) =>
                    profile == FuzzValueProfile.Boundary
                        ? rng.Pick([ushort.MinValue, ushort.MaxValue, (ushort)1])
                        : unchecked((ushort)FuzzValues.NextInt32(rng, profile)),
                (r, v) => r.U16 = (ushort)v!,
                r => r.U16,
                Identity
            ),
            Column(
                "i32",
                typeof(int),
                (rng, profile, row) => FuzzValues.NextInt32(rng, profile),
                (r, v) => r.I32 = (int)v!,
                r => r.I32,
                Identity
            ),
            OptionalColumn(
                "opt_i32",
                typeof(int),
                (rng, profile, row) => FuzzValues.NextInt32(rng, profile),
                (r, v) => r.OptI32 = (int?)v,
                r => r.OptI32,
                Identity
            ),
            Column(
                "u32",
                typeof(uint),
                (rng, profile, row) =>
                    profile == FuzzValueProfile.Boundary
                        ? rng.Pick([uint.MinValue, uint.MaxValue, 1u])
                        : unchecked((uint)FuzzValues.NextInt32(rng, profile)),
                (r, v) => r.U32 = (uint)v!,
                r => r.U32,
                Identity
            ),
            Column(
                "i64",
                typeof(long),
                (rng, profile, row) => FuzzValues.NextInt64(rng, profile),
                (r, v) => r.I64 = (long)v!,
                r => r.I64,
                Identity
            ),
            OptionalColumn(
                "opt_i64",
                typeof(long),
                (rng, profile, row) => FuzzValues.NextInt64(rng, profile),
                (r, v) => r.OptI64 = (long?)v,
                r => r.OptI64,
                Identity
            ),
            Column(
                "f32",
                typeof(float),
                (rng, profile, row) => FuzzValues.NextSingle(rng, profile),
                (r, v) => r.F32 = (float)v!,
                r => r.F32,
                Identity
            ),
            Column(
                "f64",
                typeof(double),
                (rng, profile, row) => FuzzValues.NextDouble(rng, profile),
                (r, v) => r.F64 = (double)v!,
                r => r.F64,
                Identity
            ),
            OptionalColumn(
                "opt_f64",
                typeof(double),
                (rng, profile, row) => FuzzValues.NextDouble(rng, profile),
                (r, v) => r.OptF64 = (double?)v,
                r => r.OptF64,
                Identity
            ),
            Column(
                "text",
                typeof(string),
                (rng, profile, row) => FuzzValues.NextString(rng, profile),
                (r, v) => r.Text = (string)v!,
                r => r.Text,
                Identity
            ),
            OptionalColumn(
                "opt_text",
                typeof(string),
                (rng, profile, row) => FuzzValues.NextString(rng, profile),
                (r, v) => r.OptText = (string?)v,
                r => r.OptText,
                Identity
            ),
            OptionalColumn(
                "blob",
                typeof(byte[]),
                (rng, profile, row) => FuzzValues.NextBytes(rng, profile),
                (r, v) => r.Blob = (byte[]?)v,
                r => r.Blob,
                Identity
            ),
            Column(
                "money",
                typeof(decimal),
                (rng, profile, row) => FuzzValues.NextDecimal(rng, profile, 18, 6),
                (r, v) => r.Money = (decimal)v!,
                r => r.Money,
                Identity
            ),
            OptionalColumn(
                "opt_money",
                typeof(decimal),
                (rng, profile, row) => FuzzValues.NextDecimal(rng, profile, 9, 2),
                (r, v) => r.OptMoney = (decimal?)v,
                r => r.OptMoney,
                Identity
            ),
            Column(
                "ts_millis",
                typeof(DateTime),
                (rng, profile, row) => FuzzValues.NextDateTime(rng, profile),
                (r, v) => r.TsMillis = (DateTime)v!,
                r => r.TsMillis,
                TruncateMillis
            ),
            Column(
                "ts_micros",
                typeof(DateTime),
                (rng, profile, row) => FuzzValues.NextDateTime(rng, profile),
                (r, v) => r.TsMicros = (DateTime)v!,
                r => r.TsMicros,
                TruncateMicros
            ),
            OptionalColumn(
                "opt_ts",
                typeof(DateTime),
                (rng, profile, row) => FuzzValues.NextDateTime(rng, profile),
                (r, v) => r.OptTs = (DateTime?)v,
                r => r.OptTs,
                TruncateMillis
            ),
            Column(
                "day",
                typeof(DateTime),
                (rng, profile, row) => FuzzValues.NextDateOnly(rng, profile),
                (r, v) => r.Day = (DateOnly)v!,
                r => r.Day,
                DateOnlyToDateTime
            ),
            Column(
                "duration",
                typeof(TimeSpan),
                (rng, profile, row) => FuzzValues.NextTimeSpan(rng, profile),
                (r, v) => r.Duration = (TimeSpan)v!,
                r => r.Duration,
                TruncateTimeSpanMillis
            ),
            Column(
                "guid",
                typeof(Guid),
                (rng, profile, row) => FuzzValues.NextGuid(rng, profile),
                (r, v) => r.RowGuid = (Guid)v!,
                r => r.RowGuid,
                Identity
            ),
            OptionalColumn(
                "opt_guid",
                typeof(Guid),
                (rng, profile, row) => FuzzValues.NextGuid(rng, profile),
                (r, v) => r.OptGuid = (Guid?)v,
                r => r.OptGuid,
                Identity
            ),
            Column(
                "status",
                typeof(int),
                (rng, profile, row) => (EventStatus)rng.Next(0, 3),
                (r, v) => r.Status = (EventStatus)v!,
                r => r.Status,
                EnumToInt
            ),
            OptionalColumn(
                "opt_status",
                typeof(int),
                (rng, profile, row) => (EventStatus)rng.Next(0, 3),
                (r, v) => r.OptStatus = (EventStatus?)v,
                r => r.OptStatus,
                EnumToInt
            ),
        };

    private static object? Identity(object? value) => value;

    private static object? TruncateMillis(object? value) =>
        value is DateTime dt ? FuzzValues.TruncateTo(dt, TimeSpan.TicksPerMillisecond) : value;

    private static object? TruncateMicros(object? value) =>
        value is DateTime dt ? FuzzValues.TruncateTo(dt, TicksPerMicrosecond) : value;

    private static object? DateOnlyToDateTime(object? value) =>
        value is DateOnly day ? day.ToDateTime(TimeOnly.MinValue) : value;

    private static object? TruncateTimeSpanMillis(object? value) =>
        value is TimeSpan span
            ? TimeSpan.FromTicks(span.Ticks - (span.Ticks % TimeSpan.TicksPerMillisecond))
            : value;

    private static object? EnumToInt(object? value) =>
        value is EventStatus status ? (int)status : value;

    private static FuzzColumn Column(
        string name,
        Type canonicalType,
        Func<FuzzRandom, FuzzValueProfile, int, object?> generate,
        Action<FuzzWideRecord, object?> assign,
        Func<FuzzWideRecord, object?> read,
        Func<object?, object?> normalize
    ) => new(name, optional: false, canonicalType, generate, assign, read, normalize);

    private static FuzzColumn OptionalColumn(
        string name,
        Type canonicalType,
        Func<FuzzRandom, FuzzValueProfile, int, object?> generate,
        Action<FuzzWideRecord, object?> assign,
        Func<FuzzWideRecord, object?> read,
        Func<object?, object?> normalize
    ) => new(name, optional: true, canonicalType, generate, assign, read, normalize);
}
