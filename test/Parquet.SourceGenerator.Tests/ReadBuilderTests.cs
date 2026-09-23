using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record BuilderOrder
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string? Name { get; init; }
}

/// <summary>
/// The generated read entry point and its single reader struct (issues #217, #478, #479).
/// </summary>
public sealed class ReadBuilderTests
{
    private const int RowsPerGroup = 50;

    private static List<BuilderOrder> Rows(int count) =>
        Enumerable
            .Range(1, count)
            .Select(i => new BuilderOrder { Id = i, Name = $"row-{i}" })
            .ToList();

    private static async Task<byte[]> WriteAsync(int count)
    {
        using var stream = new MemoryStream();
        await Rows(count)
            .WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = RowsPerGroup }
            );
        return stream.ToArray();
    }

    [Fact]
    public async Task StreamSourceMaterializesEveryShape()
    {
        byte[] bytes = await WriteAsync(120);

        using var forArray = new MemoryStream(bytes);
        BuilderOrder[] array = await BuilderOrderParquet.From(forArray).ToArrayAsync();

        using var forStream = new MemoryStream(bytes);
        var streamed = new List<BuilderOrder>();
        await foreach (var item in BuilderOrderParquet.From(forStream).AsAsyncEnumerable())
        {
            streamed.Add(item);
        }

        using var forBatches = new MemoryStream(bytes);
        int batchedRows = 0;
        await foreach (var batch in BuilderOrderParquet.From(forBatches).Batches())
        {
            batchedRows += batch.RowCount;
        }

        array.Length.ShouldBe(120);
        array.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 120));
        streamed.ShouldBe(array);
        batchedRows.ShouldBe(120);
    }

    [Fact]
    public async Task MemorySourceMaterializesEveryShapeIncludingParallel()
    {
        byte[] bytes = await WriteAsync(120);
        var memory = new ReadOnlyMemory<byte>(bytes);

        BuilderOrder[] sequential = await BuilderOrderParquet.From(memory).ToArrayAsync();
        BuilderOrder[] parallel = await BuilderOrderParquet
            .From(memory)
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 2 })
            .Parallel()
            .ToArrayAsync();

        var streamed = new List<BuilderOrder>();
        await foreach (var item in BuilderOrderParquet.From(memory).AsAsyncEnumerable())
        {
            streamed.Add(item);
        }

        sequential.Length.ShouldBe(120);
        parallel.ShouldBe(sequential);
        streamed.ShouldBe(sequential);
    }

    [Fact]
    public async Task CallersNeedingAListConvertTheArray()
    {
        // #479: List<T> versus T[] is a collection preference, not a storage-engine capability, so
        // the reader offers one materialised shape. This is the documented migration.
        byte[] bytes = await WriteAsync(60);

        List<BuilderOrder> list = (
            await BuilderOrderParquet.From(new ReadOnlyMemory<byte>(bytes)).ToArrayAsync()
        ).ToList();

        list.Count.ShouldBe(60);
        list.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 60));
    }

    [Fact]
    public async Task OptionsFlowThroughTheBuilder()
    {
        byte[] bytes = await WriteAsync(60);
        var options = new ParquetSerializerOptions { DeduplicateStrings = true };

        BuilderOrder[] read = await BuilderOrderParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .WithOptions(options)
            .ToArrayAsync();

        read.Length.ShouldBe(60);
    }

    [Fact]
    public async Task OptionsAppliedAfterParallelOrWhereKeepThatState()
    {
        // WithOptions replaces only the options: a later WithOptions must not drop the parallel
        // flag or the predicate that an earlier call set.
        byte[] bytes = await WriteAsync(200);
        var memory = new ReadOnlyMemory<byte>(bytes);
        var limited = new ParquetSerializerOptions { MaxRowGroupCount = 1 };

        // The file has four row groups, so the guard proves the options reached the read.
        await Should.ThrowAsync<InvalidDataException>(() =>
            BuilderOrderParquet.From(memory).Parallel().WithOptions(limited).ToArrayAsync()
        );

        BuilderOrder[] filtered = await BuilderOrderParquet
            .From(memory)
            .Where(m => m.Id.Min >= 100)
            .WithOptions(new ParquetSerializerOptions())
            .ToArrayAsync();
        filtered.Length.ShouldBeLessThan(200);
        filtered.ShouldAllBe(r => r.Id >= 100 - RowsPerGroup);

        // And the parallel flag survives WithOptions: Where() still sees a parallel reader.
        Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet
                .From(memory)
                .Parallel()
                .WithOptions(new ParquetSerializerOptions())
                .Where(m => true)
        );
    }

    [Fact]
    public async Task WhereAppliesTheSamePruningFromEitherSource()
    {
        // The removed flat methods grew `predicate` on the stream overloads of ReadParquetAsync
        // and ReadParquetArrayAsync but never on the buffer ones, so buffer + List and buffer +
        // array were the two cells of the grid where pushdown was simply unavailable (defect 3 in
        // docs/19). The builder routes those through the streaming core that does accept a
        // predicate, so the same filter is reachable from every source.
        byte[] bytes = await WriteAsync(200);

        using var streamSource = new MemoryStream(bytes);
        BuilderOrder[] fromStream = await BuilderOrderParquet
            .From(streamSource)
            .Where(m => m.Id.Min >= 100)
            .ToArrayAsync();

        BuilderOrder[] fromMemory = await BuilderOrderParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .Where(m => m.Id.Min >= 100)
            .ToArrayAsync();

        fromStream.ShouldNotBeEmpty();
        fromMemory.ShouldBe(fromStream);
        fromStream.ShouldAllBe(r => r.Id >= 100 - RowsPerGroup);
    }

    [Fact]
    public async Task FilteredArrayAndEnumerableAgree()
    {
        byte[] bytes = await WriteAsync(200);
        var memory = new ReadOnlyMemory<byte>(bytes);

        BuilderOrder[] array = await BuilderOrderParquet
            .From(memory)
            .Where(m => m.Id.Min >= 100)
            .ToArrayAsync();

        var streamed = new List<BuilderOrder>();
        await foreach (
            var item in BuilderOrderParquet
                .From(memory)
                .Where(m => m.Id.Min >= 100)
                .AsAsyncEnumerable()
        )
        {
            streamed.Add(item);
        }

        using var stream = new MemoryStream(bytes);
        var streamedFromStream = new List<BuilderOrder>();
        await foreach (
            var item in BuilderOrderParquet
                .From(stream)
                .Where(m => m.Id.Min >= 100)
                .AsAsyncEnumerable()
        )
        {
            streamedFromStream.Add(item);
        }

        array.ShouldNotBeEmpty();
        streamed.ShouldBe(array);
        streamedFromStream.ShouldBe(array);
    }

    [Fact]
    public void OneReaderTypeExpressesEveryState()
    {
        // #478: the four #217 state types (stream, memory, filtered, parallel source) collapse into
        // one public reader. Every composing member returns that same type.
        Type reader = typeof(BuilderOrderParquetReader);
        reader.IsValueType.ShouldBeTrue();
        reader
            .GetCustomAttributes()
            .Any(a => a.GetType().Name == "IsReadOnlyAttribute")
            .ShouldBeTrue("the reader should be a readonly struct");

        typeof(BuilderOrderParquet)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "From")
            .ShouldAllBe(m => m.ReturnType == reader);
        foreach (string composer in new[] { "WithOptions", "Where", "Parallel" })
        {
            reader.GetMethod(composer)!.ReturnType.ShouldBe(reader, composer);
        }

        foreach (
            string removed in new[]
            {
                "ParquetStreamSource",
                "ParquetMemorySource",
                "ParquetFilteredSource",
                "ParquetParallelSource",
            }
        )
        {
            typeof(BuilderOrder)
                .Assembly.GetType($"{typeof(BuilderOrder).Namespace}.BuilderOrder{removed}")
                .ShouldBeNull($"BuilderOrder{removed} should no longer be emitted (#478)");
        }
    }

    [Fact]
    public void TheReaderOffersOneMaterialisedShape()
    {
        // #479: ToArrayAsync and AsAsyncEnumerable are the stable terminals; ToListAsync is gone.
        Type reader = typeof(BuilderOrderParquetReader);
        reader.GetMethod("ToListAsync").ShouldBeNull();
        reader.GetMethod("ToArrayAsync")!.ReturnType.ShouldBe(typeof(Task<BuilderOrder[]>));
        reader
            .GetMethod("AsAsyncEnumerable")!
            .ReturnType.ShouldBe(typeof(IAsyncEnumerable<BuilderOrder>));
        reader.GetMethod("Batches").ShouldNotBeNull();
    }

    [Fact]
    public void ComposingAReadAllocatesNothing()
    {
        var options = new ParquetSerializerOptions();
        Func<BuilderOrderRowGroupMetadata, bool> predicate = m => m.Id.Min >= 0;
        var memory = new ReadOnlyMemory<byte>(new byte[16]);

        // Warm up so JIT and static initialisation are outside the measured window.
        _ = BuilderOrderParquet.From(memory).WithOptions(options).Where(predicate);
        _ = BuilderOrderParquet.From(memory).WithOptions(options).Parallel();

        long before = GC.GetAllocatedBytesForCurrentThread();
        BuilderOrderParquetReader filtered = BuilderOrderParquet
            .From(memory)
            .WithOptions(options)
            .Where(predicate)
            .WithOptions(options);
        BuilderOrderParquetReader parallel = BuilderOrderParquet
            .From(memory)
            .Parallel()
            .WithOptions(options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(filtered);
        GC.KeepAlive(parallel);
        allocated.ShouldBe(0L);
    }

    [Fact]
    public void ParallelTakesNoDegreeArgumentWhileTheOptionOwnsIt()
    {
        // #239 made ParquetSerializerOptions the single home for configuration. A degree argument
        // on Parallel() would give the knob two homes again — the condition #218 existed to remove.
        // #241 decides whether it moves here now the builder exists; until then the absence is
        // deliberate, and this test keeps it from being "fixed" by accident.
        typeof(BuilderOrderParquetReader)
            .GetMethod("Parallel")!
            .GetParameters()
            .ShouldBeEmpty();
    }

    // ── Combinations the #217 state types made unrepresentable (#478, docs/47 §4.2) ──────────
    //
    // Rule: the call that completes an unsupported combination throws NotSupportedException.
    // Parallel() and Where() know the source and existing state, so they throw themselves; a
    // terminal throws only when the combination is the terminal itself. None is silently degraded.

    [Fact]
    public void ParallelOnAStreamSourceThrowsNamingTheBufferSource()
    {
        using var stream = new MemoryStream();
        var ex = Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet.From(stream).Parallel()
        );
        ex.Message.ShouldContain("From(ReadOnlyMemory<byte>)");

        // Options or a predicate first changes nothing: the source is still a stream.
        Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet.From(stream).WithOptions(new ParquetSerializerOptions()).Parallel()
        );
    }

    [Fact]
    public async Task ParallelThenAsAsyncEnumerableThrows()
    {
        byte[] bytes = await WriteAsync(10);
        BuilderOrderParquetReader reader = BuilderOrderParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .Parallel();

        // Thrown by the call itself, not deferred to the first MoveNextAsync.
        var ex = Should.Throw<NotSupportedException>(() => reader.AsAsyncEnumerable());
        ex.Message.ShouldContain("Parallel()");
    }

    [Fact]
    public async Task ParallelThenBatchesThrows()
    {
        byte[] bytes = await WriteAsync(10);
        BuilderOrderParquetReader reader = BuilderOrderParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .Parallel();

        var ex = Should.Throw<NotSupportedException>(() => reader.Batches());
        ex.Message.ShouldContain("Parallel()");
    }

    [Fact]
    public void WhereThenParallelThrows()
    {
        var ex = Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet
                .From(new ReadOnlyMemory<byte>(new byte[16]))
                .Where(m => m.Id.Min >= 100)
                .Parallel()
        );
        ex.Message.ShouldContain("Where()");
    }

    [Fact]
    public void ParallelThenWhereThrows()
    {
        var ex = Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet
                .From(new ReadOnlyMemory<byte>(new byte[16]))
                .Parallel()
                .Where(m => m.Id.Min >= 100)
        );
        ex.Message.ShouldContain("Parallel()");
    }

    [Fact]
    public async Task WhereThenBatchesThrowsRatherThanIgnoringThePredicate()
    {
        // The #217 filtered source had no Batches(); the column-batch reader takes no predicate,
        // so running it would silently return unpruned row groups.
        byte[] bytes = await WriteAsync(10);
        using var stream = new MemoryStream(bytes);

        var ex = Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet.From(stream).Where(m => m.Id.Min >= 100).Batches()
        );
        ex.Message.ShouldContain("Where()");
        Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet
                .From(new ReadOnlyMemory<byte>(bytes))
                .Where(m => m.Id.Min >= 100)
                .Batches()
        );
    }

    [Fact]
    public void WhereTwiceThrowsRatherThanReplacingThePredicate()
    {
        using var stream = new MemoryStream();
        var ex = Should.Throw<NotSupportedException>(() =>
            BuilderOrderParquet.From(stream).Where(m => m.Id.Min >= 1).Where(m => m.Id.Max <= 9)
        );
        ex.Message.ShouldContain("single predicate");
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Should.Throw<ArgumentNullException>(() => BuilderOrderParquet.From((Stream)null!));
        using var stream = new MemoryStream();
        Should.Throw<ArgumentNullException>(() =>
            BuilderOrderParquet.From(stream).WithOptions(null!)
        );
        Should.Throw<ArgumentNullException>(() => BuilderOrderParquet.From(stream).Where(null!));

        // Unchanged from the #217 state types: every state rejects null options.
        var memory = new ReadOnlyMemory<byte>(new byte[16]);
        Should.Throw<ArgumentNullException>(() =>
            BuilderOrderParquet.From(memory).Parallel().WithOptions(null!)
        );
        Should.Throw<ArgumentNullException>(() =>
            BuilderOrderParquet.From(memory).Where(m => true).WithOptions(null!)
        );
    }
}
