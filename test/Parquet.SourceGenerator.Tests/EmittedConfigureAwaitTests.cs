using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Behavioural pins for #418 / #423 / #425: emitted awaits must not capture the caller's
/// <see cref="SynchronizationContext"/>, and the streaming write must flow its cancellation token
/// into the source <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
public class EmittedConfigureAwaitTests
{
    private static StreamingChunkModel Item(int i) =>
        new()
        {
            Id = i,
            Text = $"Text_{i}",
            OptionalText = i % 2 == 0 ? null : $"Opt_{i}",
            Data = new byte[] { (byte)i },
            GuidVal = Guid.NewGuid(),
            Amount = 1.25m + i,
        };

    private sealed class CancellationProbe
    {
        public volatile bool SourceObservedCancellation;
    }

    private static async IAsyncEnumerable<StreamingChunkModel> BlockingSource(
        CancellationProbe probe,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        yield return Item(0);

        // Parks until the token handed to the ENUMERATOR is cancelled. Without WithCancellation on
        // the emitted await foreach that token is default and this never completes (#425).
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            probe.SourceObservedCancellation = true;
            throw;
        }

        yield return Item(1);
    }

    [Fact]
    public async Task StreamingWriteFlowsCancellationIntoTheSourceEnumeratorAsync()
    {
        var probe = new CancellationProbe();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        using var stream = new MemoryStream();

        OperationCanceledException? caught = null;
        try
        {
            await BlockingSource(probe)
                .WriteParquetAsync(stream, cancellationToken: cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }

        caught.ShouldNotBeNull();
        probe.SourceObservedCancellation.ShouldBeTrue();
    }

    /// <summary>A context that never runs anything: a captured continuation would hang forever.</summary>
    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private int _posts;

        public int PostCount => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state) =>
            Interlocked.Increment(ref _posts);

        public override void Send(SendOrPostCallback d, object? state) =>
            Interlocked.Increment(ref _posts);
    }

    private static void RunUnderNonPumpingContext(Action work)
    {
        var context = new NonPumpingSynchronizationContext();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();

        thread
            .Join(TimeSpan.FromSeconds(60))
            .ShouldBeTrue("an emitted await captured the SynchronizationContext and deadlocked");
        failure.ShouldBeNull();
        context.PostCount.ShouldBe(0);
    }

    [Fact]
    public void GeneratedWriteAndReadCompleteUnderABlockedSynchronizationContext()
    {
        var items = Enumerable.Range(0, 12).Select(Item).ToList();

        RunUnderNonPumpingContext(() =>
        {
            using var stream = new MemoryStream();
            items
                .WriteParquetAsync(stream, new ParquetSerializerOptions { RowGroupSize = 5 })
                .GetAwaiter()
                .GetResult();

            stream.Position = 0;
            StreamingChunkModel[] sequential = StreamingChunkModelParquet
                .From(stream)
                .ToArrayAsync()
                .GetAwaiter()
                .GetResult();
            sequential.Length.ShouldBe(items.Count);

            // Buffer-backed builder: the await foreach over ReadEnumerableCoreAsync (#423).
            StreamingChunkModel[] fromBytes = StreamingChunkModelParquet
                .From(stream.ToArray())
                .ToArrayAsync()
                .GetAwaiter()
                .GetResult();
            fromBytes.Length.ShouldBe(items.Count);

            StreamingChunkModel[] parallel = StreamingChunkModelParquet
                .From(stream.ToArray())
                .Parallel()
                .ToArrayAsync()
                .GetAwaiter()
                .GetResult();
            parallel.Length.ShouldBe(items.Count);
        });
    }
}
