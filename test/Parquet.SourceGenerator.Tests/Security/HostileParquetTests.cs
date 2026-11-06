using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace Parquet.SourceGenerator.Tests.Security;

public class HostileParquetTests
{
    private static readonly int[] SingleOneArray = [1];
    private static readonly int[] SingleZeroArray = [0];
    private static readonly int[] SingleNinetyNineArray = [99];
    private static readonly int[] SingleHundredArray = [100];
    private static readonly int[] TwoZerosArray = [0, 0];
    private static readonly int[] TwoTwosArray = [2, 2];
    private static readonly int[] TwoValuesArray = [100, 200];

    [Fact]
    public async Task RowGroupCountExceedsMaxThrowsInvalidDataException()
    {
        // Generate a valid file with 3 row groups (each size 2)
        var items = Enumerable
            .Range(0, 6)
            .Select(i => new MultiRowGroupModel { Id = i, Name = $"item_{i}" })
            .ToList();

        using var ms = new MemoryStream();
        await items.WriteParquetBatchedAsync(ms, new ParquetSerializerOptions { RowGroupSize = 2 });

        var hostileOptions = new ParquetSerializerOptions
        {
            MaxRowGroupCount = 2, // File has 3 row groups, which exceeds the limit of 2
        };

        // 1. ReadParquetAsync (List)
        ms.Position = 0;
        var exList = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquetExtensions.ReadParquetAsync(ms, hostileOptions)
        );
        Assert.Contains("Row group count", exList.Message);
        Assert.Contains("exceeds maximum allowed", exList.Message);

        // 2. ReadParquetArrayAsync (Array)
        ms.Position = 0;
        var exArr = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquetExtensions.ReadParquetArrayAsync(ms, hostileOptions)
        );
        Assert.Contains("Row group count", exArr.Message);

        // 3. ReadParquetParallelArrayAsync (Parallel Array)
        ms.Position = 0;
        var exParallel = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquetExtensions.ReadParquetParallelArrayAsync(ms, hostileOptions)
        );
        Assert.Contains("Row group count", exParallel.Message);

        // 4. ReadParquetStreamAsync (IAsyncEnumerable)
        ms.Position = 0;
        var exStream = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (
                var _ in MultiRowGroupModelParquetExtensions.ReadParquetStreamAsync(
                    ms,
                    hostileOptions
                )
            ) { }
        });
        Assert.Contains("Row group count", exStream.Message);
    }

    [Fact]
    public async Task TotalRowCountExceedsMaxAllocationValuesThrowsInvalidDataException()
    {
        // 10 items total
        var items = Enumerable
            .Range(0, 10)
            .Select(i => new MultiRowGroupModel { Id = i, Name = $"item_{i}" })
            .ToList();

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        var hostileOptions = new ParquetSerializerOptions
        {
            MaxAllocationValues = 5, // File has 10 rows, which exceeds limit of 5
        };

        // 1. ReadParquetAsync
        ms.Position = 0;
        var exList = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquetExtensions.ReadParquetAsync(ms, hostileOptions)
        );
        Assert.Contains("exceeds maximum allowed", exList.Message);

        // 2. ReadParquetArrayAsync
        ms.Position = 0;
        var exArr = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquetExtensions.ReadParquetArrayAsync(ms, hostileOptions)
        );
        Assert.Contains("exceeds maximum allowed", exArr.Message);

        // 3. ReadParquetParallelArrayAsync
        ms.Position = 0;
        var exParallel = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquetExtensions.ReadParquetParallelArrayAsync(ms, hostileOptions)
        );
        Assert.Contains("exceeds maximum allowed", exParallel.Message);

        // 4. ReadParquetStreamAsync (stream checks each row group row count against MaxAllocationValues)
        ms.Position = 0;
        var exStream = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (
                var _ in MultiRowGroupModelParquetExtensions.ReadParquetStreamAsync(
                    ms,
                    hostileOptions
                )
            ) { }
        });
        Assert.Contains("exceeds maximum allowed", exStream.Message);
    }

    [Fact]
    public async Task SchemaNestingDepthExceedsMaxThrowsInvalidDataException()
    {
        // NestedOrder has depth 2 (Address struct in NestedOrder)
        var items = new List<NestedOrder>
        {
            new()
            {
                Id = 1,
                Ship = new Address { City = "Seattle", Zip = 98101 },
                Bill = new Address { City = "Redmond", Zip = 98052 },
            },
        };

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        var hostileOptions = new ParquetSerializerOptions
        {
            MaxNestingDepth = 1, // NestedOrder has depth 2 > 1
        };

        ms.Position = 0;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NestedOrderParquetExtensions.ReadParquetAsync(ms, hostileOptions)
        );
        Assert.Contains("Schema nesting depth", ex.Message);
        Assert.Contains("exceeds maximum allowed 1", ex.Message);
    }

    [Fact]
    public async Task ListColumnValuesExceedMaxAllocationValuesThrowsInvalidDataException()
    {
        // ListRow with multiple list elements
        var items = new List<ListRow>
        {
            new()
            {
                Id = 1,
                Scores = new List<int> { 10, 20, 30, 40, 50 },
                Tags = new List<string?> { "a", "b", "c", "d" },
                Keys = [Guid.NewGuid(), Guid.NewGuid()],
            },
        };

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        // Limit allocation values to 3, but Scores has 5 values
        var hostileOptions = new ParquetSerializerOptions { MaxAllocationValues = 3 };

        ms.Position = 0;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ListRowParquetExtensions.ReadParquetAsync(ms, hostileOptions)
        );
        Assert.Contains("exceeds maximum allowed 3", ex.Message);
    }

    private static readonly int[] SingleNegativeOneArray = [-1];

    [Fact]
    public async Task RepetitionLevelRowOverrunThrowsInvalidDataException()
    {
        var items = new List<ListRow>
        {
            new() { Id = 1, Scores = [10] },
            new() { Id = 2, Scores = [20] },
        };
        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        byte[] bytes = ms.ToArray();
        int footerLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 4 - footerLength;
        var offsets = new List<int>();
        for (int i = footerStart; i < bytes.Length - 5; i++)
        {
            if (bytes[i] == 0x16 && bytes[i + 1] == 0x04)
            {
                offsets.Add(i);
            }
        }

        Assert.True(
            offsets.Count >= 3,
            "Expected at least 3 num_rows/num_values occurrences in Thrift footer"
        );
        // Patch FileMetaData.num_rows (first), Id column num_values (second), and RowGroup.num_rows (last)
        // from 2 to 1. Leave list column num_values intact so list repetition levels contain 2 rows
        // while the row group capacity is only 1 row.
        bytes[offsets[0] + 1] = 0x02;
        bytes[offsets[1] + 1] = 0x02;
        bytes[offsets[^1] + 1] = 0x02;

        using var corruptedMs = new MemoryStream(bytes);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ListRowParquetExtensions.ReadParquetAsync(corruptedMs)
        );
        Assert.Contains("produced more rows than row group capacity", ex.Message);
    }

    [Fact]
    public async Task CorruptedDefinitionLevelOutOfBoundsThrowsInvalidDataException()
    {
        var schema = ListRowParquetExtensions.Schema;

        using var ms = new MemoryStream();
        await using (var writer = await ParquetWriter.CreateAsync(schema, ms))
        {
            using var groupWriter = writer.CreateRowGroup();

            await groupWriter.WriteAsync<int>(
                (DataField)schema.DataFields[0],
                new ReadOnlyMemory<int>(SingleOneArray)
            );

            // Scores max definition level is 2. Inject corrupted def level = 99
            var scoresField = (DataField)schema.DataFields[2];
            var scoresValues = new ReadOnlyMemory<int>(SingleHundredArray);
            var corruptedDef = new ReadOnlyMemory<int>(SingleNinetyNineArray);
            var repLevels = new ReadOnlyMemory<int>(SingleZeroArray);

            await groupWriter.WriteAllPartsAsync<int>(
                scoresField,
                scoresValues,
                corruptedDef,
                repLevels,
                cancellationToken: default
            );

            await WriteEmptyListColumnsAsync(groupWriter, schema);
        }

        ms.Position = 0;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ListRowParquetExtensions.ReadParquetAsync(ms)
        );
        Assert.Contains("Illegal definition level 99", ex.Message);
    }

    [Fact]
    public async Task CorruptedNegativeDefinitionLevelThrowsInvalidDataException()
    {
        var schema = ListRowParquetExtensions.Schema;

        using var ms = new MemoryStream();
        await using (var writer = await ParquetWriter.CreateAsync(schema, ms))
        {
            using var groupWriter = writer.CreateRowGroup();

            await groupWriter.WriteAsync<int>(
                (DataField)schema.DataFields[0],
                new ReadOnlyMemory<int>(SingleOneArray)
            );

            // Scores defLevel injected as -1 (negative definition level)
            var scoresField = (DataField)schema.DataFields[2];
            var scoresValues = new ReadOnlyMemory<int>(SingleHundredArray);
            var corruptedDef = new ReadOnlyMemory<int>(SingleNegativeOneArray);
            var repLevels = new ReadOnlyMemory<int>(SingleZeroArray);

            await groupWriter.WriteAllPartsAsync<int>(
                scoresField,
                scoresValues,
                corruptedDef,
                repLevels,
                cancellationToken: default
            );

            await WriteEmptyListColumnsAsync(groupWriter, schema);
        }

        ms.Position = 0;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ListRowParquetExtensions.ReadParquetAsync(ms)
        );
        Assert.Contains("Illegal definition level", ex.Message);
    }

    [Fact]
    public async Task AbortedHostileReadPreservesArrayPoolHygiene()
    {
        // Verify that repeated exceptions from hostile files do not exhaust or destabilize ArrayPool
        var schema = ListRowParquetExtensions.Schema;

        using var ms = new MemoryStream();
        await using (var writer = await ParquetWriter.CreateAsync(schema, ms))
        {
            using var groupWriter = writer.CreateRowGroup();

            await groupWriter.WriteAsync<int>(
                (DataField)schema.DataFields[0],
                new ReadOnlyMemory<int>(SingleOneArray)
            );

            var scoresField = (DataField)schema.DataFields[2];
            var corruptedDef = new ReadOnlyMemory<int>(SingleNinetyNineArray);
            await groupWriter.WriteAllPartsAsync(
                scoresField,
                new ReadOnlyMemory<int>(SingleOneArray),
                corruptedDef,
                new ReadOnlyMemory<int>(SingleZeroArray),
                cancellationToken: default
            );

            await WriteEmptyListColumnsAsync(groupWriter, schema);
        }

        // Run 50 aborted reads in succession — each should fail cleanly with InvalidDataException
        // without throwing OutOfMemoryException or corrupting buffer pools
        for (int i = 0; i < 50; i++)
        {
            ms.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ListRowParquetExtensions.ReadParquetAsync(ms)
            );
        }

        // Verify a normal read succeeds without any pool corruption
        var validItems = new List<ListRow>
        {
            new()
            {
                Id = 100,
                Scores = [1, 2],
                Tags = ["ok"],
                Keys = [Guid.NewGuid()],
            },
        };
        using var validMs = new MemoryStream();
        await validItems.WriteParquetAsync(validMs);
        validMs.Position = 0;
        var restored = await ListRowParquetExtensions.ReadParquetAsync(validMs);
        Assert.Single(restored);
        Assert.Equal(100, restored[0].Id);
    }

    private static async Task WriteEmptyListColumnsAsync(
        ParquetRowGroupWriter groupWriter,
        ParquetSchema schema
    )
    {
        await groupWriter.WriteAllPartsAsync<ReadOnlyMemory<char>>(
            (DataField)schema.DataFields[1],
            ReadOnlyMemory<ReadOnlyMemory<char>>.Empty,
            new ReadOnlyMemory<int>(SingleZeroArray),
            new ReadOnlyMemory<int>(SingleZeroArray),
            cancellationToken: default
        );
        await groupWriter.WriteAllPartsAsync<Guid>(
            (DataField)schema.DataFields[3],
            ReadOnlyMemory<Guid>.Empty,
            new ReadOnlyMemory<int>(SingleZeroArray),
            new ReadOnlyMemory<int>(SingleZeroArray),
            cancellationToken: default
        );
        await groupWriter.WriteAllPartsAsync<DateTime>(
            (DataField)schema.DataFields[4],
            ReadOnlyMemory<DateTime>.Empty,
            new ReadOnlyMemory<int>(SingleZeroArray),
            new ReadOnlyMemory<int>(SingleZeroArray),
            cancellationToken: default
        );
        await groupWriter.WriteAllPartsAsync<ReadOnlyMemory<byte>>(
            (DataField)schema.DataFields[5],
            ReadOnlyMemory<ReadOnlyMemory<byte>>.Empty,
            new ReadOnlyMemory<int>(SingleZeroArray),
            new ReadOnlyMemory<int>(SingleZeroArray),
            cancellationToken: default
        );
    }
}
