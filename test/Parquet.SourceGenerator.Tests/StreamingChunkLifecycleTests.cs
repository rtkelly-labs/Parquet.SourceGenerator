using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record StreamingChunkModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("text")]
    public string Text { get; init; } = string.Empty;

    [ParquetColumn("optional_text")]
    public string? OptionalText { get; init; }

    [ParquetColumn("data")]
    public byte[] Data { get; init; } = Array.Empty<byte>();

    [ParquetColumn("guid")]
    public Guid GuidVal { get; init; }

    [ParquetColumn("amount")]
    [ParquetDecimal(18, 2)]
    public decimal Amount { get; init; }
}

public class StreamingChunkLifecycleTests
{
    private static async IAsyncEnumerable<StreamingChunkModel> ToAsyncEnumerable(
        IEnumerable<StreamingChunkModel> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task StreamingWriteRemainderChunkLifecycleAndFidelity()
    {
        const int totalCount = 17;
        const int rowGroupSize = 5;

        var items = Enumerable
            .Range(0, totalCount)
            .Select(i => new StreamingChunkModel
            {
                Id = i,
                Text = $"Text_{i}",
                OptionalText = i % 3 == 0 ? null : $"Opt_{i}",
                Data = new byte[] { (byte)(i & 0xFF), (byte)((i * 7) & 0xFF) },
                GuidVal = Guid.NewGuid(),
                Amount = 100.50m + i,
            })
            .ToList();

        using var stream = new MemoryStream();
        await ToAsyncEnumerable(items)
            .WriteParquetAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
            );

        // 1. Verify Parquet low-level row group structure
        stream.Position = 0;
        await using (var reader = await ParquetReader.CreateAsync(stream))
        {
            reader.RowGroupCount.ShouldBe(4);
            using var rg0 = reader.OpenRowGroupReader(0);
            rg0.RowCount.ShouldBe(5);
            using var rg1 = reader.OpenRowGroupReader(1);
            rg1.RowCount.ShouldBe(5);
            using var rg2 = reader.OpenRowGroupReader(2);
            rg2.RowCount.ShouldBe(5);
            using var rg3 = reader.OpenRowGroupReader(3);
            rg3.RowCount.ShouldBe(2);
        }

        // 2. Sequential Read
        stream.Position = 0;
        StreamingChunkModel[] sequentialResults = await StreamingChunkModelParquet
            .From(stream)
            .ToArrayAsync();
        AssertEqualLists(items, sequentialResults);

        // 3. Parallel Read across row groups
        stream.Position = 0;
        StreamingChunkModel[] parallelResults = await StreamingChunkModelParquet
            .From(stream.ToArray())
            .Parallel()
            .ToArrayAsync();
        AssertEqualLists(items, parallelResults);

        // 4. Streaming Read row-group by row-group
        stream.Position = 0;
        var streamedResults = new List<StreamingChunkModel>();
        await foreach (var item in StreamingChunkModelParquet.From(stream).AsAsyncEnumerable())
        {
            streamedResults.Add(item);
        }
        AssertEqualLists(items, streamedResults);
    }

    [Fact]
    public async Task StreamingWriteExactMultipleProducesExactRowGroups()
    {
        const int totalCount = 20;
        const int rowGroupSize = 5;

        var items = Enumerable
            .Range(0, totalCount)
            .Select(i => new StreamingChunkModel
            {
                Id = i,
                Text = $"Item_{i}",
                OptionalText = $"Opt_{i}",
                Data = new byte[] { (byte)i },
                GuidVal = Guid.NewGuid(),
                Amount = 10.0m * i,
            })
            .ToList();

        using var stream = new MemoryStream();
        await ToAsyncEnumerable(items)
            .WriteParquetAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
            );

        stream.Position = 0;
        await using (var reader = await ParquetReader.CreateAsync(stream))
        {
            reader.RowGroupCount.ShouldBe(4);
            for (int r = 0; r < 4; r++)
            {
                using var rg = reader.OpenRowGroupReader(r);
                rg.RowCount.ShouldBe(5);
            }
        }

        stream.Position = 0;
        StreamingChunkModel[] readBack = await StreamingChunkModelParquet
            .From(stream)
            .ToArrayAsync();
        AssertEqualLists(items, readBack);
    }

    [Fact]
    public async Task StreamingWriteSingleItemRemainderChunkLifecycle()
    {
        const int totalCount = 11;
        const int rowGroupSize = 5;

        var items = Enumerable
            .Range(0, totalCount)
            .Select(i => new StreamingChunkModel
            {
                Id = i,
                Text = $"Row_{i}",
                OptionalText = null,
                Data = Array.Empty<byte>(),
                GuidVal = Guid.NewGuid(),
                Amount = 1.0m,
            })
            .ToList();

        using var stream = new MemoryStream();
        await ToAsyncEnumerable(items)
            .WriteParquetAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
            );

        stream.Position = 0;
        await using (var reader = await ParquetReader.CreateAsync(stream))
        {
            reader.RowGroupCount.ShouldBe(3);
            using var rg0 = reader.OpenRowGroupReader(0);
            rg0.RowCount.ShouldBe(5);
            using var rg1 = reader.OpenRowGroupReader(1);
            rg1.RowCount.ShouldBe(5);
            using var rg2 = reader.OpenRowGroupReader(2);
            rg2.RowCount.ShouldBe(1);
        }

        stream.Position = 0;
        StreamingChunkModel[] readBack = await StreamingChunkModelParquet
            .From(stream.ToArray())
            .Parallel()
            .ToArrayAsync();
        AssertEqualLists(items, readBack);
    }

    [Fact]
    public async Task StreamingWriteEmptySequenceCreatesValidEmptyParquetFile()
    {
        var items = Enumerable.Empty<StreamingChunkModel>();

        using var stream = new MemoryStream();
        await ToAsyncEnumerable(items)
            .WriteParquetAsync(stream, new ParquetSerializerOptions { RowGroupSize = 10 });

        stream.Position = 0;
        await using (var reader = await ParquetReader.CreateAsync(stream))
        {
            reader.RowGroupCount.ShouldBe(0);
        }

        stream.Position = 0;
        StreamingChunkModel[] readBack = await StreamingChunkModelParquet
            .From(stream)
            .ToArrayAsync();
        readBack.ShouldBeEmpty();
    }

    [Fact]
    public async Task StreamingWriteMultiRowGroupBufferRecyclingDoesNotLeakStaleData()
    {
        // First row group of 5 items has populated text and binary
        // Second row group (remainder of 2 items) has null text and empty binary
        var items = CreateMultiRowGroupRecyclingTestData();

        using var stream = new MemoryStream();
        await ToAsyncEnumerable(items)
            .WriteParquetAsync(stream, new ParquetSerializerOptions { RowGroupSize = 5 });

        // Verify low level
        stream.Position = 0;
        await using (var reader = await ParquetReader.CreateAsync(stream))
        {
            reader.RowGroupCount.ShouldBe(2);
            using var rg0 = reader.OpenRowGroupReader(0);
            rg0.RowCount.ShouldBe(5);
            using var rg1 = reader.OpenRowGroupReader(1);
            rg1.RowCount.ShouldBe(2);
        }

        // Verify roundtrip read
        stream.Position = 0;
        StreamingChunkModel[] results = await StreamingChunkModelParquet
            .From(stream.ToArray())
            .Parallel()
            .ToArrayAsync();

        AssertEqualLists(items, results);

        // Specifically assert the remainder items did not leak stale buffer contents
        results[5].Text.ShouldBe(string.Empty);
        results[5].OptionalText.ShouldBeNull();
        results[5].Data.ShouldBeEmpty();
        results[5].GuidVal.ShouldBe(Guid.Empty);
        results[5].Amount.ShouldBe(0m);

        results[6].Text.ShouldBe(string.Empty);
        results[6].OptionalText.ShouldBeNull();
        results[6].Data.ShouldBeEmpty();
        results[6].GuidVal.ShouldBe(Guid.Empty);
        results[6].Amount.ShouldBe(0m);
    }

    private static void AssertEqualLists(
        List<StreamingChunkModel> expected,
        IReadOnlyList<StreamingChunkModel> actual
    )
    {
        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Id.ShouldBe(expected[i].Id);
            actual[i].Text.ShouldBe(expected[i].Text);
            actual[i].OptionalText.ShouldBe(expected[i].OptionalText);
            actual[i].Data.ShouldBe(expected[i].Data);
            actual[i].GuidVal.ShouldBe(expected[i].GuidVal);
            actual[i].Amount.ShouldBe(expected[i].Amount);
        }
    }

    private static List<StreamingChunkModel> CreateMultiRowGroupRecyclingTestData() =>
        [
            // Row Group 1 (5 items)
            new()
            {
                Id = 1,
                Text = "NonEmpty1",
                OptionalText = "LongStringForBuffer1",
                Data = new byte[] { 1, 2, 3, 4, 5 },
                GuidVal = Guid.NewGuid(),
                Amount = 12.34m,
            },
            new()
            {
                Id = 2,
                Text = "NonEmpty2",
                OptionalText = "LongStringForBuffer2",
                Data = new byte[] { 6, 7, 8, 9, 10 },
                GuidVal = Guid.NewGuid(),
                Amount = 23.45m,
            },
            new()
            {
                Id = 3,
                Text = "NonEmpty3",
                OptionalText = "LongStringForBuffer3",
                Data = new byte[] { 11, 12, 13, 14, 15 },
                GuidVal = Guid.NewGuid(),
                Amount = 34.56m,
            },
            new()
            {
                Id = 4,
                Text = "NonEmpty4",
                OptionalText = "LongStringForBuffer4",
                Data = new byte[] { 16, 17, 18, 19, 20 },
                GuidVal = Guid.NewGuid(),
                Amount = 45.67m,
            },
            new()
            {
                Id = 5,
                Text = "NonEmpty5",
                OptionalText = "LongStringForBuffer5",
                Data = new byte[] { 21, 22, 23, 24, 25 },
                GuidVal = Guid.NewGuid(),
                Amount = 56.78m,
            },
            // Row Group 2 (remainder 2 items) with nulls and empty arrays
            new()
            {
                Id = 6,
                Text = "",
                OptionalText = null,
                Data = Array.Empty<byte>(),
                GuidVal = Guid.Empty,
                Amount = 0m,
            },
            new()
            {
                Id = 7,
                Text = "",
                OptionalText = null,
                Data = Array.Empty<byte>(),
                GuidVal = Guid.Empty,
                Amount = 0m,
            },
        ];
}
