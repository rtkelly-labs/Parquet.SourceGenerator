using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static Parquet.SourceGenerator.Tests.Security.DecompressionGuardTests;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Pins #463: the guard never stops validating without saying so. Before, an I/O failure while it
/// read a page header was swallowed and the read carried on with no limit applied, leaving nothing
/// for an operator to observe. Now the failure reaches the caller as the I/O error it is, and so
/// does every other way the guard cannot validate a page (see <see cref="DecompressionGuardTests"/>).
/// </summary>
public sealed class DecompressionGuardFailureTests
{
    private const int HeaderOffset = 4;

    public static IEnumerable<object[]> Flavours() => DecompressionGuardHarness.Flavours();

    /// <summary>
    /// A stream whose single-byte reads fail once armed, the way a dropped connection does. The
    /// guard reads a page header a byte at a time, and the caller's own reads use the block
    /// overloads, so only the guard's validation meets the failure.
    /// </summary>
    private sealed class FailingStream : MemoryStream
    {
        public FailingStream(byte[] bytes)
            : base(bytes, writable: false) { }

        public bool Armed { get; set; }

        public override int ReadByte() =>
            Armed ? throw new IOException("simulated I/O failure") : base.ReadByte();
    }

    private static byte[] TwoPageFile() =>
        Concat(
            new byte[HeaderOffset],
            Canonical(uncompressed: 64, compressed: 32),
            new byte[32],
            Canonical(uncompressed: 64, compressed: 32),
            new byte[32],
            new byte[16]
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AnIoFailureReadingAHeaderAtASeekTargetReachesTheCaller(string flavour)
    {
        using var inner = new FailingStream(TwoPageFile());
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );
        guard.Activate();
        inner.Armed = true;

        var ex = Should.Throw<IOException>(() => guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin));
        ex.Message.ShouldBe("simulated I/O failure");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public async Task AnIoFailureReadingTheNextHeaderReachesTheCallerAsync(string flavour)
    {
        using var inner = new FailingStream(TwoPageFile());
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );
        guard.Activate();
        guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin);
        int firstHeaderLength = Canonical(uncompressed: 64, compressed: 32).Length;
        guard.Stream.Position = HeaderOffset + firstHeaderLength;
        guard.Stream.Read(new byte[32], 0, 32).ShouldBe(32);
        inner.Armed = true;

        await Should.ThrowAsync<IOException>(() =>
            guard.Stream.ReadAsync(new byte[8], 0, 8, CancellationToken.None)
        );
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void TheStreamPositionIsRestoredWhenValidationFails(string flavour)
    {
        using var inner = new FailingStream(TwoPageFile());
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );
        guard.Activate();
        inner.Position = 10;
        inner.Armed = true;

        Should.Throw<IOException>(() => guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin));

        inner.Armed = false;
        inner.Position.ShouldBe(HeaderOffset);
    }
}
