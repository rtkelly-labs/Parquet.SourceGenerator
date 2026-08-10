using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record ParallelRow
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("score")]
    public double? Score { get; init; }
}

/// <summary>
/// <c>ReadParquetParallelAsync</c> used to compute a parallelism figure and then read row groups in
/// a plain sequential loop. The buffer overload now genuinely divides the work: one
/// <c>ParquetReader</c> over one stream per worker, with row groups claimed dynamically.
/// </summary>
/// <remarks>
/// The properties worth pinning down are the ones concurrency can break — ordering, completeness
/// and equivalence with the sequential reader — rather than a wall-clock speedup, which would make
/// the suite flaky on a loaded or single-core runner.
/// </remarks>
public sealed class ParallelReadTests
{
    private static async Task<byte[]> WriteAsync(int rowCount, int rowGroupSize)
    {
        List<ParallelRow> rows = Enumerable.Range(1, rowCount)
            .Select(i => new ParallelRow
            {
                Id = i,
                Name = $"row_{i}",
                Score = i % 5 == 0 ? null : i * 0.5,
            })
            .ToList();

        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(stream, rowGroupSize);
        return stream.ToArray();
    }

    [Fact]
    public async Task RowsComeBackInFileOrderAcrossManyRowGroups()
    {
        // Enough row groups that more than one worker is genuinely in play, and an uneven tail so
        // the last group is a different size from the rest.
        byte[] bytes = await WriteAsync(rowCount: 1_050, rowGroupSize: 100);

        List<ParallelRow> read = await ParallelRowParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(bytes));

        Assert.Equal(1_050, read.Count);
        Assert.Equal(Enumerable.Range(1, 1_050), read.Select(r => r.Id));
    }

    [Fact]
    public async Task ParallelReadMatchesTheSequentialReadExactly()
    {
        byte[] bytes = await WriteAsync(rowCount: 1_050, rowGroupSize: 100);

        List<ParallelRow> sequential = await ParallelRowParquetExtensions.ReadParquetAsync(
            new ReadOnlyMemory<byte>(bytes));
        List<ParallelRow> parallel = await ParallelRowParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(bytes));

        // Records compare structurally, so this covers the nullable column and the string column
        // as well as ordering — a buffer handed between workers would show up as a shifted value.
        Assert.Equal(sequential, parallel);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(64)]
    public async Task EveryDegreeOfParallelismProducesTheSameResult(int maxDegreeOfParallelism)
    {
        // 64 exceeds the row-group count, which exercises the clamp: more workers than row groups
        // would otherwise mean readers opened for no work.
        byte[] bytes = await WriteAsync(rowCount: 500, rowGroupSize: 50);

        List<ParallelRow> read = await ParallelRowParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(bytes),
            maxDegreeOfParallelism);

        Assert.Equal(500, read.Count);
        Assert.Equal(Enumerable.Range(1, 500), read.Select(r => r.Id));
        Assert.Equal("row_250", read[249].Name);
        Assert.Null(read[4].Score);
    }

    [Fact]
    public async Task SingleRowGroupFileReadsWithoutDividingTheWork()
    {
        // workerCount collapses to 1 here, which takes the branch that stays on the calling thread
        // instead of paying for a thread-pool hop and a second reader.
        byte[] bytes = await WriteAsync(rowCount: 25, rowGroupSize: 1_000);

        List<ParallelRow> read = await ParallelRowParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(bytes),
            maxDegreeOfParallelism: 16);

        Assert.Equal(25, read.Count);
        Assert.Equal(Enumerable.Range(1, 25), read.Select(r => r.Id));
    }

    [Fact]
    public async Task OptionsSupplyTheParallelismWhenTheArgumentIsUnset()
    {
        byte[] bytes = await WriteAsync(rowCount: 300, rowGroupSize: 50);

        List<ParallelRow> read = await ParallelRowParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(bytes),
            maxDegreeOfParallelism: -1,
            new ParquetSerializerOptions { MaxDegreeOfParallelism = 2 });

        Assert.Equal(300, read.Count);
        Assert.Equal(Enumerable.Range(1, 300), read.Select(r => r.Id));
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        byte[] bytes = await WriteAsync(rowCount: 500, rowGroupSize: 50);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ParallelRowParquetExtensions.ReadParquetParallelAsync(
                new ReadOnlyMemory<byte>(bytes),
                maxDegreeOfParallelism: 4,
                options: null,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task SlicedBufferIsHonouredByEveryWorker()
    {
        // Each worker builds its own stream from the same ReadOnlyMemory, so an offset dropped in
        // CreateBufferStream would corrupt every worker but the probe.
        byte[] bytes = await WriteAsync(rowCount: 400, rowGroupSize: 50);
        byte[] padded = new byte[bytes.Length + 16];
        bytes.CopyTo(padded, 8);

        List<ParallelRow> read = await ParallelRowParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(padded, 8, bytes.Length));

        Assert.Equal(400, read.Count);
        Assert.Equal(Enumerable.Range(1, 400), read.Select(r => r.Id));
    }

    [Fact]
    public async Task RepeatedReadsAreStable()
    {
        // A race in buffer ownership or row-group claiming is intermittent by nature, so a single
        // pass proves little. Repeating with concurrent readers in flight is what surfaces it.
        byte[] bytes = await WriteAsync(rowCount: 600, rowGroupSize: 40);
        var expected = Enumerable.Range(1, 600).ToList();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            List<ParallelRow>[] results = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ =>
                    ParallelRowParquetExtensions.ReadParquetParallelAsync(
                        new ReadOnlyMemory<byte>(bytes),
                        maxDegreeOfParallelism: 4)));

            foreach (List<ParallelRow> result in results)
            {
                Assert.Equal(expected, result.Select(r => r.Id));
            }
        }
    }
}
