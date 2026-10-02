using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public sealed partial class SiblingItem
{
    public int A { get; init; }

    public int B { get; init; }
}

[ParquetSerializable]
public sealed partial record SiblingRow
{
    public int Id { get; init; }

    public List<SiblingItem> Items { get; init; } = new();
}

/// <summary>
/// The columns under one <c>List&lt;Struct&gt;</c> are read by a single walk driven by the first
/// column's entry count, which indexes every sibling's definition levels. Each column declares its
/// own <c>num_values</c>, and each is sized and validated alone, so a file can make them disagree
/// while every page header stays self-consistent (#365). Sibling leaves of one element must carry
/// identical level streams.
/// </summary>
public sealed class CompoundLevelConsistencyTests
{
    private static readonly int[] Ids = [1, 2];
    private static readonly int[] ThreeAs = [1, 2, 3];
    private static readonly int[] ThreeBs = [10, 20, 30];
    private static readonly int[] TwoBs = [10, 20];
    private static readonly int[] RepThree = [0, 1, 0];
    private static readonly int[] RepTwo = [0, 0];

    private static async Task<byte[]> WriteAsync(int[] bs, int[] bRepetition)
    {
        ParquetSchema schema = SiblingRowParquetExtensions.Schema;
        var idField = (DataField)schema.DataFields[0];
        var aField = (DataField)schema.DataFields[1];
        var bField = (DataField)schema.DataFields[2];

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter group = writer.CreateRowGroup();
            await group.WriteAsync<int>(idField, new ReadOnlyMemory<int>(Ids));
            await WriteLeafAsync(group, aField, ThreeAs, RepThree);
            await WriteLeafAsync(group, bField, bs, bRepetition);
        }

        return stream.ToArray();
    }

    private static async Task WriteLeafAsync(
        ParquetRowGroupWriter group,
        DataField field,
        int[] values,
        int[] repetition
    )
    {
        int[] definition = new int[values.Length];
        Array.Fill(definition, field.MaxDefinitionLevel);
        await group.WriteAllPartsAsync<int>(
            field,
            new ReadOnlyMemory<int>(values),
            new ReadOnlyMemory<int>(definition),
            new ReadOnlyMemory<int>(repetition),
            cancellationToken: default
        );
    }

    [Fact]
    public async Task ConsistentSiblingsReadBackAsync()
    {
        byte[] file = await WriteAsync(ThreeBs, RepThree);

        SiblingRow[] rows = await SiblingRowParquet.From(new MemoryStream(file)).ToArrayAsync();

        rows.Length.ShouldBe(2);
        rows[0].Items.Count.ShouldBe(2);
        rows[1].Items.Count.ShouldBe(1);
        rows[0].Items[1].B.ShouldBe(20);
    }

    [Fact]
    public async Task SiblingLeafDeclaringFewerValuesThanItsAnchorIsRejectedAsync()
    {
        // Items.A has three entries, Items.B two, and each column chunk is internally valid.
        byte[] file = await WriteAsync(TwoBs, RepTwo);

        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() =>
            SiblingRowParquet.From(new MemoryStream(file)).ToArrayAsync()
        );

        error.Message.ShouldContain("sibling column");
    }
}
