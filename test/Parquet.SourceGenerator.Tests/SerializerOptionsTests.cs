using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record CompressibleRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("payload")]
    public string Payload { get; init; } = string.Empty;
}

[ParquetSerializable]
public partial record MicrosecondRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("captured_at")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime CapturedAt { get; init; }
}

public sealed class SerializerOptionsTests
{
    private static List<CompressibleRecord> HighlyCompressibleRows(int count)
    {
        var rows = new List<CompressibleRecord>(count);
        for (int i = 0; i < count; i++)
        {
            // Identical payloads so any real compression shows up plainly in the byte count.
            rows.Add(new CompressibleRecord { Id = i, Payload = new string('a', 512) });
        }

        return rows;
    }

    private static async Task<long> WriteAndMeasureAsync(ParquetCompressionMethod method)
    {
        using var stream = new MemoryStream();
        await HighlyCompressibleRows(2_000)
            .WriteParquetAsync(stream, new ParquetSerializerOptions { CompressionMethod = method });
        return stream.Length;
    }

    [Fact]
    public async Task CompressionMethodIsActuallyApplied()
    {
        // CompressionMethod was accepted and silently discarded — the option never reached the
        // writer, so every file came out Snappy regardless. Comparing sizes is the check that would
        // have caught that: with 2,000 identical 512-byte payloads, an uncompressed file is far
        // larger than a compressed one. Asserting on a ratio rather than an exact size keeps this
        // from breaking on a Parquet.Net encoding change.
        long uncompressed = await WriteAndMeasureAsync(ParquetCompressionMethod.None);
        long snappy = await WriteAndMeasureAsync(ParquetCompressionMethod.Snappy);
        long gzip = await WriteAndMeasureAsync(ParquetCompressionMethod.Gzip);

        (snappy < uncompressed / 2).ShouldBeTrue(
            $"Snappy ({snappy} bytes) should be well under half of uncompressed ({uncompressed} bytes)"
        );
        (gzip < uncompressed / 2).ShouldBeTrue(
            $"Gzip ({gzip} bytes) should be well under half of uncompressed ({uncompressed} bytes)"
        );
    }

    [Fact]
    public async Task EveryCompressionMethodRoundtrips()
    {
        // A mapping mistake — pointing at the wrong Parquet.Net enum member — would most likely
        // surface as a write or read failure rather than a wrong size.
        foreach (
            ParquetCompressionMethod method in new[]
            {
                ParquetCompressionMethod.None,
                ParquetCompressionMethod.Snappy,
                ParquetCompressionMethod.Gzip,
                ParquetCompressionMethod.Lz4,
                ParquetCompressionMethod.Brotli,
                ParquetCompressionMethod.Zstd,
            }
        )
        {
            var written = new List<CompressibleRecord>
            {
                new() { Id = 1, Payload = "first" },
                new() { Id = 2, Payload = "second" },
            };

            using var stream = new MemoryStream();
            await written.WriteParquetAsync(
                stream,
                new ParquetSerializerOptions { CompressionMethod = method }
            );
            stream.Position = 0;

            List<CompressibleRecord> read =
                await CompressibleRecordParquetExtensions.ReadParquetAsync(stream);

            read.Count.ShouldBe(2);
            read[0].Payload.ShouldBe("first");
            read[1].Payload.ShouldBe("second");
        }
    }

    [Fact]
    public async Task MicrosecondTimestampsKeepSubMillisecondPrecision()
    {
        // The Microseconds unit mapped to DateTimeFormat.DateAndTime, which Parquet.Net documents as
        // *millisecond* precision — so a column asked for in microseconds was silently written
        // coarser, and the sub-millisecond part was lost on the way back. A timestamp carrying
        // microseconds that milliseconds cannot represent is what distinguishes the two.
        var captured = new DateTime(2026, 8, 5, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_890);
        (captured.Ticks % TimeSpan.TicksPerMillisecond).ShouldNotBe(0);

        var written = new List<MicrosecondRecord>
        {
            new() { Id = 1, CapturedAt = captured },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        List<MicrosecondRecord> read = await MicrosecondRecordParquetExtensions.ReadParquetAsync(
            stream
        );

        read.ShouldHaveSingleItem();

        // Truncated to whole microseconds, which is the precision actually being requested.
        long expectedMicros = captured.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        long actualMicros = read[0].CapturedAt.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        actualMicros.ShouldBe(expectedMicros);

        // And the value is genuinely finer than millisecond resolution would allow.
        DateTime millisecondTruncated = new DateTime(
            captured.Ticks - (captured.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc
        );
        read[0].CapturedAt.ShouldNotBe(millisecondTruncated);
    }
}
