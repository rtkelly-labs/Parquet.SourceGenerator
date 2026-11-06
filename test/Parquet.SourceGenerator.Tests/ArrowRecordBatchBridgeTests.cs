using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using Parquet;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// ── Arrow bridge test models ───────────────────────────────────────────────

[ParquetSerializable]
public partial record ArrowPrimitiveRow
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("sequence")]
    public long Sequence { get; init; }

    [ParquetColumn("small")]
    public short Small { get; init; }

    [ParquetColumn("delta")]
    public sbyte Delta { get; init; }

    [ParquetColumn("tiny")]
    public byte Tiny { get; init; }

    [ParquetColumn("usmall")]
    public ushort USmall { get; init; }

    [ParquetColumn("umedium")]
    public uint UMedium { get; init; }

    [ParquetColumn("ularge")]
    public ulong ULarge { get; init; }

    [ParquetColumn("ratio")]
    public float Ratio { get; init; }

    [ParquetColumn("amount")]
    public double Amount { get; init; }

    [ParquetColumn("active")]
    public bool Active { get; init; }

    [ParquetColumn("name")]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("payload")]
    public byte[] Payload { get; init; } = System.Array.Empty<byte>();

    [ParquetColumn("price")]
    [ParquetDecimal(18, 4)]
    public decimal Price { get; init; }

    [ParquetColumn("created_at")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime CreatedAt { get; init; }

    [ParquetColumn("day")]
    public DateOnly Day { get; init; }

    [ParquetColumn("at")]
    public TimeOnly At { get; init; }

    [ParquetColumn("elapsed")]
    public TimeSpan Elapsed { get; init; }

    [ParquetColumn("correlation_id")]
    public Guid CorrelationId { get; init; }
}

[ParquetSerializable]
public partial record ArrowNullableRow
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("score")]
    public int? Score { get; init; }

    [ParquetColumn("label")]
    public string? Label { get; init; }

    [ParquetColumn("blob")]
    public byte[]? Blob { get; init; }

    [ParquetColumn("moment")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime? Moment { get; init; }
}

/// <summary>
/// End-to-end coverage for the conditionally-emitted Apache Arrow ingestion bridge (#177):
/// RecordBatch → generated writer → POCO reader round-trips, strict schema validation, definition
/// levels derived from the Arrow validity bitmap, and the zero-copy handoff for fixed-width
/// columns.
/// </summary>
public sealed class ArrowRecordBatchBridgeTests
{
    private static readonly DateTime Epoch = new DateTime(
        1970,
        1,
        1,
        0,
        0,
        0,
        DateTimeKind.Unspecified
    );

    // Shared single-row fixtures. Hoisted out of the call sites so the analyzers do not see a
    // constant array allocated per invocation (CA1861).
    private static readonly int[] OneInt = { 1 };
    private static readonly int[] ZeroInt = { 0 };
    private static readonly int[] ThreeIds = { 1, 2, 3 };
    private static readonly long[] OneLong = { 1L };
    private static readonly long[] ZeroLong = { 0L };
    private static readonly bool[] OneInvalid = { false };
    private static readonly string?[] OneLabel = { "a" };
    private static readonly byte[]?[] OneBlob = { new byte[] { 1 } };

    // ── Arrow construction helpers ─────────────────────────────────────────

    private static ArrowBuffer Values<T>(T[] values)
        where T : struct
    {
        return new ArrowBuffer.Builder<T>(Math.Max(values.Length, 1))
            .Append(values.AsSpan())
            .Build();
    }

    private static ArrowBuffer Validity(bool[]? valid)
    {
        if (valid is null)
        {
            return ArrowBuffer.Empty;
        }

        var builder = new ArrowBuffer.BitmapBuilder(Math.Max(valid.Length, 1));
        foreach (bool v in valid)
        {
            builder.Append(v);
        }

        return builder.Build();
    }

    private static ArrayData PrimitiveData<T>(IArrowType type, T[] values, bool[]? valid = null)
        where T : struct
    {
        int nullCount = valid is null ? 0 : valid.Count(v => !v);
        return new ArrayData(
            type,
            values.Length,
            nullCount,
            0,
            new[] { Validity(valid), Values(values) },
            (ArrayData[]?)null
        );
    }

    private static FixedSizeBinaryArray GuidArray(Guid[] guids, bool[]? valid = null)
    {
        byte[] payload = new byte[guids.Length * 16];
        for (int i = 0; i < guids.Length; i++)
        {
            GuidToBigEndian(guids[i]).CopyTo(payload, i * 16);
        }

        int nullCount = valid is null ? 0 : valid.Count(v => !v);
        var data = new ArrayData(
            new FixedSizeBinaryType(16),
            guids.Length,
            nullCount,
            0,
            new[] { Validity(valid), Values(payload) },
            (ArrayData[]?)null
        );
        return new FixedSizeBinaryArray(data);
    }

    /// <summary>RFC 4122 byte order — the layout the emitted bridge reads back.</summary>
    internal static byte[] GuidToBigEndian(Guid value)
    {
        byte[] little = value.ToByteArray();
        return new[]
        {
            little[3],
            little[2],
            little[1],
            little[0],
            little[5],
            little[4],
            little[7],
            little[6],
            little[8],
            little[9],
            little[10],
            little[11],
            little[12],
            little[13],
            little[14],
            little[15],
        };
    }

    private static StringArray Strings(IEnumerable<string?> values)
    {
        var builder = new StringArray.Builder();
        foreach (string? value in values)
        {
            if (value is null)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(value);
            }
        }

        return builder.Build();
    }

    private static BinaryArray Binaries(IEnumerable<byte[]?> values)
    {
        var builder = new BinaryArray.Builder();
        foreach (byte[]? value in values)
        {
            if (value is null)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(value.AsSpan());
            }
        }

        return builder.Build();
    }

    private static long ToMicros(DateTime value) => (value - Epoch).Ticks / 10L;

    // ── Canonical fixture ──────────────────────────────────────────────────

    private static readonly Guid[] FixtureGuids =
    {
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"),
    };

    private static List<ArrowPrimitiveRow> ExpectedPrimitiveRows()
    {
        return new List<ArrowPrimitiveRow>
        {
            new ArrowPrimitiveRow
            {
                Id = 1,
                Sequence = 10_000_000_000L,
                Small = -12,
                Delta = -100,
                Tiny = 200,
                USmall = 60_000,
                UMedium = 4_000_000_000u,
                ULarge = 18_000_000_000_000_000_000ul,
                Ratio = 1.5f,
                Amount = 2.25d,
                Active = true,
                Name = "alpha",
                Payload = new byte[] { 1, 2, 3 },
                Price = 1234.5678m,
                CreatedAt = new DateTime(2024, 3, 1, 12, 30, 15, DateTimeKind.Unspecified).AddTicks(
                    1230
                ),
                Day = new DateOnly(2024, 3, 1),
                At = new TimeOnly(1, 2, 3),
                Elapsed = TimeSpan.FromMilliseconds(1500),
                CorrelationId = FixtureGuids[0],
            },
            new ArrowPrimitiveRow
            {
                Id = 2,
                Sequence = -5L,
                Small = 12,
                Delta = 100,
                Tiny = 0,
                USmall = 0,
                UMedium = 0u,
                ULarge = 0ul,
                Ratio = -0.5f,
                Amount = -0.125d,
                Active = false,
                Name = "βeta — unicode",
                Payload = System.Array.Empty<byte>(),
                Price = -0.0001m,
                CreatedAt = new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified),
                Day = new DateOnly(1970, 1, 1),
                At = new TimeOnly(0, 0, 0),
                Elapsed = TimeSpan.Zero,
                CorrelationId = FixtureGuids[1],
            },
            new ArrowPrimitiveRow
            {
                Id = 3,
                Sequence = 42L,
                Small = short.MaxValue,
                Delta = sbyte.MinValue,
                Tiny = byte.MaxValue,
                USmall = ushort.MaxValue,
                UMedium = uint.MaxValue,
                ULarge = ulong.MaxValue,
                Ratio = float.Epsilon,
                Amount = double.MaxValue,
                Active = true,
                Name = "",
                Payload = new byte[] { 255, 0, 128, 64 },
                Price = 0m,
                CreatedAt = new DateTime(2030, 6, 15, 6, 0, 0, DateTimeKind.Unspecified),
                Day = new DateOnly(2030, 6, 15),
                At = new TimeOnly(23, 59, 59),
                Elapsed = TimeSpan.FromMilliseconds(-250),
                CorrelationId = FixtureGuids[2],
            },
        };
    }

    private static RecordBatch BuildPrimitiveBatch()
    {
        List<ArrowPrimitiveRow> rows = ExpectedPrimitiveRows();
        int n = rows.Count;

        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("sequence").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("small").DataType(Int16Type.Default).Nullable(false))
            .Field(f => f.Name("delta").DataType(Int8Type.Default).Nullable(false))
            .Field(f => f.Name("tiny").DataType(UInt8Type.Default).Nullable(false))
            .Field(f => f.Name("usmall").DataType(UInt16Type.Default).Nullable(false))
            .Field(f => f.Name("umedium").DataType(UInt32Type.Default).Nullable(false))
            .Field(f => f.Name("ularge").DataType(UInt64Type.Default).Nullable(false))
            .Field(f => f.Name("ratio").DataType(FloatType.Default).Nullable(false))
            .Field(f => f.Name("amount").DataType(DoubleType.Default).Nullable(false))
            .Field(f => f.Name("active").DataType(BooleanType.Default).Nullable(false))
            .Field(f => f.Name("name").DataType(StringType.Default).Nullable(false))
            .Field(f => f.Name("payload").DataType(BinaryType.Default).Nullable(false))
            .Field(f => f.Name("price").DataType(new Decimal128Type(18, 4)).Nullable(false))
            .Field(f =>
                f.Name("created_at")
                    .DataType(new TimestampType(TimeUnit.Microsecond, (string?)null))
                    .Nullable(false)
            )
            .Field(f => f.Name("day").DataType(Date32Type.Default).Nullable(false))
            .Field(f => f.Name("at").DataType(new Time64Type(TimeUnit.Microsecond)).Nullable(false))
            .Field(f => f.Name("elapsed").DataType(DurationType.Millisecond).Nullable(false))
            .Field(f =>
                f.Name("correlation_id").DataType(new FixedSizeBinaryType(16)).Nullable(false)
            )
            .Build();

        var decimalBuilder = new Decimal128Array.Builder(new Decimal128Type(18, 4));
        foreach (ArrowPrimitiveRow row in rows)
        {
            decimalBuilder.Append(row.Price);
        }

        var booleanBuilder = new BooleanArray.Builder();
        foreach (ArrowPrimitiveRow row in rows)
        {
            booleanBuilder.Append(row.Active);
        }

        var arrays = new IArrowArray[]
        {
            new Int32Array(PrimitiveData(Int32Type.Default, rows.Select(r => r.Id).ToArray())),
            new Int64Array(
                PrimitiveData(Int64Type.Default, rows.Select(r => r.Sequence).ToArray())
            ),
            new Int16Array(PrimitiveData(Int16Type.Default, rows.Select(r => r.Small).ToArray())),
            new Int8Array(PrimitiveData(Int8Type.Default, rows.Select(r => r.Delta).ToArray())),
            new UInt8Array(PrimitiveData(UInt8Type.Default, rows.Select(r => r.Tiny).ToArray())),
            new UInt16Array(
                PrimitiveData(UInt16Type.Default, rows.Select(r => r.USmall).ToArray())
            ),
            new UInt32Array(
                PrimitiveData(UInt32Type.Default, rows.Select(r => r.UMedium).ToArray())
            ),
            new UInt64Array(
                PrimitiveData(UInt64Type.Default, rows.Select(r => r.ULarge).ToArray())
            ),
            new FloatArray(PrimitiveData(FloatType.Default, rows.Select(r => r.Ratio).ToArray())),
            new DoubleArray(
                PrimitiveData(DoubleType.Default, rows.Select(r => r.Amount).ToArray())
            ),
            booleanBuilder.Build(),
            Strings(rows.Select(r => (string?)r.Name)),
            Binaries(rows.Select(r => (byte[]?)r.Payload)),
            decimalBuilder.Build(),
            new TimestampArray(
                PrimitiveData(
                    new TimestampType(TimeUnit.Microsecond, (string?)null),
                    rows.Select(r => ToMicros(r.CreatedAt)).ToArray()
                )
            ),
            new Date32Array(
                PrimitiveData(
                    Date32Type.Default,
                    rows.Select(r => r.Day.DayNumber - new DateOnly(1970, 1, 1).DayNumber).ToArray()
                )
            ),
            new Time64Array(
                PrimitiveData(
                    new Time64Type(TimeUnit.Microsecond),
                    rows.Select(r => r.At.Ticks / 10L).ToArray()
                )
            ),
            new DurationArray(
                PrimitiveData(
                    DurationType.Millisecond,
                    rows.Select(r => (long)r.Elapsed.TotalMilliseconds).ToArray()
                )
            ),
            GuidArray(rows.Select(r => r.CorrelationId).ToArray()),
        };

        return new RecordBatch(schema, arrays, n);
    }

    private static async Task<byte[]> WriteArrowAsync(RecordBatch batch)
    {
        using var stream = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(
                ArrowPrimitiveRowParquetExtensions.Schema,
                stream
            )
        )
        {
            await ArrowPrimitiveRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch);
        }

        return stream.ToArray();
    }

    // ── Round trip ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ArrowBatchRoundTripsThroughThePocoReaderForEverySupportedKind()
    {
        using RecordBatch batch = BuildPrimitiveBatch();
        byte[] bytes = await WriteArrowAsync(batch);

        using var readStream = new MemoryStream(bytes);
        List<ArrowPrimitiveRow> actual = await ArrowPrimitiveRowParquetExtensions.ReadParquetAsync(
            readStream
        );

        List<ArrowPrimitiveRow> expected = ExpectedPrimitiveRows();
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            ArrowPrimitiveRow e = expected[i];
            ArrowPrimitiveRow a = actual[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Sequence, a.Sequence);
            Assert.Equal(e.Small, a.Small);
            Assert.Equal(e.Delta, a.Delta);
            Assert.Equal(e.Tiny, a.Tiny);
            Assert.Equal(e.USmall, a.USmall);
            Assert.Equal(e.UMedium, a.UMedium);
            Assert.Equal(e.ULarge, a.ULarge);
            Assert.Equal(e.Ratio, a.Ratio);
            Assert.Equal(e.Amount, a.Amount);
            Assert.Equal(e.Active, a.Active);
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Payload, a.Payload);
            Assert.Equal(e.Price, a.Price);
            Assert.Equal(e.CreatedAt.Ticks, a.CreatedAt.Ticks);
            Assert.Equal(e.Day, a.Day);
            Assert.Equal(e.At, a.At);
            Assert.Equal(e.Elapsed, a.Elapsed);
            Assert.Equal(e.CorrelationId, a.CorrelationId);
        }
    }

    // ── Definition levels from the validity bitmap ─────────────────────────

    private static List<ArrowNullableRow> NullableRows()
    {
        return new List<ArrowNullableRow>
        {
            new ArrowNullableRow
            {
                Id = 1,
                Score = 10,
                Label = "one",
                Blob = new byte[] { 9 },
                Moment = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            },
            new ArrowNullableRow
            {
                Id = 2,
                Score = null,
                Label = null,
                Blob = null,
                Moment = null,
            },
            new ArrowNullableRow
            {
                Id = 3,
                Score = 30,
                Label = "three",
                Blob = new byte[] { 1, 2 },
                Moment = new DateTime(2021, 5, 5, 5, 5, 5, DateTimeKind.Unspecified),
            },
        };
    }

    private static RecordBatch BuildNullableBatch()
    {
        List<ArrowNullableRow> rows = NullableRows();
        bool[] valid = rows.Select(r => r.Score is not null).ToArray();

        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("score").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("label").DataType(StringType.Default).Nullable(true))
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Field(f =>
                f.Name("moment")
                    .DataType(new TimestampType(TimeUnit.Microsecond, (string?)null))
                    .Nullable(true)
            )
            .Build();

        var arrays = new IArrowArray[]
        {
            new Int32Array(PrimitiveData(Int32Type.Default, rows.Select(r => r.Id).ToArray())),
            new Int32Array(
                PrimitiveData(Int32Type.Default, rows.Select(r => r.Score ?? 0).ToArray(), valid)
            ),
            Strings(rows.Select(r => r.Label)),
            Binaries(rows.Select(r => r.Blob)),
            new TimestampArray(
                PrimitiveData(
                    new TimestampType(TimeUnit.Microsecond, (string?)null),
                    rows.Select(r => r.Moment is null ? 0L : ToMicros(r.Moment.Value)).ToArray(),
                    rows.Select(r => r.Moment is not null).ToArray()
                )
            ),
        };

        return new RecordBatch(schema, arrays, rows.Count);
    }

    [Fact]
    public async Task ValidityBitmapProducesByteIdenticalFilesToThePocoWritePath()
    {
        using RecordBatch batch = BuildNullableBatch();

        using var arrowStream = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(
                ArrowNullableRowParquetExtensions.Schema,
                arrowStream
            )
        )
        {
            await ArrowNullableRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch);
        }

        using var pocoStream = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(
                ArrowNullableRowParquetExtensions.Schema,
                pocoStream
            )
        )
        {
            await writer.WriteParquetRowGroupAsync(NullableRows());
        }

        Assert.Equal(Sha256(pocoStream.ToArray()), Sha256(arrowStream.ToArray()));
    }

    [Fact]
    public async Task NullableArrowColumnsRoundTripWithTheirNulls()
    {
        using RecordBatch batch = BuildNullableBatch();

        using var stream = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(
                ArrowNullableRowParquetExtensions.Schema,
                stream
            )
        )
        {
            await ArrowNullableRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch);
        }

        using var readStream = new MemoryStream(stream.ToArray());
        List<ArrowNullableRow> actual = await ArrowNullableRowParquetExtensions.ReadParquetAsync(
            readStream
        );

        List<ArrowNullableRow> expected = NullableRows();
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            // Record equality would compare Blob by reference, so the columns are checked one by one.
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.Equal(expected[i].Score, actual[i].Score);
            Assert.Equal(expected[i].Label, actual[i].Label);
            Assert.Equal(expected[i].Blob, actual[i].Blob);
            Assert.Equal(expected[i].Moment, actual[i].Moment);
        }
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ── Strict schema validation ───────────────────────────────────────────

    private static async Task<InvalidDataException> WriteNullableAndCaptureAsync(RecordBatch batch)
    {
        using var stream = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(
            ArrowNullableRowParquetExtensions.Schema,
            stream
        );
        return await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ArrowNullableRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch)
        );
    }

    [Fact]
    public async Task MissingColumnFailsValidationWithTheFieldName()
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[] { new Int32Array(PrimitiveData(Int32Type.Default, ThreeIds)) },
            3
        );

        InvalidDataException error = await WriteNullableAndCaptureAsync(batch);

        Assert.Contains("score: column is missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("label: column is missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeMismatchFailsValidationWithBothTypes()
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("score").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("label").DataType(StringType.Default).Nullable(true))
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Field(f =>
                f.Name("moment")
                    .DataType(new TimestampType(TimeUnit.Microsecond, (string?)null))
                    .Nullable(true)
            )
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[]
            {
                new Int64Array(PrimitiveData(Int64Type.Default, OneLong)),
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                Strings(OneLabel),
                Binaries(OneBlob),
                new TimestampArray(
                    PrimitiveData(new TimestampType(TimeUnit.Microsecond, (string?)null), ZeroLong)
                ),
            },
            1
        );

        InvalidDataException error = await WriteNullableAndCaptureAsync(batch);

        Assert.Contains(
            "id: expected Arrow Int32, found int64",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task TimestampUnitMismatchFailsValidation()
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("score").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("label").DataType(StringType.Default).Nullable(true))
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Field(f =>
                f.Name("moment")
                    .DataType(new TimestampType(TimeUnit.Millisecond, (string?)null))
                    .Nullable(true)
            )
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[]
            {
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                Strings(OneLabel),
                Binaries(OneBlob),
                new TimestampArray(
                    PrimitiveData(new TimestampType(TimeUnit.Millisecond, (string?)null), ZeroLong)
                ),
            },
            1
        );

        InvalidDataException error = await WriteNullableAndCaptureAsync(batch);

        Assert.Contains("moment: timestamp unit mismatch", error.Message, StringComparison.Ordinal);
        Assert.Contains("Microsecond", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeUtf8IsRejected()
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("score").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("label").DataType(LargeStringType.Default).Nullable(true))
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Field(f =>
                f.Name("moment")
                    .DataType(new TimestampType(TimeUnit.Microsecond, (string?)null))
                    .Nullable(true)
            )
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[]
            {
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                Strings(OneLabel),
                Binaries(OneBlob),
                new TimestampArray(
                    PrimitiveData(new TimestampType(TimeUnit.Microsecond, (string?)null), ZeroLong)
                ),
            },
            1
        );

        InvalidDataException error = await WriteNullableAndCaptureAsync(batch);

        Assert.Contains("label: expected Arrow Utf8", error.Message, StringComparison.Ordinal);
        Assert.Contains("large_utf8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DictionaryEncodedColumnIsRejected()
    {
        var dictionaryType = new DictionaryType(Int32Type.Default, StringType.Default, false);
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("score").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("label").DataType(dictionaryType).Nullable(true))
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Field(f =>
                f.Name("moment")
                    .DataType(new TimestampType(TimeUnit.Microsecond, (string?)null))
                    .Nullable(true)
            )
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[]
            {
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                new DictionaryArray(
                    dictionaryType,
                    new Int32Array(PrimitiveData(Int32Type.Default, ZeroInt)),
                    Strings(OneLabel)
                ),
                Binaries(OneBlob),
                new TimestampArray(
                    PrimitiveData(new TimestampType(TimeUnit.Microsecond, (string?)null), ZeroLong)
                ),
            },
            1
        );

        InvalidDataException error = await WriteNullableAndCaptureAsync(batch);

        Assert.Contains(
            "label: dictionary-encoded Arrow columns are not supported",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task NullsInARequiredColumnFailValidation()
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("score").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("label").DataType(StringType.Default).Nullable(true))
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Field(f =>
                f.Name("moment")
                    .DataType(new TimestampType(TimeUnit.Microsecond, (string?)null))
                    .Nullable(true)
            )
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[]
            {
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt, OneInvalid)),
                new Int32Array(PrimitiveData(Int32Type.Default, OneInt)),
                Strings(OneLabel),
                Binaries(OneBlob),
                new TimestampArray(
                    PrimitiveData(new TimestampType(TimeUnit.Microsecond, (string?)null), ZeroLong)
                ),
            },
            1
        );

        InvalidDataException error = await WriteNullableAndCaptureAsync(batch);

        Assert.Contains(
            "id: the Parquet column is required but the Arrow column carries 1 null(s)",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task EmptyBatchWritesNothingAndDoesNotThrow()
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("score").DataType(Int32Type.Default).Nullable(true))
            .Field(f => f.Name("label").DataType(StringType.Default).Nullable(true))
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Field(f =>
                f.Name("moment")
                    .DataType(new TimestampType(TimeUnit.Microsecond, (string?)null))
                    .Nullable(true)
            )
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[]
            {
                new Int32Array(PrimitiveData(Int32Type.Default, System.Array.Empty<int>())),
                new Int32Array(PrimitiveData(Int32Type.Default, System.Array.Empty<int>())),
                Strings(System.Array.Empty<string?>()),
                Binaries(System.Array.Empty<byte[]?>()),
                new TimestampArray(
                    PrimitiveData(
                        new TimestampType(TimeUnit.Microsecond, (string?)null),
                        System.Array.Empty<long>()
                    )
                ),
            },
            0
        );

        using var stream = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(
                ArrowNullableRowParquetExtensions.Schema,
                stream
            )
        )
        {
            await ArrowNullableRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch);
        }

        using var readStream = new MemoryStream(stream.ToArray());
        List<ArrowNullableRow> rows = await ArrowNullableRowParquetExtensions.ReadParquetAsync(
            readStream
        );
        Assert.Empty(rows);
    }
}
