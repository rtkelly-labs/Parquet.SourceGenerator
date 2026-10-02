using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// A nullable value column is handed to the writer as a packed lane (the non-null values only) plus
/// one definition level per row. The batch constructor checks the lane shapes in O(1), so nothing
/// stopped a caller passing a lane longer than the packed count, which is the normal shape for an
/// array rented from <see cref="System.Buffers.ArrayPool{T}"/> (#381). The writer now derives the
/// packed count from the definition levels it is about to write and slices the lane with it.
/// </summary>
public sealed class PackedLaneWriteTests
{
    private static readonly double[] OnePacked = [10.5];
    private static readonly int[] Ids3 = [1, 2, 3];
    private static readonly int[] Present101 = [1, 0, 1];
    private static readonly int[] Present111 = [1, 1, 1];
    private static readonly int[] Level2 = [1, 2, 1];
    private static readonly double?[] Expected = [10.5, null, 20.5];

    private static ReadOnlyMemory<double> Lane(params double[] packed)
    {
        // A rented array is longer than asked for and its tail is not the caller's data.
        var rented = new double[packed.Length + 5];
        Array.Fill(rented, 99.0);
        packed.CopyTo(rented, 0);
        return rented;
    }

    private static BatchOptionalInModelBatch BatchWith(
        ReadOnlyMemory<double> score,
        int[] levels
    ) => new(rowCount: 3, id: Ids3, score: score, scoreDefinitionLevels: levels);

    private static async Task<BatchOptionalInModel[]> RoundTripAsync(
        BatchOptionalInModelBatch batch
    )
    {
        using var stream = new MemoryStream();
        await batch.WriteParquetAsync(stream);
        stream.Position = 0;
        return await BatchOptionalInModelParquet.From(stream).ToArrayAsync();
    }

    [Fact]
    public async Task PackedLaneLongerThanItsPresentCountIsSlicedAsync()
    {
        BatchOptionalInModel[] rows = await RoundTripAsync(BatchWith(Lane(10.5, 20.5), Present101));

        rows.Select(r => r.Score).ShouldBe(Expected);
    }

    [Fact]
    public async Task PackedLaneShorterThanItsPresentCountIsRejectedByNameAsync()
    {
        using var stream = new MemoryStream();
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() =>
            BatchWith(OnePacked, Present111).WriteParquetAsync(stream)
        );

        error.Message.ShouldContain("Score");
    }

    [Fact]
    public async Task DefinitionLevelOtherThanZeroOrOneIsRejectedByNameAsync()
    {
        using var stream = new MemoryStream();
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() =>
            BatchWith(Lane(10.5, 20.5, 30.5), Level2).WriteParquetAsync(stream)
        );

        error.Message.ShouldContain("Score");
    }
}
