using System.IO;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;
using Parquet;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record ArrowStrictRow
{
    [ParquetColumn("id")]
    public long Id { get; init; }

    [ParquetColumn("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Batches whose schema and data disagree, or whose buffers are unsound, are refused by validation
/// with an <see cref="InvalidDataException"/> naming the column — never an
/// <see cref="System.InvalidCastException"/> or <see cref="System.ArgumentOutOfRangeException"/>
/// from deep inside the bridge, and never a silently chosen duplicate. Backported from
/// Arrow.SourceGenerator's reader validation.
/// </summary>
public sealed class ArrowBatchStructuralValidationTests
{
    private static Int64Array Ids(params long[] values) =>
        new Int64Array.Builder().AppendRange(values).Build();

    private static StringArray Names(params string[] values)
    {
        var builder = new StringArray.Builder();
        foreach (string value in values)
        {
            builder.Append(value);
        }

        return builder.Build();
    }

    private static async Task<InvalidDataException> RejectAsync(RecordBatch batch)
    {
        using var stream = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(
            ArrowStrictRowParquetExtensions.Schema,
            stream
        );
        return await Should.ThrowAsync<InvalidDataException>(async () =>
            await ArrowStrictRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch)
        );
    }

    [Fact]
    public async Task DuplicateFieldNameIsAmbiguousNotFirstMatch()
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("id").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("name").DataType(StringType.Default).Nullable(true))
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[] { Ids(1), Ids(2), Names("a") },
            1
        );

        (await RejectAsync(batch)).Message.ShouldContain(
            "id: column appears 2 times",
            Case.Sensitive
        );
    }

    [Fact]
    public async Task SchemaThatDisagreesWithItsColumnIsRejectedNotCast()
    {
        // The schema claims Int64 for "id" but the column is a string array.
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("name").DataType(StringType.Default).Nullable(true))
            .Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { Names("x"), Names("a") }, 1);

        (await RejectAsync(batch)).Message.ShouldContain(
            "id: the schema declares int64 but the column holds utf8",
            Case.Sensitive
        );
    }

    [Fact]
    public async Task ArrayWhoseTypeParametersDisagreeWithTheSchemaIsRejected()
    {
        // Same type id, different parameters: the schema claims the mapped Decimal128(38, 18) but
        // the array holds Decimal128(38, 4). Decoding it at scale 18 would silently shift every
        // value by 14 decimal places.
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("value").DataType(new Decimal128Type(38, 18)).Nullable(false))
            .Build();
        var values = new Decimal128Array.Builder(new Decimal128Type(38, 4)).Append(1.5m).Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { values }, 1);

        using var stream = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(
            ArrowWideDecimalRowParquetExtensions.Schema,
            stream
        );
        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(async () =>
            await ArrowWideDecimalRowParquetExtensions.WriteParquetRowGroupAsync(writer, batch)
        );

        error.Message.ShouldContain(
            "value: the column's array disagrees with its schema field: decimal precision/scale mismatch",
            Case.Sensitive
        );
    }

    [Fact]
    public async Task NullCountThatDisagreesWithTheValidityBitmapIsRejected()
    {
        // "id" is required. Bit 0 unset (row 0 null), bit 1 set, yet the array claims no nulls:
        // trusting NullCount would write row 0's undefined slot as a value.
        var validity = new ArrowBuffer(new byte[] { 0b10, 0, 0, 0, 0, 0, 0, 0 });
        var hostile = new Int64Array(new ArrowBuffer(new byte[16]), validity, 2, 0, 0);
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("name").DataType(StringType.Default).Nullable(true))
            .Build();
        using var batch = new RecordBatch(
            schema,
            new IArrowArray[] { hostile, Names("a", "b") },
            2
        );

        (await RejectAsync(batch)).Message.ShouldContain(
            "id: the Arrow column's validity bitmap marks nulls that its null count does not declare",
            Case.Sensitive
        );
    }

    [Fact]
    public async Task MalformedOffsetsAreRejectedBeforeAnyValueIsSliced()
    {
        // Offsets 0, 5, 2: decreasing, and 5 runs past a 3-byte value buffer.
        var offsets = new ArrowBuffer.Builder<int>().Append(0).Append(5).Append(2).Build();
        var values = new ArrowBuffer.Builder<byte>()
            .Append((byte)'a')
            .Append((byte)'b')
            .Append((byte)'c')
            .Build();
        var hostile = new StringArray(
            new ArrayData(StringType.Default, 2, 0, 0, new[] { ArrowBuffer.Empty, offsets, values })
        );
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int64Type.Default).Nullable(false))
            .Field(f => f.Name("name").DataType(StringType.Default).Nullable(true))
            .Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { Ids(1, 2), hostile }, 2);

        (await RejectAsync(batch)).Message.ShouldContain(
            "name: offsets decrease or run past the value buffer",
            Case.Sensitive
        );
    }
}
