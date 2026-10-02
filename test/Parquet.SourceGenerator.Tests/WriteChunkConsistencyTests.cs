using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The write path sizes every buffer from <c>chunk.Count</c>, read once, then fills the buffers by
/// walking the chunk. These tests give it a chunk whose <c>Count</c> disagrees with what it
/// actually holds, which is what a list mutated mid-write (#375) or a lazily backed
/// <c>IReadOnlyCollection</c> (#388) looks like, and require an <see cref="InvalidOperationException"/>
/// rather than an out-of-bounds read or a silently truncated file.
/// </summary>
public sealed class WriteChunkConsistencyTests
{
    /// <summary>
    /// A <see cref="List{T}"/> whose <see cref="IReadOnlyCollection{T}.Count"/> reports more than
    /// the list holds. The list's capacity covers the reported count, so an unguarded walk stays
    /// inside the backing array (it reads default slots) instead of corrupting the test process.
    /// </summary>
    private sealed class OverReportingList
        : List<BoundsCheckRecord>,
            IReadOnlyCollection<BoundsCheckRecord>
    {
        private readonly int _reported;

        public OverReportingList(int reported, IEnumerable<BoundsCheckRecord> items)
            : base(reported)
        {
            _reported = reported;
            AddRange(items);
        }

        int IReadOnlyCollection<BoundsCheckRecord>.Count => _reported;
    }

    /// <summary>A sequence that is neither a list nor an array and reports its own count.</summary>
    private sealed class MiscountedCollection : IReadOnlyCollection<BoundsCheckRecord>
    {
        private readonly BoundsCheckRecord[] _items;

        public MiscountedCollection(int reported, BoundsCheckRecord[] items)
        {
            Count = reported;
            _items = items;
        }

        public int Count { get; }

        public IEnumerator<BoundsCheckRecord> GetEnumerator() =>
            ((IEnumerable<BoundsCheckRecord>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static BoundsCheckRecord[] Records(int count)
    {
        var records = new BoundsCheckRecord[count];
        for (int i = 0; i < count; i++)
        {
            records[i] = new BoundsCheckRecord { Id = i, Name = "n" + i };
        }

        return records;
    }

    private static async Task WriteChunkAsync(IReadOnlyCollection<BoundsCheckRecord> chunk)
    {
        using var stream = new MemoryStream();
        await using global::Parquet.ParquetWriter writer = await global::Parquet
            .ParquetWriter.CreateAsync(BoundsCheckRecordParquetExtensions.Schema, stream)
            .ConfigureAwait(false);
        await BoundsCheckRecordParquetExtensions
            .WriteParquetRowGroupAsync(writer, chunk)
            .ConfigureAwait(false);
    }

    [Fact]
    public async Task ListShorterThanItsReportedCountThrowsInsteadOfReadingPastTheSpanAsync()
    {
        // Count says 10, the list holds 3: the span the writer takes is 3 long.
        var chunk = new OverReportingList(10, Records(3));

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() =>
            WriteChunkAsync(chunk)
        );

        error.Message.ShouldContain("modified during serialization");
    }

    [Fact]
    public async Task CollectionYieldingMoreThanItsCountThrowsInsteadOfDroppingRowsAsync()
    {
        // Rent(5) hands back a 16-slot array, so the extra seven rows used to land in it and then
        // vanish when only 5 were written.
        var chunk = new MiscountedCollection(5, Records(12));

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() =>
            WriteChunkAsync(chunk)
        );

        error.Message.ShouldContain("yielded more");
    }

    [Fact]
    public async Task CollectionYieldingFewerThanItsCountThrowsInsteadOfWritingStaleRowsAsync()
    {
        var chunk = new MiscountedCollection(12, Records(5));

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() =>
            WriteChunkAsync(chunk)
        );

        error.Message.ShouldContain("yielded 5");
    }

    [Fact]
    public async Task HonestCollectionsStillWriteAsync()
    {
        await WriteChunkAsync(new OverReportingList(3, Records(3)));
        await WriteChunkAsync(new MiscountedCollection(4, Records(4)));
        await WriteChunkAsync(Records(4));
    }
}
