using System;
using Parquet.File.Values.Primitives;

namespace Parquet.SourceGenerator.Behaviour;

// The shared behavioural suite (#597, #490): this folder is compiled into
// Parquet.SourceGenerator.Tests (modern backend, Parquet.Net 6.x) and into
// Parquet.SourceGenerator.Legacy.Tests (legacy backend, Parquet.Net 4.25), and each project's
// generator emits its own serializer for the models below. The only per-backend code is the
// driver (BehaviourDriver.cs declares it, each project implements it), so the #494 surface
// alignment touches one file per backend and none of the tests.
//
// Plain classes with settable properties: net472 has no IsExternalInit, so records and `init`
// accessors are not available to the legacy leg.

public enum MatrixIntLevel
{
    Zero = 0,
    One = 1,
    Max = int.MaxValue,
    Min = int.MinValue,
}

public enum MatrixByteLevel : byte
{
    Low = 0,
    High = byte.MaxValue,
}

public enum MatrixLongLevel : long
{
    Low = long.MinValue,
    Mid = 0,
    High = long.MaxValue,
}

/// <summary>
/// One required and one optional column for every flat type both backends support: the type
/// matrix. Column names are explicit so a narrower model can describe a subset of the same file.
/// </summary>
[ParquetSerializable]
public sealed partial class TypeMatrixRow
{
    [ParquetColumn("id")]
    public int Id { get; set; }

    [ParquetColumn("bool")]
    public bool Bool { get; set; }

    [ParquetColumn("bool_opt")]
    public bool? BoolOpt { get; set; }

    [ParquetColumn("byte")]
    public byte Byte { get; set; }

    [ParquetColumn("byte_opt")]
    public byte? ByteOpt { get; set; }

    [ParquetColumn("sbyte")]
    public sbyte SByte { get; set; }

    [ParquetColumn("sbyte_opt")]
    public sbyte? SByteOpt { get; set; }

    [ParquetColumn("short")]
    public short ShortValue { get; set; }

    [ParquetColumn("short_opt")]
    public short? ShortOpt { get; set; }

    [ParquetColumn("ushort")]
    public ushort UShortValue { get; set; }

    [ParquetColumn("ushort_opt")]
    public ushort? UShortOpt { get; set; }

    [ParquetColumn("int")]
    public int IntValue { get; set; }

    [ParquetColumn("int_opt")]
    public int? IntOpt { get; set; }

    [ParquetColumn("uint")]
    public uint UIntValue { get; set; }

    [ParquetColumn("uint_opt")]
    public uint? UIntOpt { get; set; }

    [ParquetColumn("long")]
    public long LongValue { get; set; }

    [ParquetColumn("long_opt")]
    public long? LongOpt { get; set; }

    [ParquetColumn("ulong")]
    public ulong ULongValue { get; set; }

    [ParquetColumn("ulong_opt")]
    public ulong? ULongOpt { get; set; }

    [ParquetColumn("float")]
    public float FloatValue { get; set; }

    [ParquetColumn("float_opt")]
    public float? FloatOpt { get; set; }

    [ParquetColumn("double")]
    public double DoubleValue { get; set; }

    [ParquetColumn("double_opt")]
    public double? DoubleOpt { get; set; }

    [ParquetColumn("decimal")]
    public decimal DecimalValue { get; set; }

    [ParquetColumn("decimal_opt")]
    public decimal? DecimalOpt { get; set; }

    [ParquetColumn("decimal_18_4")]
    [ParquetDecimal(18, 4)]
    public decimal DecimalNarrow { get; set; }

    [ParquetColumn("datetime")]
    public DateTime DateTime { get; set; }

    [ParquetColumn("datetime_opt")]
    public DateTime? DateTimeOpt { get; set; }

#if NET8_0_OR_GREATER
    // The legacy backend writes microsecond timestamps only on net8.0+ (a compile error below).
    [ParquetColumn("datetime_micros")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime DateTimeMicros { get; set; }
#endif

    [ParquetColumn("timespan")]
    public TimeSpan TimeSpan { get; set; }

    [ParquetColumn("timespan_opt")]
    public TimeSpan? TimeSpanOpt { get; set; }

    [ParquetColumn("guid")]
    public Guid GuidValue { get; set; }

    [ParquetColumn("guid_opt")]
    public Guid? GuidOpt { get; set; }

    [ParquetColumn("string")]
    public string StringValue { get; set; } = string.Empty;

    [ParquetColumn("string_opt")]
    public string? StringOpt { get; set; }

    [ParquetColumn("bytes")]
    public byte[] Bytes { get; set; } = [];

    [ParquetColumn("bytes_opt")]
    public byte[]? BytesOpt { get; set; }

    [ParquetColumn("enum")]
    public MatrixIntLevel Enum { get; set; }

    [ParquetColumn("enum_opt")]
    public MatrixIntLevel? EnumOpt { get; set; }

    [ParquetColumn("enum_byte")]
    public MatrixByteLevel EnumByte { get; set; }

    [ParquetColumn("enum_byte_opt")]
    public MatrixByteLevel? EnumByteOpt { get; set; }

    [ParquetColumn("enum_long")]
    public MatrixLongLevel EnumLong { get; set; }

    [ParquetColumn("enum_long_opt")]
    public MatrixLongLevel? EnumLongOpt { get; set; }

    [ParquetColumn("interval")]
    public Interval Interval { get; set; }

    [ParquetColumn("interval_opt")]
    public Interval? IntervalOpt { get; set; }

#if NET6_0_OR_GREATER
    [ParquetColumn("dateonly")]
    public DateOnly DateOnly { get; set; }

    [ParquetColumn("dateonly_opt")]
    public DateOnly? DateOnlyOpt { get; set; }

    [ParquetColumn("timeonly")]
    public TimeOnly TimeOnly { get; set; }

    [ParquetColumn("timeonly_opt")]
    public TimeOnly? TimeOnlyOpt { get; set; }
#endif
}

/// <summary>
/// The required columns of <see cref="TypeMatrixRow"/> and none of its optional ones: a file
/// written from this is the "missing optional column" cell of the matrix.
/// </summary>
[ParquetSerializable]
public sealed partial class TypeMatrixRequiredOnly
{
    [ParquetColumn("id")]
    public int Id { get; set; }

    [ParquetColumn("bool")]
    public bool Bool { get; set; }

    [ParquetColumn("byte")]
    public byte Byte { get; set; }

    [ParquetColumn("sbyte")]
    public sbyte SByte { get; set; }

    [ParquetColumn("short")]
    public short ShortValue { get; set; }

    [ParquetColumn("ushort")]
    public ushort UShortValue { get; set; }

    [ParquetColumn("int")]
    public int IntValue { get; set; }

    [ParquetColumn("uint")]
    public uint UIntValue { get; set; }

    [ParquetColumn("long")]
    public long LongValue { get; set; }

    [ParquetColumn("ulong")]
    public ulong ULongValue { get; set; }

    [ParquetColumn("float")]
    public float FloatValue { get; set; }

    [ParquetColumn("double")]
    public double DoubleValue { get; set; }

    [ParquetColumn("decimal")]
    public decimal DecimalValue { get; set; }

    [ParquetColumn("decimal_18_4")]
    [ParquetDecimal(18, 4)]
    public decimal DecimalNarrow { get; set; }

    [ParquetColumn("datetime")]
    public DateTime DateTime { get; set; }

#if NET8_0_OR_GREATER
    // The legacy backend writes microsecond timestamps only on net8.0+ (a compile error below).
    [ParquetColumn("datetime_micros")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime DateTimeMicros { get; set; }
#endif

    [ParquetColumn("timespan")]
    public TimeSpan TimeSpan { get; set; }

    [ParquetColumn("guid")]
    public Guid GuidValue { get; set; }

    [ParquetColumn("string")]
    public string StringValue { get; set; } = string.Empty;

    [ParquetColumn("bytes")]
    public byte[] Bytes { get; set; } = [];

    [ParquetColumn("enum")]
    public MatrixIntLevel Enum { get; set; }

    [ParquetColumn("enum_byte")]
    public MatrixByteLevel EnumByte { get; set; }

    [ParquetColumn("enum_long")]
    public MatrixLongLevel EnumLong { get; set; }

    [ParquetColumn("interval")]
    public Interval Interval { get; set; }

#if NET6_0_OR_GREATER
    [ParquetColumn("dateonly")]
    public DateOnly DateOnly { get; set; }

    [ParquetColumn("timeonly")]
    public TimeOnly TimeOnly { get; set; }
#endif
}

/// <summary>
/// A compressible row for the codec matrix: repetitive text, a numeric run and a payload.
/// </summary>
[ParquetSerializable]
public sealed partial class CodecRow
{
    [ParquetColumn("id")]
    public int Id { get; set; }

    [ParquetColumn("label")]
    public string Label { get; set; } = string.Empty;

    [ParquetColumn("note")]
    public string? Note { get; set; }

    [ParquetColumn("value")]
    public double Value { get; set; }

    [ParquetColumn("payload")]
    public byte[]? Payload { get; set; }
}
