using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.File.Values.Primitives;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public enum TypeMatrixStatus
{
    Pending = 0,
    Active = 1,
    Closed = 2,
}

[ParquetSerializable]
public partial record GeneratedTypeMatrixRecord
{
    [ParquetColumn("bool_value")]
    public bool BoolValue { get; init; }

    [ParquetColumn("byte_value")]
    public byte ByteValue { get; init; }

    [ParquetColumn("sbyte_value")]
    public sbyte SByteValue { get; init; }

    [ParquetColumn("short_value")]
    public short ShortValue { get; init; }

    [ParquetColumn("ushort_value")]
    public ushort UShortValue { get; init; }

    [ParquetColumn("int_value")]
    public int IntValue { get; init; }

    [ParquetColumn("uint_value")]
    public uint UIntValue { get; init; }

    [ParquetColumn("long_value")]
    public long LongValue { get; init; }

    [ParquetColumn("ulong_value")]
    public ulong ULongValue { get; init; }

    [ParquetColumn("float_value")]
    public float FloatValue { get; init; }

    [ParquetColumn("double_value")]
    public double DoubleValue { get; init; }

    [ParquetColumn("string_value")]
    public string StringValue { get; init; } = string.Empty;

    [ParquetColumn("bytes_value")]
    public byte[] BytesValue { get; init; } = Array.Empty<byte>();

    [ParquetColumn("date_only_value")]
    public DateOnly DateOnlyValue { get; init; }

    [ParquetColumn("memory_bytes_value")]
    public ReadOnlyMemory<byte> MemoryBytesValue { get; init; }

    [ParquetColumn("memory_chars_value")]
    public ReadOnlyMemory<char> MemoryCharsValue { get; init; }

    [ParquetColumn("interval_value")]
    public Interval IntervalValue { get; init; }

    [ParquetColumn("decimal_value")]
    [ParquetDecimal(18, 4)]
    public decimal DecimalValue { get; init; }

    [ParquetColumn("datetime_value")]
    public DateTime DateTimeValue { get; init; }

    [ParquetColumn("timespan_value")]
    public TimeSpan TimeSpanValue { get; init; }

    [ParquetColumn("timeonly_value")]
    public TimeOnly TimeOnlyValue { get; init; }

    [ParquetColumn("guid_value")]
    public Guid GuidValue { get; init; }

    [ParquetColumn("enum_value")]
    public TypeMatrixStatus EnumValue { get; init; }
}

[ParquetSerializable]
public partial record NullableGeneratedTypeMatrixRecord
{
    [ParquetColumn("bool_value")]
    public bool? BoolValue { get; init; }

    [ParquetColumn("byte_value")]
    public byte? ByteValue { get; init; }

    [ParquetColumn("sbyte_value")]
    public sbyte? SByteValue { get; init; }

    [ParquetColumn("short_value")]
    public short? ShortValue { get; init; }

    [ParquetColumn("ushort_value")]
    public ushort? UShortValue { get; init; }

    [ParquetColumn("int_value")]
    public int? IntValue { get; init; }

    [ParquetColumn("uint_value")]
    public uint? UIntValue { get; init; }

    [ParquetColumn("long_value")]
    public long? LongValue { get; init; }

    [ParquetColumn("ulong_value")]
    public ulong? ULongValue { get; init; }

    [ParquetColumn("float_value")]
    public float? FloatValue { get; init; }

    [ParquetColumn("double_value")]
    public double? DoubleValue { get; init; }

    [ParquetColumn("string_value")]
    public string? StringValue { get; init; }

    [ParquetColumn("bytes_value")]
    public byte[]? BytesValue { get; init; }

    [ParquetColumn("date_only_value")]
    public DateOnly? DateOnlyValue { get; init; }

    [ParquetColumn("memory_bytes_value")]
    public ReadOnlyMemory<byte>? MemoryBytesValue { get; init; }

    [ParquetColumn("memory_chars_value")]
    public ReadOnlyMemory<char>? MemoryCharsValue { get; init; }

    [ParquetColumn("interval_value")]
    public Interval? IntervalValue { get; init; }

    [ParquetColumn("decimal_value")]
    [ParquetDecimal(18, 4)]
    public decimal? DecimalValue { get; init; }

    [ParquetColumn("datetime_value")]
    public DateTime? DateTimeValue { get; init; }

    [ParquetColumn("timespan_value")]
    public TimeSpan? TimeSpanValue { get; init; }

    [ParquetColumn("timeonly_value")]
    public TimeOnly? TimeOnlyValue { get; init; }

    [ParquetColumn("guid_value")]
    public Guid? GuidValue { get; init; }

    [ParquetColumn("enum_value")]
    public TypeMatrixStatus? EnumValue { get; init; }
}

public sealed class GeneratedTypeMatrixTests
{
    private static readonly ParquetCompressionMethod[] CompressionMethods =
    [
        ParquetCompressionMethod.None,
        ParquetCompressionMethod.Snappy,
        ParquetCompressionMethod.Gzip,
        ParquetCompressionMethod.Lz4,
        ParquetCompressionMethod.Brotli,
        ParquetCompressionMethod.Zstd,
    ];

    [Theory]
    [MemberData(nameof(AllCompressionMethods))]
    public async Task RequiredTypeMatrixRoundtripsAcrossAllCompressionMethods(
        ParquetCompressionMethod compressionMethod
    )
    {
        List<GeneratedTypeMatrixRecord> expected = RequiredRows();
        byte[] bytes = await WriteRequiredAsync(expected, compressionMethod, rowGroupSize: 2);

        List<GeneratedTypeMatrixRecord> sequential =
            await GeneratedTypeMatrixRecordParquetExtensions.ReadParquetAsync(
                new MemoryStream(bytes)
            );
        GeneratedTypeMatrixRecord[] array =
            await GeneratedTypeMatrixRecordParquetExtensions.ReadParquetArrayAsync(
                new MemoryStream(bytes)
            );
        List<GeneratedTypeMatrixRecord> parallel =
            await GeneratedTypeMatrixRecordParquetExtensions.ReadParquetParallelAsync(
                bytes,
                new ParquetSerializerOptions { MaxDegreeOfParallelism = 2 }
            );
        GeneratedTypeMatrixRecord[] parallelArray =
            await GeneratedTypeMatrixRecordParquetExtensions.ReadParquetParallelArrayAsync(
                bytes,
                new ParquetSerializerOptions { MaxDegreeOfParallelism = 2 }
            );

        var streamed = new List<GeneratedTypeMatrixRecord>();
        await foreach (
            GeneratedTypeMatrixRecord row in GeneratedTypeMatrixRecordParquetExtensions.ReadParquetStreamAsync(
                new MemoryStream(bytes)
            )
        )
        {
            streamed.Add(row);
        }

        AssertEquivalent(expected, sequential);
        AssertEquivalent(expected, array);
        AssertEquivalent(expected, parallel);
        AssertEquivalent(expected, parallelArray);
        AssertEquivalent(expected, streamed);
    }

    [Fact]
    public async Task NullableTypeMatrixCoversAllNullMixedAndNonNullRows()
    {
        List<NullableGeneratedTypeMatrixRecord> expected =
        [
            new(),
            NullableRow(1),
            new NullableGeneratedTypeMatrixRecord
            {
                BoolValue = false,
                ByteValue = 0,
                SByteValue = 0,
                ShortValue = 0,
                UShortValue = 0,
                IntValue = 0,
                UIntValue = 0,
                LongValue = 0,
                ULongValue = 0,
                FloatValue = 0,
                DoubleValue = 0,
                StringValue = string.Empty,
                BytesValue = Array.Empty<byte>(),
                DateOnlyValue = DateOnly.MinValue,
                MemoryBytesValue = ReadOnlyMemory<byte>.Empty,
                MemoryCharsValue = ReadOnlyMemory<char>.Empty,
                IntervalValue = new Interval(0, 0, 0),
                DecimalValue = 0,
                DateTimeValue = DateTime.MinValue,
                TimeSpanValue = TimeSpan.Zero,
                TimeOnlyValue = TimeOnly.MinValue,
                GuidValue = Guid.Empty,
                EnumValue = TypeMatrixStatus.Pending,
            },
        ];
        byte[] bytes = await WriteNullableAsync(
            expected,
            ParquetCompressionMethod.Snappy,
            rowGroupSize: 1
        );

        List<NullableGeneratedTypeMatrixRecord> actual =
            await NullableGeneratedTypeMatrixRecordParquetExtensions.ReadParquetAsync(
                new MemoryStream(bytes)
            );
        List<NullableGeneratedTypeMatrixRecord> parallel =
            await NullableGeneratedTypeMatrixRecordParquetExtensions.ReadParquetParallelAsync(
                bytes,
                new ParquetSerializerOptions { MaxDegreeOfParallelism = 3 }
            );

        AssertEquivalent(expected, actual);
        AssertEquivalent(expected, parallel);
    }

    public static IEnumerable<object[]> AllCompressionMethods() =>
        CompressionMethods.Select(method => new object[] { method });

    private static async Task<byte[]> WriteRequiredAsync(
        IReadOnlyList<GeneratedTypeMatrixRecord> rows,
        ParquetCompressionMethod compressionMethod,
        int rowGroupSize
    )
    {
        using var stream = new MemoryStream();
        await ((IEnumerable<GeneratedTypeMatrixRecord>)rows).WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions
            {
                CompressionMethod = compressionMethod,
                RowGroupSize = rowGroupSize,
            }
        );
        return stream.ToArray();
    }

    private static async Task<byte[]> WriteNullableAsync(
        IReadOnlyList<NullableGeneratedTypeMatrixRecord> rows,
        ParquetCompressionMethod compressionMethod,
        int rowGroupSize
    )
    {
        using var stream = new MemoryStream();
        await ((IEnumerable<NullableGeneratedTypeMatrixRecord>)rows).WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions
            {
                CompressionMethod = compressionMethod,
                RowGroupSize = rowGroupSize,
            }
        );
        return stream.ToArray();
    }

    private static List<GeneratedTypeMatrixRecord> RequiredRows() =>
        [
            new()
            {
                BoolValue = true,
                ByteValue = byte.MaxValue,
                SByteValue = sbyte.MinValue,
                ShortValue = short.MinValue,
                UShortValue = ushort.MaxValue,
                IntValue = int.MinValue,
                UIntValue = uint.MaxValue,
                LongValue = long.MinValue,
                ULongValue = ulong.MaxValue,
                FloatValue = -123.5f,
                DoubleValue = double.MaxValue / 2,
                StringValue = string.Empty,
                BytesValue = Array.Empty<byte>(),
                DateOnlyValue = new DateOnly(2024, 1, 2),
                MemoryBytesValue = new byte[] { 1, 2, 3 },
                MemoryCharsValue = "first".AsMemory(),
                IntervalValue = new Interval(1, 2, 3),
                DecimalValue = 12345678901234.5678m,
                DateTimeValue = new DateTime(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc),
                TimeSpanValue = TimeSpan.FromMilliseconds(123456),
                TimeOnlyValue = new TimeOnly(3, 4, 5, 678),
                GuidValue = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                EnumValue = TypeMatrixStatus.Active,
            },
            new()
            {
                BoolValue = false,
                ByteValue = 0,
                SByteValue = sbyte.MaxValue,
                ShortValue = short.MaxValue,
                UShortValue = 0,
                IntValue = int.MaxValue,
                UIntValue = 0,
                LongValue = long.MaxValue,
                ULongValue = 0,
                FloatValue = float.MaxValue / 2,
                DoubleValue = double.MinValue / 2,
                StringValue = "unicode: cafe\u0301 \u65e5\u672c",
                BytesValue = new byte[] { 0, 1, 255 },
                DateOnlyValue = DateOnly.MaxValue,
                MemoryBytesValue = new byte[] { 255, 254 },
                MemoryCharsValue = "second".AsMemory(),
                IntervalValue = new Interval(2, 3, 4),
                DecimalValue = -0.0001m,
                DateTimeValue = new DateTime(2025, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
                TimeSpanValue = TimeSpan.Zero,
                TimeOnlyValue = TimeOnly.MaxValue,
                GuidValue = Guid.Empty,
                EnumValue = TypeMatrixStatus.Closed,
            },
            new()
            {
                BoolValue = true,
                ByteValue = 1,
                SByteValue = -1,
                ShortValue = 1,
                UShortValue = 1,
                IntValue = 1,
                UIntValue = 1,
                LongValue = 1,
                ULongValue = 1,
                FloatValue = 0.5f,
                DoubleValue = -0.5,
                StringValue = "repeated",
                BytesValue = new byte[] { 42 },
                DateOnlyValue = new DateOnly(2000, 1, 1),
                MemoryBytesValue = ReadOnlyMemory<byte>.Empty,
                MemoryCharsValue = ReadOnlyMemory<char>.Empty,
                IntervalValue = new Interval(0, 0, 0),
                DecimalValue = 1.0000m,
                DateTimeValue = DateTime.UnixEpoch,
                TimeSpanValue = TimeSpan.FromMilliseconds(1),
                TimeOnlyValue = TimeOnly.MinValue,
                GuidValue = Guid.NewGuid(),
                EnumValue = TypeMatrixStatus.Pending,
            },
        ];

    private static NullableGeneratedTypeMatrixRecord NullableRow(int seed) =>
        new()
        {
            BoolValue = seed % 2 == 0 ? true : null,
            ByteValue = (byte)seed,
            SByteValue = (sbyte)-seed,
            ShortValue = (short)seed,
            UShortValue = (ushort)seed,
            IntValue = seed,
            UIntValue = (uint)seed,
            LongValue = seed,
            ULongValue = (ulong)seed,
            FloatValue = seed + 0.5f,
            DoubleValue = seed + 0.25,
            StringValue = $"row_{seed}",
            BytesValue = new byte[] { (byte)seed },
            DateOnlyValue = new DateOnly(2024, 1, seed + 1),
            MemoryBytesValue = new byte[] { (byte)seed },
            MemoryCharsValue = $"row_{seed}".AsMemory(),
            IntervalValue = new Interval(seed, seed + 1, seed + 2),
            DecimalValue = seed + 0.0001m,
            DateTimeValue = new DateTime(2024, 1, seed + 1, 0, 0, 0, DateTimeKind.Utc),
            TimeSpanValue = TimeSpan.FromSeconds(seed),
            TimeOnlyValue = new TimeOnly(seed, 0),
            GuidValue = new Guid(seed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1),
            EnumValue = TypeMatrixStatus.Active,
        };

    private static void AssertEquivalent<T>(IEnumerable<T> expected, IEnumerable<T> actual) =>
        ParquetCompatibilityOracle.AssertEquivalent(
            expected,
            actual,
            new CompatibilityComparisonOptions
            {
                TimestampPrecision = TimeSpan.FromMicroseconds(1),
                FloatingPointTolerance = 0,
            }
        );
}
