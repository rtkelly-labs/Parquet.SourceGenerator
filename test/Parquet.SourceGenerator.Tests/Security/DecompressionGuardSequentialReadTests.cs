using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static Parquet.SourceGenerator.Tests.Security.DecompressionGuardTests;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Pins #360: a page header reached by reading on from the previous page, with no seek, is
/// validated like one reached by a seek. Parquet.Net 4.x (the legacy backend) seeks once to the
/// start of a column chunk and then reads its pages one after another, so a limit that only
/// checked seek targets bounded the first page of each chunk and nothing after it.
/// </summary>
/// <remarks>
/// Run against the guard compiled into this assembly and against the modern and legacy emitters'
/// guard compiled at C# 7.3 with no target framework symbols (what net472 sees). The Span and
/// Memory overloads go through the base class on the C# 7.3 flavours, which calls the array
/// overloads, so one read API each is exercised for every flavour.
/// </remarks>
#pragma warning disable CA1835 // the array overload is the API under test
public sealed class DecompressionGuardSequentialReadTests
{
    private const int FirstUncompressed = 64;
    private const int FirstCompressed = 32;
    private static readonly string[] ReadApis =
    [
        "Read",
        "ReadByte",
        "ReadAsync",
        "ReadSpan",
        "ReadAsyncMemory",
    ];

    public static IEnumerable<object[]> Flavours() => DecompressionGuardHarness.Flavours();

    public static IEnumerable<object[]> FlavoursAndReadApis() =>
        DecompressionGuardHarness
            .Flavours()
            .SelectMany(flavour => ReadApis.Select(api => new[] { flavour[0], api }));

    private static async Task<int> ReadWithAsync(string api, Stream stream, int count)
    {
        var buffer = new byte[count];
        switch (api)
        {
            case "Read":
                return stream.Read(buffer, 0, count);
            case "ReadByte":
                return stream.ReadByte() < 0 ? 0 : 1;
            case "ReadAsync":
                return await stream.ReadAsync(buffer, 0, count, CancellationToken.None);
            case "ReadSpan":
                return stream.Read(buffer.AsSpan());
            default:
                return await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        }
    }

    /// <summary>[junk][first page: header and 32 payload bytes][second header][its payload][tail]</summary>
    private static (MemoryStream File, long FirstPayloadStart, long SecondHeaderStart) TwoPages(
        byte[] secondHeader,
        int secondPayload
    )
    {
        byte[] first = Canonical(FirstUncompressed, FirstCompressed);
        byte[] bytes = Concat(
            new byte[HeaderOffset],
            first,
            new byte[FirstCompressed],
            secondHeader,
            new byte[secondPayload],
            new byte[16]
        );
        return (
            new MemoryStream(bytes, writable: false),
            HeaderOffset + first.Length,
            HeaderOffset + first.Length + FirstCompressed
        );
    }

    private const int HeaderOffset = 4;

    private static byte[] HostileSecondHeader() => Canonical(100_000_000, 100_000);

    private static DecompressionGuardHarness.Guard ActivatedAtFirstPage(
        string flavour,
        MemoryStream file
    )
    {
        DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            file,
            ownsInner: true
        );
        guard.Activate();
        guard.Stream.Seek(HeaderOffset, SeekOrigin.Begin);
        return guard;
    }

    /// <summary>
    /// Reads the first page's payload exactly to its end, so the stream sits on the second header
    /// without any seek having been made to it.
    /// </summary>
    private static void ReadToTheEndOfTheFirstPage(
        DecompressionGuardHarness.Guard guard,
        long payloadStart
    )
    {
        guard.Stream.Position = payloadStart;
        guard.Stream.Read(new byte[FirstCompressed], 0, FirstCompressed).ShouldBe(FirstCompressed);
    }

    [Theory]
    [MemberData(nameof(FlavoursAndReadApis))]
    public async Task ReadingOnIntoAnOversizeNextPageIsRejectedAsync(string flavour, string api)
    {
        (MemoryStream file, long payloadStart, long secondHeader) = TwoPages(
            HostileSecondHeader(),
            secondPayload: 100_000
        );
        using DecompressionGuardHarness.Guard guard = ActivatedAtFirstPage(flavour, file);

        ReadToTheEndOfTheFirstPage(guard, payloadStart);
        guard.Stream.Position.ShouldBe(secondHeader);

        var ex = await Should.ThrowAsync<InvalidDataException>(() =>
            ReadWithAsync(api, guard.Stream, 16)
        );
        ex.Message.ShouldContain("exceeding maximum allowed");
        ex.Message.ShouldContain("offset " + secondHeader);
    }

    [Theory]
    [MemberData(nameof(FlavoursAndReadApis))]
    public async Task ReadingOnIntoAnUnparseableNextPageIsRejectedAsync(string flavour, string api)
    {
        (MemoryStream file, long payloadStart, _) = TwoPages(
            Enumerable.Repeat((byte)0xFF, 24).ToArray(),
            secondPayload: 64
        );
        using DecompressionGuardHarness.Guard guard = ActivatedAtFirstPage(flavour, file);
        ReadToTheEndOfTheFirstPage(guard, payloadStart);

        await Should.ThrowAsync<InvalidDataException>(() => ReadWithAsync(api, guard.Stream, 16));
    }

    [Theory]
    [MemberData(nameof(FlavoursAndReadApis))]
    public async Task ReadingOnIntoAValidNextPageSucceedsAsync(string flavour, string api)
    {
        (MemoryStream file, long payloadStart, _) = TwoPages(
            Canonical(FirstUncompressed, FirstCompressed),
            secondPayload: FirstCompressed
        );
        using DecompressionGuardHarness.Guard guard = ActivatedAtFirstPage(flavour, file);
        ReadToTheEndOfTheFirstPage(guard, payloadStart);

        (await ReadWithAsync(api, guard.Stream, 16)).ShouldBeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public async Task ASingleReadAcrossThePageBoundaryStopsAtItAsync(string flavour)
    {
        (MemoryStream file, long payloadStart, long secondHeader) = TwoPages(
            HostileSecondHeader(),
            secondPayload: 100_000
        );
        using DecompressionGuardHarness.Guard guard = ActivatedAtFirstPage(flavour, file);
        guard.Stream.Position = payloadStart;

        var buffer = new byte[4096];
        int read = guard.Stream.Read(buffer, 0, buffer.Length);

        read.ShouldBe((int)(secondHeader - payloadStart));
        guard.Stream.Position.ShouldBe(secondHeader);
        await Should.ThrowAsync<InvalidDataException>(() =>
            guard.Stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
        );
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ReadingEveryPageOfAValidChunkReturnsEveryByte(string flavour)
    {
        (MemoryStream file, _, _) = TwoPages(
            Canonical(FirstUncompressed, FirstCompressed),
            secondPayload: FirstCompressed
        );
        using DecompressionGuardHarness.Guard guard = ActivatedAtFirstPage(flavour, file);

        // Reading with a buffer larger than any page is shortened at each boundary and carries on.
        // Stopping at the end of the second payload: nothing a real reader does reads on past the
        // last page of a chunk, and what follows it is not a page header.
        long expected = file.Length - HeaderOffset - 16;
        long total = 0;
        var buffer = new byte[4096];
        guard.Stream.Position = HeaderOffset;
        while (total < expected)
        {
            int read = guard.Stream.Read(buffer, 0, buffer.Length);
            read.ShouldBeGreaterThan(0);
            total += read;
        }

        total.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void ReadsAreNotShortenedBeforeActivation(string flavour)
    {
        (MemoryStream file, _, _) = TwoPages(HostileSecondHeader(), secondPayload: 100_000);
        using DecompressionGuardHarness.Guard guard = DecompressionGuardHarness.Create(
            flavour,
            file,
            ownsInner: true
        );

        var buffer = new byte[4096];

        guard.Stream.Read(buffer, 0, buffer.Length).ShouldBe(buffer.Length);
    }

    [Theory]
    [MemberData(nameof(Flavours))]
    public void AReadInsideThePayloadIsNotShortened(string flavour)
    {
        (MemoryStream file, long payloadStart, _) = TwoPages(
            Canonical(FirstUncompressed, FirstCompressed),
            secondPayload: FirstCompressed
        );
        using DecompressionGuardHarness.Guard guard = ActivatedAtFirstPage(flavour, file);
        guard.Stream.Position = payloadStart;

        var buffer = new byte[FirstCompressed - 1];

        guard.Stream.Read(buffer, 0, buffer.Length).ShouldBe(buffer.Length);
    }
}
