using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
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
        List<ParallelRow> rows = Enumerable
            .Range(1, rowCount)
            .Select(i => new ParallelRow
            {
                Id = i,
                Name = $"row_{i}",
                Score = i % 5 == 0 ? null : i * 0.5,
            })
            .ToList();

        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
        );
        return stream.ToArray();
    }

    [Fact]
    public async Task RowsComeBackInFileOrderAcrossManyRowGroups()
    {
        // Enough row groups that more than one worker is genuinely in play, and an uneven tail so
        // the last group is a different size from the rest.
        byte[] bytes = await WriteAsync(rowCount: 1_050, rowGroupSize: 100);

        ParallelRow[] read = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .Parallel()
            .ToArrayAsync();

        read.Length.ShouldBe(1_050);
        read.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 1_050));
    }

    [Fact]
    public async Task ParallelReadMatchesTheSequentialReadExactly()
    {
        byte[] bytes = await WriteAsync(rowCount: 1_050, rowGroupSize: 100);

        ParallelRow[] sequential = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .ToArrayAsync();
        ParallelRow[] parallel = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .Parallel()
            .ToArrayAsync();

        // Records compare structurally, so this covers the nullable column and the string column
        // as well as ordering — a buffer handed between workers would show up as a shifted value.
        parallel.ShouldBe(sequential);
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

        ParallelRow[] read = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .WithOptions(
                new ParquetSerializerOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }
            )
            .Parallel()
            .ToArrayAsync();

        read.Length.ShouldBe(500);
        read.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 500));
        read[249].Name.ShouldBe("row_250");
        read[4].Score.ShouldBeNull();
    }

    [Fact]
    public async Task SingleRowGroupFileReadsWithoutDividingTheWork()
    {
        // workerCount collapses to 1 here, which takes the branch that stays on the calling thread
        // instead of paying for a thread-pool hop and a second reader.
        byte[] bytes = await WriteAsync(rowCount: 25, rowGroupSize: 1_000);

        ParallelRow[] read = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 16 })
            .Parallel()
            .ToArrayAsync();

        read.Length.ShouldBe(25);
        read.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 25));
    }

    [Fact]
    public async Task OptionsSupplyTheParallelism()
    {
        byte[] bytes = await WriteAsync(rowCount: 300, rowGroupSize: 50);

        ParallelRow[] read = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 2 })
            .Parallel()
            .ToArrayAsync();

        read.Length.ShouldBe(300);
        read.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 300));
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        byte[] bytes = await WriteAsync(rowCount: 500, rowGroupSize: 50);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ParallelRowParquet
                .From(new ReadOnlyMemory<byte>(bytes))
                .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
                .Parallel()
                .ToArrayAsync(cts.Token)
        );
    }

    [Fact]
    public async Task SlicedBufferIsHonouredByEveryWorker()
    {
        // Each worker builds its own stream from the same ReadOnlyMemory, so an offset dropped in
        // CreateBufferStream would corrupt every worker but the probe.
        byte[] bytes = await WriteAsync(rowCount: 400, rowGroupSize: 50);
        byte[] padded = new byte[bytes.Length + 16];
        bytes.CopyTo(padded, 8);

        ParallelRow[] read = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(padded, 8, bytes.Length))
            .Parallel()
            .ToArrayAsync();

        read.Length.ShouldBe(400);
        read.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 400));
    }

    /// <summary>
    /// A buffer that is not array-backed takes <c>CreateBufferStream</c>'s copying fallback. Because
    /// every worker builds its own stream, that fallback ran once per worker plus once for the probe
    /// — the whole file materialised N+1 times, which is precisely the allocation profile the buffer
    /// overload exists to avoid. The buffer is now normalised to an array once, up front.
    /// </summary>
    /// <remarks>
    /// This one asserts the path reads correctly. The copy count itself is pinned by
    /// <see cref="EmittedParallelReaderNormalisesTheBufferOnceRatherThanPerWorker"/> against the
    /// emitted source, and measured by the <c>SourceGeneratorReadParallelBufferAsync</c> benchmark,
    /// whose allocation figure the regression gate watches.
    /// </remarks>
    [Fact]
    public async Task NonArrayBackedBufferStillReadsCorrectly()
    {
        byte[] bytes = await WriteAsync(rowCount: 400, rowGroupSize: 50);
        using var owner = new NonArrayBackedBuffer(bytes);
        ReadOnlyMemory<byte> memory = owner.Memory;

        MemoryMarshal
            .TryGetArray(memory, out _)
            .ShouldBeFalse(
                "The buffer must not be array-backed, or this test exercises the wrong branch."
            );

        ParallelRow[] read = await ParallelRowParquet
            .From(memory)
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
            .Parallel()
            .ToArrayAsync();

        read.Length.ShouldBe(400);
        read.Select(r => r.Id).ShouldBe(Enumerable.Range(1, 400));
    }

    /// <summary>
    /// Pins the copy count in the emitted source, where it is deterministic.
    /// </summary>
    /// <remarks>
    /// A runtime assertion would have to measure process-wide allocations, which the rest of the
    /// suite pollutes because xUnit runs classes in parallel — so it would be flaky in exactly the
    /// way that gets a test deleted. The emitted source says the same thing without the noise:
    /// the buffer is normalised once, and every downstream call takes the normalised value.
    /// </remarks>
    [Fact]
    public void EmittedParallelReaderNormalisesTheBufferOnceRatherThanPerWorker()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(
                new[]
                {
                    new PropertyModel(
                        "Id",
                        "id",
                        "int",
                        null,
                        null,
                        1,
                        null,
                        null,
                        PropertyKind.Primitive,
                        false
                    ),
                }
            )
        );

        string source = CodeEmitter.EmitSource(model);

        // Normalised practical once, from the caller's buffer.
        source.ShouldContain("MemoryMarshal.TryGetArray(parquetBytes, out _)");
        Occurrences(source, "var sourceBytes =").ShouldBe(1);

        // The probe reads through the normalised value, and both worker dispatch sites — the
        // single-threaded branch and the Task.Run loop — hand it on rather than the original.
        Occurrences(source, "CreateBufferStream(sourceBytes)").ShouldBe(1);
        Occurrences(source, "ReadRowGroupsIntoAsync(sourceBytes,").ShouldBe(2);
        Occurrences(source, "ReadRowGroupsIntoAsync(parquetBytes,").ShouldBe(0);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        for (
            int i = haystack.IndexOf(needle, StringComparison.Ordinal);
            i >= 0;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Memory owned by a <see cref="MemoryManager{T}"/> reports <c>false</c> from
    /// <c>MemoryMarshal.TryGetArray</c>, which is the only way to reach the copying fallback.
    /// </summary>
    private sealed class NonArrayBackedBuffer : MemoryManager<byte>
    {
        private readonly byte[] _bytes;

        public NonArrayBackedBuffer(byte[] bytes) => _bytes = bytes;

        public override Span<byte> GetSpan() => _bytes;

        // The read path needs only TryGetArray and ToArray, neither of which pins.
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() => throw new NotSupportedException();

        protected override void Dispose(bool disposing) { }
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
            ParallelRow[][] results = await Task.WhenAll(
                Enumerable
                    .Range(0, 4)
                    .Select(_ =>
                        ParallelRowParquet
                            .From(new ReadOnlyMemory<byte>(bytes))
                            .WithOptions(
                                new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 }
                            )
                            .Parallel()
                            .ToArrayAsync()
                    )
            );

            foreach (ParallelRow[] result in results)
            {
                result.Select(r => r.Id).ShouldBe(expected);
            }
        }
    }

    [Fact]
    public async Task ToArrayAsyncStreamAndBufferReturnsNativeArray()
    {
        byte[] bytes = await WriteAsync(rowCount: 50, rowGroupSize: 20);

        using var stream = new MemoryStream(bytes);
        ParallelRow[] fromStream = await ParallelRowParquet.From(stream).ToArrayAsync();
        fromStream.Length.ShouldBe(50);
        fromStream[0].Id.ShouldBe(1);
        fromStream[49].Id.ShouldBe(50);

        ParallelRow[] fromBuffer = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .ToArrayAsync();
        fromBuffer.Length.ShouldBe(50);
        fromBuffer[0].Id.ShouldBe(1);
        fromBuffer[49].Id.ShouldBe(50);
    }

    [Fact]
    public async Task ParallelToArrayAsyncReturnsNativeArray()
    {
        // The stream half of this test covered the removed stream "parallel" overload, which read
        // sequentially; the builder offers Parallel() only on a buffer (#480, docs/48).
        byte[] bytes = await WriteAsync(rowCount: 100, rowGroupSize: 25);

        ParallelRow[] fromBuffer = await ParallelRowParquet
            .From(new ReadOnlyMemory<byte>(bytes))
            .WithOptions(new ParquetSerializerOptions { MaxDegreeOfParallelism = 4 })
            .Parallel()
            .ToArrayAsync();
        fromBuffer.Length.ShouldBe(100);
        fromBuffer[0].Id.ShouldBe(1);
        fromBuffer[99].Id.ShouldBe(100);
    }

    [Fact]
    public void EmittedCodeContainsThresholdOptimizationsAndDirectArrayOverloads()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(
                new[]
                {
                    new PropertyModel(
                        "Id",
                        "id",
                        "int",
                        null,
                        null,
                        1,
                        null,
                        null,
                        PropertyKind.Primitive,
                        false
                    ),
                }
            )
        );

        string source = CodeEmitter.EmitSource(model);

        // Direct array reader overloads
        source.ShouldContain("Task<TestEntity[]> ReadArrayCoreAsync(");
        source.ShouldContain("Task<TestEntity[]> ReadParallelArrayCoreAsync(");

        // Threshold fast paths
        source.ShouldContain(
            "if (items is global::System.Collections.Generic.IReadOnlyCollection<TestEntity> col && col.Count <= targetChunkSize)"
        );
        source.ShouldContain("if (rowGroupCount <= 1 || totalRows <= 10_000)");
    }
}
