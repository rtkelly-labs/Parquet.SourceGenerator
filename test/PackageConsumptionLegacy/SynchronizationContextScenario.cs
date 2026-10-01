using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet.SourceGenerator;

namespace PackageConsumptionLegacy;

/// <summary>
/// Runs the emitted write and read paths with the caller blocked on a single-threaded
/// SynchronizationContext, the way a classic ASP.NET, WinForms or WPF caller on .NET Framework
/// calls an async API with <c>.GetAwaiter().GetResult()</c> (#565). Each call runs on a dedicated
/// thread whose context never pumps, so a continuation posted back to it shows up as a timeout
/// instead of a hang. A positive control (a bare await over a stream that completes on another
/// thread) must time out first, so a harness that cannot detect a deadlock fails.
///
/// What this does and does not prove. The emitted code puts <c>ConfigureAwait(false)</c> on every
/// await, and nothing here can strengthen that on a stream that suspends: Parquet.Net 4.25
/// ParquetReader.CreateAsync and ParquetWriter column writes do not use it themselves, so a
/// round trip over a genuinely asynchronous stream deadlocks inside Parquet.Net whatever the
/// emitted code does (verified by calling Parquet.Net directly). The round trip therefore uses a
/// synchronously completing stream. It catches emitted code that blocks or posts to the caller
/// context on its own, and it runs under the target runtime.
/// </summary>
internal static class SynchronizationContextScenario
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static bool Run()
    {
        // Positive control: an await without ConfigureAwait(false) must deadlock here.
        var gated = new GatedStream(new MemoryStream());
        if (!BlocksOnContext(() => BareAwaitAsync(gated), ControlTimeout, out _, gated.Release))
        {
            Console.Error.WriteLine(
                "FAILED: the synchronization-context harness did not detect a deadlock; "
                    + "its result would prove nothing."
            );
            return false;
        }

        List<Measurement> rows = Enumerable
            .Range(0, 250)
            .Select(i => new Measurement
            {
                Id = i,
                Val = i * 1.5,
                Label = "r" + i,
            })
            .ToList();

        var stream = new MemoryStream();

        if (
            BlocksOnContext(
                () => rows.WriteParquetAsync(stream),
                Timeout,
                out Exception? writeError
            )
        )
        {
            Console.Error.WriteLine(
                "FAILED: WriteParquetAsync did not complete while its caller was blocked on a "
                    + "single-threaded SynchronizationContext."
            );
            return false;
        }

        if (writeError != null)
        {
            Console.Error.WriteLine($"FAILED: WriteParquetAsync threw: {writeError}");
            return false;
        }

        stream.Position = 0;
        List<Measurement>? read = null;
        bool readBlocked = BlocksOnContext(
            () => ReadAsync(stream, r => read = r),
            Timeout,
            out Exception? readError
        );
        if (readBlocked)
        {
            Console.Error.WriteLine(
                "FAILED: ReadParquetAsync did not complete while its caller was blocked on a "
                    + "single-threaded SynchronizationContext."
            );
            return false;
        }

        if (readError != null)
        {
            Console.Error.WriteLine($"FAILED: ReadParquetAsync threw: {readError}");
            return false;
        }

        if (read is null || read.Count != rows.Count || read[249].Label != "r249")
        {
            Console.Error.WriteLine(
                $"FAILED: blocked-context round trip returned {read?.Count.ToString() ?? "nothing"} rows, expected {rows.Count}."
            );
            return false;
        }

        Console.WriteLine(
            $"SynchronizationContext scenario OK: wrote and read {read.Count} rows with the caller blocked "
                + "(the control, a bare await over an asynchronous stream, deadlocked as required)."
        );
        return true;
    }

    private static async Task BareAwaitAsync(Stream stream)
    {
        // Deliberately no ConfigureAwait(false): this is the control.
        await stream.FlushAsync();
    }

    private static async Task ReadAsync(Stream stream, Action<List<Measurement>> capture)
    {
        // The wrapper's own await uses ConfigureAwait(false) so only the library is under test.
        capture(
            await MeasurementParquetLegacyExtensions.ReadParquetAsync(stream).ConfigureAwait(false)
        );
    }

    /// <summary>
    /// Starts <paramref name="start"/> on a thread whose SynchronizationContext never runs its
    /// queue, then blocks that thread on the task with <c>.GetAwaiter().GetResult()</c>. Returns
    /// true when the task did not finish within <paramref name="timeout"/>.
    /// </summary>
    private static bool BlocksOnContext(
        Func<Task> start,
        TimeSpan timeout,
        out Exception? error,
        Action? afterStart = null
    )
    {
        bool timedOut = false;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            try
            {
                Task task = start();
                afterStart?.Invoke();
                if (task.Wait(timeout))
                {
                    task.GetAwaiter().GetResult();
                }
                else
                {
                    timedOut = true;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        // Bounded: start() itself may block, which would never reach the task wait above.
        if (!thread.Join(timeout + TimeSpan.FromSeconds(5)))
        {
            timedOut = true;
        }

        error = failure;
        return timedOut;
    }

    /// <summary>A context that accepts work and never runs it, like a blocked UI thread.</summary>
    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Dropped on purpose: the thread that owns this context is blocked.
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("Send onto a blocked context would deadlock.");
    }

    /// <summary>
    /// Every asynchronous operation waits for <see cref="Release"/> before touching the inner stream,
    /// so an await over it suspends until the harness has the task in hand: a handshake, not a timer
    /// that a descheduled thread could outrun.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly Stream _inner;

        private readonly TaskCompletionSource<bool> _gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public GatedStream(Stream inner) => _inner = inner;

        public void Release() => _gate.TrySetResult(true);

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            await _gate.Task.ConfigureAwait(false);
            return _inner.Read(buffer, offset, count);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            await _gate.Task.ConfigureAwait(false);
            _inner.Write(buffer, offset, count);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);
            _inner.Flush();
        }
    }
}
