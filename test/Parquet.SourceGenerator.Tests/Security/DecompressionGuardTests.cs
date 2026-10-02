using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Pins the decompression guard's page header handling with hand-built Thrift compact-protocol
/// headers (#359). Each case puts one header at a known offset, activates the guard and seeks to it,
/// the way Parquet.Net does when it opens a column chunk.
/// </summary>
/// <remarks>
/// Every test runs against the guard compiled into this assembly, and against the modern and legacy
/// emitters' guard compiled at C# 7.3 without target framework symbols (what net472 sees), so the
/// shared component cannot be fixed for one backend only.
/// </remarks>
public sealed class DecompressionGuardTests
{
    private const int HeaderOffset = 4;
    private const int Oversize = 100_000_000;

    public static IEnumerable<object[]> Flavours() => DecompressionGuardHarness.Flavours();

    // Thrift compact protocol building blocks.
    internal static byte[] ZigZag(int value)
    {
        uint raw = (uint)((value << 1) ^ (value >> 31));
        var bytes = new List<byte>();
        while (raw >= 0x80)
        {
            bytes.Add((byte)(raw | 0x80));
            raw >>= 7;
        }
        bytes.Add((byte)raw);
        return bytes.ToArray();
    }

    internal static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A field header in the short form: the id delta in the high nibble.</summary>
    internal static byte[] Short(int delta, int type, byte[] payload) =>
        Concat(new[] { (byte)((delta << 4) | type) }, payload);

    /// <summary>A field header in the long form: a zero delta, then the id as a zigzag varint.</summary>
    internal static byte[] Long(int id, int type, byte[] payload) =>
        Concat(new[] { (byte)type }, ZigZag(id), payload);

    internal static byte[] I32(int value) => ZigZag(value);

    internal static readonly byte[] Stop = { 0 };

    /// <summary>The struct Parquet.Net writes for a v1 data page: four i32 fields.</summary>
    internal static byte[] DataPageHeaderStruct() =>
        Concat(
            Short(1, 5, I32(1)),
            Short(1, 5, I32(0)),
            Short(1, 5, I32(0)),
            Short(1, 5, I32(0)),
            Stop
        );

    internal static byte[] Canonical(int uncompressed, int compressed) =>
        Concat(
            Short(1, 5, I32(0)),
            Short(1, 5, I32(uncompressed)),
            Short(1, 5, I32(compressed)),
            Short(2, 12, DataPageHeaderStruct()),
            Stop
        );

    /// <summary>Lays a header out at <see cref="HeaderOffset"/>, followed by its payload and a tail.</summary>
    private static MemoryStream FileWith(byte[] header, int payloadBytes)
    {
        var bytes = new List<byte>(new byte[HeaderOffset]);
        bytes.AddRange(header);
        bytes.AddRange(Enumerable.Repeat((byte)0xAB, payloadBytes));
        bytes.AddRange(new byte[16]);
        return new MemoryStream(bytes.ToArray(), writable: false);
    }

    private static void SeekToHeader(string flavour, byte[] header, int payloadBytes = 8)
    {
        using MemoryStream inner = FileWith(header, payloadBytes);
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );
        guard.Activate();
        guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin);
    }

    private static void ShouldRejectHeader(string flavour, byte[] header, string message)
    {
        var ex = Should.Throw<InvalidDataException>(() => SeekToHeader(flavour, header));
        ex.Message.ShouldContain(message);
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void CanonicalHeaderWithinLimitsIsAccepted(string flavour) =>
        Should.NotThrow(() => SeekToHeader(flavour, Canonical(uncompressed: 64, compressed: 8)));

    [Theory]
    [MemberData(nameof(Flavours))]
    public void CanonicalHeaderOverTheLimitIsRejected(string flavour) =>
        ShouldRejectHeader(
            flavour,
            Canonical(Oversize, compressed: 100_000),
            "exceeding maximum allowed"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void FieldsInAnotherOrderAreStillValidated(string flavour)
    {
        // compressed_page_size (3) before uncompressed_page_size (2): the second needs the long
        // form because the id goes backwards.
        byte[] header = Concat(
            Short(1, 5, I32(0)),
            Short(2, 5, I32(100_000)),
            Long(2, 5, I32(Oversize)),
            Short(3, 12, DataPageHeaderStruct()),
            Stop
        );

        ShouldRejectHeader(flavour, header, "exceeding maximum allowed");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void HeaderCarryingACrcFieldIsStillValidated(string flavour)
    {
        byte[] header = Concat(
            Short(1, 5, I32(0)),
            Short(1, 5, I32(Oversize)),
            Short(1, 5, I32(100_000)),
            Short(1, 5, I32(unchecked((int)0xDEADBEEF))),
            Short(1, 12, DataPageHeaderStruct()),
            Stop
        );

        ShouldRejectHeader(flavour, header, "exceeding maximum allowed");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void LongFormFieldHeadersAreStillValidated(string flavour)
    {
        byte[] header = Concat(
            Long(1, 5, I32(0)),
            Long(2, 5, I32(Oversize)),
            Long(3, 5, I32(100_000)),
            Long(5, 12, DataPageHeaderStruct()),
            Stop
        );

        ShouldRejectHeader(flavour, header, "exceeding maximum allowed");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void NestedStructAheadOfTheSizesIsSkippedAndTheSizesAreValidated(string flavour)
    {
        byte[] header = Concat(
            Short(5, 12, DataPageHeaderStruct()),
            Long(1, 5, I32(0)),
            Long(2, 5, I32(Oversize)),
            Long(3, 5, I32(100_000)),
            Stop
        );

        ShouldRejectHeader(flavour, header, "exceeding maximum allowed");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void UnknownFieldsAreSkippedByTypeAndTheSizesAreValidated(string flavour)
    {
        byte[] header = Concat(
            Short(1, 5, I32(0)),
            Short(1, 5, I32(Oversize)),
            Short(1, 5, I32(100_000)),
            // Unknown ids 20 (binary "ab") and 21 (list of two i32).
            Long(20, 8, Concat(new byte[] { 2 }, "ab"u8.ToArray())),
            Short(1, 9, Concat(new byte[] { (2 << 4) | 5 }, I32(7), I32(9))),
            Stop
        );

        ShouldRejectHeader(flavour, header, "exceeding maximum allowed");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void LastWriterWinsOnARepeatedSizeSoARepeatIsRefused(string flavour)
    {
        // Parquet.Net keeps the last value it reads, so a small size followed by a huge one must
        // not be judged on the small one.
        byte[] header = Concat(
            Short(1, 5, I32(0)),
            Short(1, 5, I32(64)),
            Short(1, 5, I32(8)),
            Short(2, 12, DataPageHeaderStruct()),
            Long(2, 5, I32(Oversize)),
            Stop
        );

        ShouldRejectHeader(flavour, header, "repeats field 2");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ASizeFieldWithAnUnexpectedCompactTypeIsRefused(string flavour)
    {
        // Parquet.Net decodes field 2 as an i32 whatever type the header declares.
        byte[] header = Concat(
            Short(1, 5, I32(0)),
            Short(1, 6, I32(64)),
            Short(1, 5, I32(8)),
            Stop
        );

        ShouldRejectHeader(flavour, header, "compact type 6");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AMistypedFieldInsideANestedStructIsRefused(string flavour)
    {
        byte[] mistyped = Concat(
            // num_values declared as a boolean, which has no payload here but Parquet.Net would
            // still read a varint for.
            Short(1, 1, Array.Empty<byte>()),
            Short(1, 5, I32(0)),
            Stop
        );
        byte[] header = Concat(
            Short(1, 5, I32(0)),
            Short(1, 5, I32(64)),
            Short(1, 5, I32(8)),
            Short(2, 12, mistyped),
            Stop
        );

        ShouldRejectHeader(flavour, header, "compact type 1");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AHeaderMissingASizeIsRefused(string flavour) =>
        ShouldRejectHeader(
            flavour,
            Concat(Short(1, 5, I32(0)), Short(1, 5, I32(64)), Stop),
            "missing the page type or one of its sizes"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ATruncatedHeaderIsRefused(string flavour)
    {
        // The stream ends in the middle of the second field.
        var ex = Should.Throw<InvalidDataException>(() =>
        {
            using var inner = new MemoryStream(
                Concat(new byte[HeaderOffset], Short(1, 5, I32(0)), new byte[] { 0x15 }),
                writable: false
            );
            using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
                flavour,
                inner
            );
            guard.Activate();
            guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin);
        });
        ex.Message.ShouldContain("is truncated");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AnOverlongIntegerIsRefused(string flavour)
    {
        byte[] header = Concat(
            Short(1, 5, I32(0)),
            Short(1, 5, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }),
            Short(1, 5, I32(8)),
            Stop
        );

        ShouldRejectHeader(flavour, header, "overlong integer");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void BytesThatAreNotAPageHeaderAreRefused(string flavour) =>
        ShouldRejectHeader(
            flavour,
            Enumerable.Repeat((byte)0xFF, 40).ToArray(),
            "Parquet page header at offset 4"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AnUnsupportedPageTypeIsRefused(string flavour) =>
        ShouldRejectHeader(
            flavour,
            Concat(Short(1, 5, I32(9)), Short(1, 5, I32(64)), Short(1, 5, I32(8)), Stop),
            "unsupported page type 9"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ACompressedSizePastTheEndOfTheStreamIsRefused(string flavour) =>
        ShouldRejectHeader(
            flavour,
            Canonical(uncompressed: 2_000_000, compressed: 1_000_000),
            "extending beyond the end of the stream"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void SeekingWithinAValidatedPageIsNotTreatedAsAPageHeader(string flavour)
    {
        // Parquet.Net moves the base stream to each payload offset before it reads, and skips
        // fields inside a header with relative seeks. Neither is a page header, and a strict guard
        // must not parse payload bytes as one.
        byte[] header = Canonical(uncompressed: 64, compressed: 32);
        using MemoryStream inner = FileWith(header, payloadBytes: 32);
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );
        guard.Activate();
        guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin);

        long payloadStart = HeaderOffset + header.Length;
        Should.NotThrow(() =>
        {
            guard.Stream.Seek(payloadStart, SeekOrigin.Begin);
            guard.Stream.Seek(payloadStart + 7, SeekOrigin.Begin);
            guard.Stream.Seek(1, SeekOrigin.Current);
            guard.Stream.Seek(HeaderOffset + 2, SeekOrigin.Begin);
            guard.Stream.Seek(1, SeekOrigin.Current);
        });
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void SeekingPastAValidatedPageIsParsedAsTheNextHeader(string flavour)
    {
        byte[] first = Canonical(uncompressed: 64, compressed: 32);
        byte[] second = Canonical(Oversize, compressed: 100_000);
        byte[] bytes = Concat(
            new byte[HeaderOffset],
            first,
            new byte[32],
            second,
            new byte[100_016]
        );
        using var inner = new MemoryStream(bytes, writable: false);
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );
        guard.Activate();
        guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin);

        var ex = Should.Throw<InvalidDataException>(() =>
            guard.Stream.Seek(HeaderOffset + first.Length + 32, SeekOrigin.Begin)
        );
        ex.Message.ShouldContain("exceeding maximum allowed");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void TheGuardDoesNothingBeforeItIsActivated(string flavour)
    {
        using var inner = new MemoryStream(
            Enumerable.Repeat((byte)0xFF, 64).ToArray(),
            writable: false
        );
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );

        Should.NotThrow(() => guard.Stream.Seek(8, SeekOrigin.Begin));
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ValidationLeavesTheStreamPositionAtTheSeekTarget(string flavour)
    {
        byte[] header = Canonical(uncompressed: 64, compressed: 8);
        using MemoryStream inner = FileWith(header, payloadBytes: 8);
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );
        guard.Activate();

        guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin);

        guard.Stream.Position.ShouldBe(HeaderOffset);
        guard.Stream.ReadByte().ShouldBe(header[0]);
    }
}
