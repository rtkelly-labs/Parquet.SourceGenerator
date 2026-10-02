using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;
using static Parquet.SourceGenerator.Tests.Security.DecompressionGuardTests;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Pins #374: the footer is checked before Parquet.Net parses it. Parquet.Net sizes the lists and
/// byte arrays it allocates from counts and lengths inside the footer, before it reads their
/// elements, so a footer a few bytes long can ask for gigabytes and no limit that runs after
/// <c>CreateAsync</c> can intervene. The guard sees the seek that locates the footer, so it walks
/// the footer there. Each test hand-builds a Thrift footer and makes that seek.
/// </summary>
public sealed class DecompressionGuardFooterTests
{
    public static IEnumerable<object[]> Flavours() => DecompressionGuardHarness.Flavours();

    /// <summary>An unsigned varint, as used for list sizes and byte lengths.</summary>
    internal static byte[] Varint(long value)
    {
        var bytes = new List<byte>();
        ulong raw = (ulong)value;
        while (raw >= 0x80)
        {
            bytes.Add((byte)(raw | 0x80));
            raw >>= 7;
        }

        bytes.Add((byte)raw);
        return bytes.ToArray();
    }

    internal static byte[] ListHeader(long count, int elementType) =>
        count < 15
            ? new[] { (byte)((count << 4) | elementType) }
            : Concat(new[] { (byte)(0xF0 | elementType) }, Varint(count));

    internal static byte[] EmptyStruct() => Stop;

    /// <summary>A file made of the magic, a footer body, the footer length and the magic.</summary>
    internal static byte[] FileWithFooter(byte[] footer) =>
        Concat(
            "PAR1"u8.ToArray(),
            new byte[16],
            footer,
            BitConverter.GetBytes(footer.Length),
            "PAR1"u8.ToArray()
        );

    /// <summary>version, a schema of one element, num_rows, and a row group list.</summary>
    internal static byte[] Footer(byte[] rowGroupList, byte[]? schemaList = null) =>
        Concat(
            Short(1, 5, I32(1)),
            Short(1, 9, schemaList ?? Concat(ListHeader(1, 12), EmptyStruct())),
            Short(1, 6, I32(0)),
            Short(1, 9, rowGroupList),
            Stop
        );

    private static void SeekToFooter(
        string flavour,
        byte[] file,
        int maxRowGroupCount = int.MaxValue
    )
    {
        using var inner = new MemoryStream(file, writable: false);
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner,
            maxRowGroupCount: maxRowGroupCount
        );

        long position = guard.Stream.Seek(-8, SeekOrigin.End);

        position.ShouldBe(file.Length - 8);
        inner.Position.ShouldBe(position);
    }

    private static void ShouldRejectFooter(
        string flavour,
        byte[] footer,
        string message,
        int maxRowGroupCount = int.MaxValue
    )
    {
        var ex = Should.Throw<InvalidDataException>(() =>
            SeekToFooter(flavour, FileWithFooter(footer), maxRowGroupCount)
        );
        ex.Message.ShouldContain(message);
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AWellFormedFooterIsAccepted(string flavour)
    {
        // A row group with a column chunk whose metadata carries every list the walk follows.
        byte[] metadata = Concat(
            Short(1, 5, I32(1)),
            Short(1, 9, Concat(ListHeader(2, 5), I32(0), I32(3))),
            Short(1, 9, Concat(ListHeader(1, 8), Varint(3), "abc"u8.ToArray())),
            Short(2, 5, I32(0)),
            Stop
        );
        byte[] chunk = Concat(Short(3, 12, metadata), Stop);
        byte[] rowGroup = Concat(
            Short(1, 9, Concat(ListHeader(1, 12), chunk)),
            Short(1, 6, I32(100)),
            Stop
        );
        byte[] footer = Footer(Concat(ListHeader(2, 12), rowGroup, rowGroup));

        Should.NotThrow(() => SeekToFooter(flavour, FileWithFooter(footer)));
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void FieldsTheWalkDoesNotKnowAreSkippedByType(string flavour)
    {
        byte[] footer = Concat(
            Short(1, 5, I32(1)),
            // Unknown ids with a byte, a double, a set of i32, a map of binary to i64, a list of
            // booleans (a byte each) and a struct: each skipped by its declared type.
            Long(30, 3, new byte[] { 7 }),
            Long(31, 7, new byte[8]),
            Long(32, 10, Concat(ListHeader(2, 5), I32(1), I32(2))),
            Long(
                33,
                11,
                Concat(Varint(1), new byte[] { (8 << 4) | 6 }, Varint(1), "k"u8.ToArray(), I32(5))
            ),
            Long(34, 9, Concat(ListHeader(3, 1), new byte[] { 1, 2, 1 })),
            Long(35, 12, Concat(Short(1, 5, I32(1)), Stop)),
            Short(1, 9, Concat(ListHeader(1, 12), EmptyStruct())),
            Stop
        );

        Should.NotThrow(() => SeekToFooter(flavour, FileWithFooter(footer)));
    }

    public static IEnumerable<object[]> FlavoursAndImpossibleLengths() =>
        Flavours()
            .SelectMany(flavour =>
                new[] { int.MaxValue, int.MaxValue - 7, 1_000_000, 0, -1, int.MinValue }.Select(
                    length => new[] { flavour[0], length }
                )
            );

    [Theory]
    [MemberData(nameof(FlavoursAndImpossibleLengths))]
    public void AFooterLengthTheStreamCannotHoldIsRefused(string flavour, int storedLength)
    {
        // The stored length is a signed 32-bit integer. Parquet.Net computes the footer's start
        // from it in 32-bit arithmetic, which wraps for a value near int.MaxValue and goes negative
        // for a negative one, so the value itself is what has to be refused.
        byte[] file = FileWithFooter(Footer(Concat(ListHeader(1, 12), EmptyStruct())));
        BitConverter.GetBytes(storedLength).CopyTo(file, file.Length - 8);

        var ex = Should.Throw<InvalidDataException>(() => SeekToFooter(flavour, file));
        ex.Message.ShouldContain("does not fit");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ARowGroupListLongerThanTheFooterIsRefused(string flavour) =>
        // Parquet.Net allocates a List of this capacity before it reads a single element.
        ShouldRejectFooter(flavour, Footer(ListHeader(20_000_000, 12)), "20000000 elements in the");

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ASchemaListLongerThanTheFooterIsRefused(string flavour) =>
        ShouldRejectFooter(
            flavour,
            Footer(
                Concat(ListHeader(1, 12), EmptyStruct()),
                schemaList: ListHeader(20_000_000, 12)
            ),
            "20000000 elements in the"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AColumnChunkListLongerThanTheFooterIsRefused(string flavour)
    {
        byte[] rowGroup = Concat(Short(1, 9, ListHeader(20_000_000, 12)), Stop);

        ShouldRejectFooter(
            flavour,
            Footer(Concat(ListHeader(1, 12), rowGroup)),
            "20000000 elements in the"
        );
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ABinaryLongerThanTheFooterIsRefused(string flavour) =>
        ShouldRejectFooter(
            flavour,
            Concat(
                Short(1, 5, I32(1)),
                Short(5, 8, Varint(2_000_000_000)),
                Short(1, 9, Concat(ListHeader(1, 12), EmptyStruct())),
                Stop
            ),
            "2000000000 bytes in the"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void MoreRowGroupsThanTheConfiguredMaximumAreRefused(string flavour) =>
        ShouldRejectFooter(
            flavour,
            Footer(Concat(ListHeader(3, 12), EmptyStruct(), EmptyStruct(), EmptyStruct())),
            "Row group count 3 is invalid or exceeds maximum allowed 2",
            maxRowGroupCount: 2
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ARowGroupCountAtTheMaximumIsAccepted(string flavour)
    {
        byte[] footer = Footer(Concat(ListHeader(2, 12), EmptyStruct(), EmptyStruct()));

        Should.NotThrow(() => SeekToFooter(flavour, FileWithFooter(footer), maxRowGroupCount: 2));
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AListFieldDeclaredWithAnotherTypeIsRefused(string flavour) =>
        // Parquet.Net reads field 4 as a list whatever type it is declared with.
        ShouldRejectFooter(
            flavour,
            Concat(Short(1, 5, I32(1)), Short(3, 5, I32(7)), Stop),
            "but it is a list"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ARowGroupListWhoseElementsAreNotStructsIsRefused(string flavour) =>
        ShouldRejectFooter(
            flavour,
            Footer(Concat(ListHeader(1, 5), I32(1))),
            "whose elements are not structs"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ATruncatedFooterIsRefused(string flavour) =>
        // The footer ends before the struct's stop byte.
        ShouldRejectFooter(
            flavour,
            Concat(Short(1, 5, I32(1)), Short(1, 9, Concat(ListHeader(1, 12), EmptyStruct()))),
            "is truncated"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AnUnsupportedCompactTypeIsRefused(string flavour) =>
        ShouldRejectFooter(
            flavour,
            Concat(Long(30, 13, Array.Empty<byte>()), Stop),
            "unsupported compact type 13"
        );

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AFooterNestedTooDeeplyIsRefused(string flavour)
    {
        byte[] nested = Stop;
        for (int i = 0; i < 40; i++)
        {
            nested = Concat(Long(30, 12, nested), Stop);
        }

        ShouldRejectFooter(flavour, nested, "nested too deeply");
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void TheFooterIsNotWalkedForOtherSeeksFromTheEnd(string flavour)
    {
        // Parquet.Net checks the trailing magic with a seek of -4 from the end, and finds the footer
        // length with -8. Neither locates the footer, so neither is walked.
        byte[] file = FileWithFooter(Footer(Concat(ListHeader(1, 12), EmptyStruct())));
        using var inner = new MemoryStream(file, writable: false);
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            inner
        );

        Should.NotThrow(() =>
        {
            guard.Stream.Seek(-4, SeekOrigin.End);
            guard.Stream.Seek(-8, SeekOrigin.End);
            guard.Stream.Seek(0, SeekOrigin.End);
        });
    }
}
