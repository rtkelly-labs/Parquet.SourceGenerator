using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Emitted async iterators validate arguments when called, not on the first MoveNextAsync (MA0050),
/// and the parallel readers cancel their linked source with CancelAsync (CA1849) (#548).
/// </summary>
public sealed class AsyncIteratorContractTests
{
    private static readonly PropertyModel[] FlatProperties =
    {
        new("Id", "id", "int", null, null, 1, null, null, PropertyKind.Primitive, false),
        new("Name", "name", "string", null, null, 2, null, null, PropertyKind.Primitive, false),
    };

    private static async Task<MemoryStream> WriteSampleAsync()
    {
        var stream = new MemoryStream();
        List<ColumnBatchOrder> rows = Enumerable
            .Range(1, 5)
            .Select(i => new ColumnBatchOrder { OrderId = i, Region = "emea" })
            .ToList();
        await rows.WriteParquetAsync(stream, new ParquetSerializerOptions { RowGroupSize = 2 });
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void RowEnumerableThrowsAtCallTimeForNullStream()
    {
        // No await foreach, no MoveNextAsync: the exception must come from the call itself.
        Should.Throw<ArgumentNullException>(() =>
            ColumnBatchOrderParquetExtensions.ReadEnumerableCoreAsync((Stream)null!)
        );
    }

    [Fact]
    public void BatchesThrowAtCallTimeForNullStream()
    {
        Should.Throw<ArgumentNullException>(() =>
            ColumnBatchOrderParquetExtensions.ReadBatchesCoreAsync((Stream)null!)
        );
    }

    [Fact]
    public async Task RowEnumerableStillObservesWithCancellationTokenAsync()
    {
        using MemoryStream stream = await WriteSampleAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (
                ColumnBatchOrder _ in ColumnBatchOrderParquetExtensions
                    .ReadEnumerableCoreAsync(stream)
                    .WithCancellation(cts.Token)
            )
            {
                // never reached
            }
        });
    }

    [Fact]
    public async Task AsBatchesStillObserveWithCancellationTokenAsync()
    {
        using MemoryStream stream = await WriteSampleAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (
                ColumnBatchOrderBatch _ in ColumnBatchOrderParquetExtensions
                    .ReadBatchesCoreAsync(stream)
                    .WithCancellation(cts.Token)
            )
            {
                // never reached
            }
        });
    }

    [Fact]
    public void ValidatingWrappersAreNotIterators()
    {
        string source = CodeEmitter.EmitSource(
            new TargetClassModel(
                Namespace: "TestNamespace",
                ClassName: "TestEntity",
                Properties: new EquatableArray<PropertyModel>(FlatProperties)
            )
        );

        source.ShouldContain(
            "internal static global::System.Collections.Generic.IAsyncEnumerable<TestEntity> ReadEnumerableCoreAsync("
        );
        source.ShouldContain(
            "private static async global::System.Collections.Generic.IAsyncEnumerable<TestEntity> ReadEnumerableIteratorAsync("
        );
        source.ShouldContain("return ReadEnumerableIteratorAsync(");
    }

    [Fact]
    public void ParallelReadersCancelWithCancelAsync()
    {
        string source = CodeEmitter.EmitSource(
            new TargetClassModel(
                Namespace: "TestNamespace",
                ClassName: "TestEntity",
                Properties: new EquatableArray<PropertyModel>(FlatProperties)
            )
        );

        source.ShouldContain("await linkedCts.CancelAsync().ConfigureAwait(false);");
        source.ShouldNotContain("linkedCts.Cancel();");
    }
}
