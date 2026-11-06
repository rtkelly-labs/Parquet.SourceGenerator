using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// ── Full Type Matrix Struct Definitions ───────────────────────────────────

[ParquetSerializable]
public readonly partial record struct BlittableInt8Struct
{
    [ParquetColumn("val")]
    public sbyte Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableUInt8Struct
{
    [ParquetColumn("val")]
    public byte Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableInt16Struct
{
    [ParquetColumn("val")]
    public short Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableUInt16Struct
{
    [ParquetColumn("val")]
    public ushort Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableInt32Struct
{
    [ParquetColumn("val")]
    public int Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableUInt32Struct
{
    [ParquetColumn("val")]
    public uint Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableInt64Struct
{
    [ParquetColumn("val")]
    public long Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableUInt64Struct
{
    [ParquetColumn("val")]
    public ulong Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableFloatStruct
{
    [ParquetColumn("val")]
    public float Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableDoubleStruct
{
    [ParquetColumn("val")]
    public double Value { get; init; }
}

[ParquetSerializable]
public readonly partial record struct BlittableBoolStruct
{
    [ParquetColumn("val")]
    public bool Value { get; init; }
}

// ── Layout Anomaly Structs (Soundness verification) ───────────────────────

[ParquetSerializable]
public partial struct StructWithIgnoredField
{
    [ParquetColumn("val")]
    public int Value { get; set; }

    [ParquetIgnore]
    public int Extra { get; set; }
}

[ParquetSerializable]
public partial struct StructWithPrivateField
{
    [ParquetColumn("val")]
    public int Value { get; set; }

    private int _padding;

    public void SetPadding(int p) => _padding = p;

    public int GetPadding() => _padding;
}

// ── Property Test Suite ────────────────────────────────────────────────────

public sealed class BlittableStructPropertyTests
{
    private static readonly int[] TestRowCounts = [0, 1, 2, 7, 16, 31, 64, 127, 1_000];

    public static readonly TheoryData<int[]> MultiRowGroupPartitions = new()
    {
        new[] { 10, 10, 10 },
        new[] { 1, 1, 1, 1, 1 },
        new[] { 0, 50, 0, 100 },
        new[] { 3, 7, 13, 29, 61 },
        new[] { 500, 250, 125, 62 },
    };

    // ── Integral Primitives ────────────────────────────────────────────────

    [Theory]
    [InlineData(42)]
    [InlineData(1337)]
    public async Task Int32PropertySweepAllApisEquivalent(int seed)
    {
        var rng = new Random(seed);

        foreach (int count in TestRowCounts)
        {
            var items = new BlittableInt32Struct[count];
            for (int i = 0; i < count; i++)
            {
                int val = i switch
                {
                    0 => 0,
                    1 => -1,
                    2 => 1,
                    3 => int.MinValue,
                    4 => int.MaxValue,
                    _ => rng.Next(int.MinValue, int.MaxValue),
                };
                items[i] = new BlittableInt32Struct { Value = val };
            }

            using var ms = new MemoryStream();
            await items.WriteParquetAsync(ms);
            byte[] bytes = ms.ToArray();

            // 1. Sequential Stream List Read
            ms.Position = 0;
            var seqList = await BlittableInt32StructParquet.From(ms).ToListAsync();
            seqList.Count.ShouldBe(count);
            seqList.ShouldBe(items);

            // 2. Sequential Stream Array Read
            ms.Position = 0;
            var seqArray = await BlittableInt32StructParquet.From(ms).ToArrayAsync();
            seqArray.ShouldBe(items);

            // 3. Sequential Bytes Array Read
            var bytesArray = await BlittableInt32StructParquet.From(bytes).ToArrayAsync();
            bytesArray.ShouldBe(items);

            // 4. Parallel Bytes Array Read
            var parArray = await BlittableInt32StructParquet.From(bytes).Parallel().ToArrayAsync();
            parArray.ShouldBe(items);

            // 5. Parallel Bytes List Read
            var parList = await BlittableInt32StructParquet.From(bytes).Parallel().ToListAsync();
            parList.ShouldBe(items);
        }
    }

    [Theory]
    [InlineData(12345)]
    public async Task Int64PropertySweepAllApisEquivalent(int seed)
    {
        var rng = new Random(seed);

        foreach (int count in TestRowCounts)
        {
            var items = new BlittableInt64Struct[count];
            for (int i = 0; i < count; i++)
            {
                long val = i switch
                {
                    0 => 0L,
                    1 => -1L,
                    2 => 1L,
                    3 => long.MinValue,
                    4 => long.MaxValue,
                    _ => ((long)rng.Next() << 32) | (uint)rng.Next(),
                };
                items[i] = new BlittableInt64Struct { Value = val };
            }

            using var ms = new MemoryStream();
            await items.WriteParquetAsync(ms);
            byte[] bytes = ms.ToArray();

            var seqArray = await BlittableInt64StructParquet.From(bytes).ToArrayAsync();
            var parArray = await BlittableInt64StructParquet.From(bytes).Parallel().ToArrayAsync();

            seqArray.ShouldBe(items);
            parArray.ShouldBe(items);
        }
    }

    [Theory]
    [InlineData(777)]
    public async Task UnsignedIntegersPropertySweepRoundtrip(int seed)
    {
        var rng = new Random(seed);

        foreach (int count in new[] { 0, 1, 15, 64, 255 })
        {
            var byteItems = new BlittableUInt8Struct[count];
            var ushortItems = new BlittableUInt16Struct[count];
            var uintItems = new BlittableUInt32Struct[count];
            var ulongItems = new BlittableUInt64Struct[count];

            for (int i = 0; i < count; i++)
            {
                byteItems[i] = new BlittableUInt8Struct { Value = (byte)rng.Next(0, 256) };
                ushortItems[i] = new BlittableUInt16Struct { Value = (ushort)rng.Next(0, 65536) };
                uintItems[i] = new BlittableUInt32Struct { Value = (uint)rng.Next() };
                ulongItems[i] = new BlittableUInt64Struct
                {
                    Value = ((ulong)rng.Next() << 32) | (uint)rng.Next(),
                };
            }

            // UInt8
            using var ms8 = new MemoryStream();
            await byteItems.WriteParquetAsync(ms8);
            var res8 = await BlittableUInt8StructParquet.From(ms8.ToArray()).ToArrayAsync();
            res8.ShouldBe(byteItems);

            // UInt16
            using var ms16 = new MemoryStream();
            await ushortItems.WriteParquetAsync(ms16);
            var res16 = await BlittableUInt16StructParquet.From(ms16.ToArray()).ToArrayAsync();
            res16.ShouldBe(ushortItems);

            // UInt32
            using var ms32 = new MemoryStream();
            await uintItems.WriteParquetAsync(ms32);
            var res32 = await BlittableUInt32StructParquet.From(ms32.ToArray()).ToArrayAsync();
            res32.ShouldBe(uintItems);

            // UInt64
            using var ms64 = new MemoryStream();
            await ulongItems.WriteParquetAsync(ms64);
            var res64 = await BlittableUInt64StructParquet.From(ms64.ToArray()).ToArrayAsync();
            res64.ShouldBe(ulongItems);
        }
    }

    [Theory]
    [InlineData(888)]
    public async Task SignedSmallIntegersPropertySweepRoundtrip(int seed)
    {
        var rng = new Random(seed);

        foreach (int count in new[] { 0, 1, 15, 64, 255 })
        {
            var sbyteItems = new BlittableInt8Struct[count];
            var shortItems = new BlittableInt16Struct[count];

            for (int i = 0; i < count; i++)
            {
                sbyteItems[i] = new BlittableInt8Struct
                {
                    Value = (sbyte)rng.Next(sbyte.MinValue, sbyte.MaxValue + 1),
                };
                shortItems[i] = new BlittableInt16Struct
                {
                    Value = (short)rng.Next(short.MinValue, short.MaxValue + 1),
                };
            }

            using var ms8 = new MemoryStream();
            await sbyteItems.WriteParquetAsync(ms8);
            var res8 = await BlittableInt8StructParquet.From(ms8.ToArray()).ToArrayAsync();
            res8.ShouldBe(sbyteItems);

            using var ms16 = new MemoryStream();
            await shortItems.WriteParquetAsync(ms16);
            var res16 = await BlittableInt16StructParquet.From(ms16.ToArray()).ToArrayAsync();
            res16.ShouldBe(shortItems);
        }
    }

    // ── Floating Point Primitives (NaN & Infinity Invariants) ─────────────

    [Theory]
    [InlineData(54321)]
    public async Task FloatPreservesBoundariesAndNaN(int seed)
    {
        var rng = new Random(seed);
        float[] boundaryValues =
        [
            0.0f,
            -0.0f,
            float.MinValue,
            float.MaxValue,
            float.Epsilon,
            float.PositiveInfinity,
            float.NegativeInfinity,
            float.NaN,
        ];

        foreach (int count in TestRowCounts)
        {
            var items = new BlittableFloatStruct[count];
            for (int i = 0; i < count; i++)
            {
                float val =
                    i < boundaryValues.Length
                        ? boundaryValues[i]
                        : (float)rng.NextDouble() * 1000f - 500f;

                items[i] = new BlittableFloatStruct { Value = val };
            }

            using var ms = new MemoryStream();
            await items.WriteParquetAsync(ms);
            byte[] bytes = ms.ToArray();

            var readArray = await BlittableFloatStructParquet.From(bytes).ToArrayAsync();
            readArray.Length.ShouldBe(count);

            for (int i = 0; i < count; i++)
            {
                float expected = items[i].Value;
                float actual = readArray[i].Value;

                // Handle IEEE-754 NaN and bitwise representation
                if (float.IsNaN(expected))
                {
                    float.IsNaN(actual).ShouldBeTrue();
                }
                else
                {
                    BitConverter
                        .SingleToInt32Bits(actual)
                        .ShouldBe(BitConverter.SingleToInt32Bits(expected));
                }
            }
        }
    }

    [Theory]
    [InlineData(67890)]
    public async Task DoublePreservesBoundariesAndNaN(int seed)
    {
        var rng = new Random(seed);
        double[] boundaryValues =
        [
            0.0,
            -0.0,
            double.MinValue,
            double.MaxValue,
            double.Epsilon,
            double.PositiveInfinity,
            double.NegativeInfinity,
            double.NaN,
        ];

        foreach (int count in TestRowCounts)
        {
            var items = new BlittableDoubleStruct[count];
            for (int i = 0; i < count; i++)
            {
                double val =
                    i < boundaryValues.Length
                        ? boundaryValues[i]
                        : rng.NextDouble() * 100_000.0 - 50_000.0;

                items[i] = new BlittableDoubleStruct { Value = val };
            }

            using var ms = new MemoryStream();
            await items.WriteParquetAsync(ms);

            ms.Position = 0;
            var readArray = await BlittableDoubleStructParquet.From(ms).ToArrayAsync();
            readArray.Length.ShouldBe(count);

            for (int i = 0; i < count; i++)
            {
                double expected = items[i].Value;
                double actual = readArray[i].Value;

                if (double.IsNaN(expected))
                {
                    double.IsNaN(actual).ShouldBeTrue();
                }
                else
                {
                    BitConverter
                        .DoubleToInt64Bits(actual)
                        .ShouldBe(BitConverter.DoubleToInt64Bits(expected));
                }
            }
        }
    }

    // ── Boolean Primitives ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1111)]
    public async Task BoolAlternatingAndRandomRoundtrips(int seed)
    {
        var rng = new Random(seed);

        foreach (int count in TestRowCounts)
        {
            var items = new BlittableBoolStruct[count];
            for (int i = 0; i < count; i++)
            {
                bool val = (i % 3 == 0) || rng.Next(0, 2) == 1;
                items[i] = new BlittableBoolStruct { Value = val };
            }

            using var ms = new MemoryStream();
            await items.WriteParquetAsync(ms);
            var read = await BlittableBoolStructParquet.From(ms.ToArray()).ToArrayAsync();

            read.Length.ShouldBe(count);
            read.ShouldBe(items);
        }
    }

    // ── Multi-RowGroup Partition Slicing ───────────────────────────────────

    [Theory]
    [MemberData(nameof(MultiRowGroupPartitions))]
    public async Task MultiRowGroupPartitionSlicesReadBackCorrectly(int[] partitionSizes)
    {
        int totalRows = partitionSizes.Sum();
        var allItems = new List<BlittableInt32Struct>(totalRows);
        int val = 0;

        using var ms = new MemoryStream();

        var field = new Parquet.Schema.DataField<int>("val");
        var schema = new Parquet.Schema.ParquetSchema(field);

        await using (var writer = await Parquet.ParquetWriter.CreateAsync(schema, ms))
        {
            foreach (int chunkSize in partitionSizes)
            {
                using var rg = writer.CreateRowGroup();
                int[] buffer = new int[chunkSize];
                for (int i = 0; i < chunkSize; i++)
                {
                    buffer[i] = val;
                    allItems.Add(new BlittableInt32Struct { Value = val });
                    val++;
                }

                await rg.WriteAsync<int>(field, new ReadOnlyMemory<int>(buffer, 0, chunkSize));
            }
        }

        byte[] parquetBytes = ms.ToArray();

        var seqArray = await BlittableInt32StructParquet.From(parquetBytes).ToArrayAsync();
        var parArray = await BlittableInt32StructParquet
            .From(parquetBytes)
            .Parallel()
            .ToArrayAsync();
        var seqList = await BlittableInt32StructParquet
            .From(new MemoryStream(parquetBytes))
            .ToListAsync();

        seqArray.Length.ShouldBe(totalRows);
        seqArray.ShouldBe(allItems);
        parArray.ShouldBe(allItems);
        seqList.ShouldBe(allItems);
    }

    // ── Layout Anomaly Soundness Fallback Tests ────────────────────────────

    [Fact]
    public async Task StructWithIgnoredFieldFallsBackSafelyWithoutMemoryCorruption()
    {
        const int count = 100;
        var items = new StructWithIgnoredField[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new StructWithIgnoredField { Value = i * 10, Extra = 999 };
        }

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        ms.Position = 0;
        var readArray = await StructWithIgnoredFieldParquet.From(ms).ToArrayAsync();

        readArray.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            readArray[i].Value.ShouldBe(i * 10);
            readArray[i].Extra.ShouldBe(0);
        }
    }

    [Fact]
    public async Task StructWithPrivateFieldFallsBackSafelyWithoutMemoryCorruption()
    {
        const int count = 50;
        var items = new StructWithPrivateField[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new StructWithPrivateField { Value = i * 42 };
            items[i].SetPadding(i + 1);
        }

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        ms.Position = 0;
        var readArray = await StructWithPrivateFieldParquet.From(ms).ToArrayAsync();

        readArray.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            readArray[i].Value.ShouldBe(i * 42);
            readArray[i].GetPadding().ShouldBe(0);
        }
    }
}
