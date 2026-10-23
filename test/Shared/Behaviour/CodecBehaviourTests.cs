using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// The codec matrix: every <see cref="ParquetCompressionMethod"/> at every
/// <see cref="ParquetCompressionLevel"/>, asserted by round trip and by the codec the footer
/// records for every column chunk, never by a size ratio. A writer that drops the option writes
/// Snappy and passes a round trip, which is the failure the footer check exists to catch.
/// </summary>
public sealed class CodecBehaviourTests
{
    public static TheoryData<ParquetCompressionMethod> Codecs
    {
        get
        {
            var data = new TheoryData<ParquetCompressionMethod>();
            foreach (ParquetCompressionMethod codec in AllCodecs)
            {
                data.Add(codec);
            }

            return data;
        }
    }

    public static TheoryData<ParquetCompressionMethod, ParquetCompressionLevel> CodecsByLevel
    {
        get
        {
            var data = new TheoryData<ParquetCompressionMethod, ParquetCompressionLevel>();
            foreach (ParquetCompressionMethod codec in AllCodecs)
            {
                foreach (ParquetCompressionLevel level in AllLevels)
                {
                    data.Add(codec, level);
                }
            }

            return data;
        }
    }

    private static readonly ParquetCompressionMethod[] AllCodecs =
    [
        ParquetCompressionMethod.None,
        ParquetCompressionMethod.Snappy,
        ParquetCompressionMethod.Gzip,
        ParquetCompressionMethod.Lz4,
        ParquetCompressionMethod.Brotli,
        ParquetCompressionMethod.Zstd,
    ];

    private static readonly ParquetCompressionLevel[] AllLevels =
    [
        ParquetCompressionLevel.Optimal,
        ParquetCompressionLevel.Fastest,
        ParquetCompressionLevel.NoCompression,
        ParquetCompressionLevel.SmallestSize,
    ];

    /// <summary>A codec or level added to the options enums must be added to the matrix.</summary>
    [Fact]
    public void TheMatrixCoversEveryCodecAndLevel()
    {
        // The generic Enum.GetValues<T> does not exist on net472, which this file also compiles for.
#pragma warning disable CA2263
        AllCodecs.ShouldBe(
            (ParquetCompressionMethod[])Enum.GetValues(typeof(ParquetCompressionMethod)),
            ignoreOrder: true
        );
        AllLevels.ShouldBe(
            (ParquetCompressionLevel[])Enum.GetValues(typeof(ParquetCompressionLevel)),
            ignoreOrder: true
        );
#pragma warning restore CA2263
    }

    [Theory]
    [MemberData(nameof(Codecs))]
    public async Task RoundTripsAndRecordsTheCodecInTheFooter(ParquetCompressionMethod codec)
    {
        await AssertCodecAsync(codec, level: null);
    }

    [Theory]
    [MemberData(nameof(CodecsByLevel))]
    public async Task RoundTripsAtEveryCompressionLevel(
        ParquetCompressionMethod codec,
        ParquetCompressionLevel level
    )
    {
        await AssertCodecAsync(codec, level);
    }

    [Theory]
    [MemberData(nameof(Codecs))]
    public async Task TheTypeMatrixRoundTripsUnderEveryCodec(ParquetCompressionMethod codec)
    {
        List<TypeMatrixRow> rows = TypeMatrixData.BuildRows();
        var options = new ParquetSerializerOptions
        {
            RowGroupSize = TypeMatrixData.RowGroupSize,
            CompressionMethod = codec,
        };

        using var stream = new MemoryStream();
        if (
            await IsKnownUnavailableAsync(
                codec,
                () => BehaviourDriver.WriteAsync(rows, stream, options)
            )
        )
        {
            return;
        }

        await BehaviourDriver.WriteAsync(rows, stream, options);
        byte[] bytes = stream.ToArray();

        FooterSummary footer = await FooterSummary.ReadAsync(bytes);
        footer.ChunkCodecs.Distinct().ShouldBe(FooterNames(codec), ignoreOrder: true);

        using var input = new MemoryStream(bytes, writable: false);
        TypeMatrixRow[] read = await BehaviourDriver.ReadTypeMatrixAsync(input, null);
        foreach (MatrixColumn column in TypeMatrixData.Columns)
        {
            TypeMatrixBehaviourTests.AssertColumn(column.Name, rows, read, $"codec {codec}");
        }
    }

    private static async Task AssertCodecAsync(
        ParquetCompressionMethod codec,
        ParquetCompressionLevel? level
    )
    {
        List<CodecRow> rows = BuildCodecRows(3_000);
        var options = new ParquetSerializerOptions
        {
            RowGroupSize = 1_000,
            CompressionMethod = codec,
            CompressionLevel = level,
        };

        using var stream = new MemoryStream();
        if (
            await IsKnownUnavailableAsync(
                codec,
                () => BehaviourDriver.WriteAsync(rows, stream, options)
            )
        )
        {
            return;
        }

        await BehaviourDriver.WriteAsync(rows, stream, options);
        byte[] bytes = stream.ToArray();

        FooterSummary footer = await FooterSummary.ReadAsync(bytes);
        footer.RowGroupRows.ShouldBe(new long[] { 1_000, 1_000, 1_000 });
        footer.ChunkCodecs.Count.ShouldBe(15);
        footer
            .ChunkCodecs.Distinct()
            .ShouldBe(
                FooterNames(codec),
                ignoreOrder: true,
                $"{BehaviourDriver.Backend()}: {codec} at level {level?.ToString() ?? "default"}"
            );

        using var input = new MemoryStream(bytes, writable: false);
        CodecRow[] read = await BehaviourDriver.ReadCodecRowsAsync(input, null);
        read.Length.ShouldBe(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            read[i].Id.ShouldBe(rows[i].Id);
            read[i].Label.ShouldBe(rows[i].Label);
            read[i].Note.ShouldBe(rows[i].Note);
            read[i].Value.ShouldBe(rows[i].Value);
            TypeMatrixData.ValuesEqual(rows[i].Payload, read[i].Payload).ShouldBeTrue($"row {i}");
        }
    }

    /// <summary>
    /// When the backend lists <paramref name="codec"/> as unavailable on this runtime
    /// (<see cref="KnownFailures.CodecUnavailable"/>), asserts the write still refuses it with
    /// <see cref="NotSupportedException"/> and returns true; a write that now succeeds fails the test
    /// so the entry is removed. Otherwise returns false without writing.
    /// </summary>
    private static async Task<bool> IsKnownUnavailableAsync(
        ParquetCompressionMethod codec,
        Func<Task> write
    )
    {
        if (!BehaviourDriver.IsKnownFailure(KnownFailures.CodecUnavailable(codec)))
        {
            return false;
        }

        await Should.ThrowAsync<NotSupportedException>(
            write,
            $"{BehaviourDriver.Backend()} lists {codec} as unavailable on this runtime but the "
                + "write now succeeds: remove it from the driver's known failures."
        );
        return true;
    }

    /// <summary>
    /// The footer codec each option must produce. Both Parquet.Net 4.25 and 6.x record
    /// <see cref="ParquetCompressionMethod.Lz4"/> as the <c>LZ4</c> codec id, not <c>LZ4_RAW</c>.
    /// </summary>
    private static string[] FooterNames(ParquetCompressionMethod codec) =>
        codec switch
        {
            ParquetCompressionMethod.None => ["UNCOMPRESSED"],
            ParquetCompressionMethod.Snappy => ["SNAPPY"],
            ParquetCompressionMethod.Gzip => ["GZIP"],
            ParquetCompressionMethod.Lz4 => ["LZ4"],
            ParquetCompressionMethod.Brotli => ["BROTLI"],
            ParquetCompressionMethod.Zstd => ["ZSTD"],
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
        };

    internal static List<CodecRow> BuildCodecRows(int count)
    {
        string[] labels = ["alpha", "beta", "gamma", "delta"];
        return Enumerable
            .Range(0, count)
            .Select(i => new CodecRow
            {
                Id = i,
                Label = labels[i % labels.Length] + "-" + (i / 10),
                Note = i % 7 == 0 ? null : "note " + (i % 13),
                Value = i * 0.25,
                // Never zero-length: see TypeMatrixBehaviourTests.EmptyBinaryValueRoundTrips.
                Payload =
                    i % 5 == 0 ? null : Enumerable.Repeat((byte)(i % 3), 1 + (i % 17)).ToArray(),
            })
            .ToList();
    }
}
