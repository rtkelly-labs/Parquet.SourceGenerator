using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Schema;
using Shouldly;
using Xunit;
using ParquetPhysicalType = Parquet.Meta.Type;

namespace Parquet.SourceGenerator.Legacy.Tests.Hostile;

/// <summary>
/// The hostile-input matrix on the legacy read path, against a real Parquet.Net 4.25: lying
/// footers (#362), structural corruption, malformed column-chunk bounds, schema mismatches and
/// every <c>Max*</c> limit, each limit with a control that reads at the limit. The documented
/// contract is that hostile input is refused with <see cref="InvalidDataException"/> where the
/// generated reader is the one that notices, and never hangs, exhausts memory or reads back
/// changed values.
/// </summary>
public sealed class LegacyHostileInputTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    // ── Lying footers (#362) ───────────────────────────────────────────────────────────────────

    public static TheoryData<string> FooterLies =>
        new()
        {
            "row-count-past-int-max",
            "row-count-two-to-the-32",
            "row-count-negative",
            "row-count-above-allocation-limit",
            "row-count-larger-than-column",
            "column-value-count-above-allocation-limit",
        };

    [Theory]
    [MemberData(nameof(FooterLies))]
    public async Task AFooterThatLiesAboutCountsIsRejected(string lie)
    {
        byte[] valid = await HostileFiles.WriteAsync(HostileFiles.Rows(2));
        (Action<FileMetaData> mutate, ParquetSerializerOptions? options, string message) = Lie(lie);

        byte[] hostile = await HostileFiles.RewriteFooterAsync(valid, (m, _) => mutate(m));

        InvalidDataException ex = await RejectsAsync(hostile, options);
        ex.Message.ShouldContain(message);
        await ReadsAsync(valid, rows: 2);
    }

    private static (Action<FileMetaData>, ParquetSerializerOptions?, string) Lie(string lie) =>
        lie switch
        {
            "row-count-past-int-max" => (
                m => m.RowGroups[0].NumRows = 1L << 31,
                null,
                "Row group 0 row count 2147483648 is invalid or exceeds maximum allowed"
            ),
            "row-count-two-to-the-32" => (
                m => m.RowGroups[0].NumRows = 1L << 32,
                null,
                "Row group 0 row count 4294967296 is invalid or exceeds maximum allowed"
            ),
            "row-count-negative" => (
                m => m.RowGroups[0].NumRows = -1,
                null,
                "Row group 0 row count -1 is invalid or exceeds maximum allowed"
            ),
            "row-count-above-allocation-limit" => (
                m => m.RowGroups[0].NumRows = 500,
                new ParquetSerializerOptions { MaxAllocationValues = 100 },
                "Row group 0 row count 500 is invalid or exceeds maximum allowed 100"
            ),
            "row-count-larger-than-column" => (
                m => m.RowGroups[0].NumRows = 5_000,
                null,
                "supplied 2 values for a row group declaring 5000 rows"
            ),
            "column-value-count-above-allocation-limit" => (
                m => m.RowGroups[0].Columns[0].MetaData!.NumValues = 100_000,
                new ParquetSerializerOptions { MaxAllocationValues = 1_000 },
                "NumValues (100000) is invalid or exceeds maximum allowed 1000"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(lie), lie, null),
        };

    // ── Structural corruption ──────────────────────────────────────────────────────────────────

    public static TheoryData<string> AlwaysRejectedCorruptions =>
        Names(HostileFiles.AlwaysRejected);

    public static TheoryData<string> RejectedOrExactCorruptions =>
        Names(HostileFiles.RejectedOrExact);

    [Theory]
    [MemberData(nameof(AlwaysRejectedCorruptions))]
    public async Task StructuralCorruptionIsAlwaysRejected(string corruption)
    {
        byte[] valid = await HostileFiles.WriteAsync(HostileFiles.Rows(40));
        byte[] damaged = HostileFiles.AlwaysRejected[corruption](valid);

        ReadOutcome outcome = await ReadBoundedAsync(damaged);

        outcome.AssertTerminatedWithoutExhaustingMemory(corruption);
        outcome.Exception.ShouldNotBeNull($"{corruption}: the legacy reader accepted the file.");
    }

    [Theory]
    [MemberData(nameof(RejectedOrExactCorruptions))]
    public async Task OtherCorruptionIsRejectedOrReadExactly(string corruption)
    {
        List<HostileRow> rows = HostileFiles.Rows(40);
        byte[] valid = await HostileFiles.WriteAsync(rows);
        byte[] damaged = HostileFiles.RejectedOrExact[corruption](valid);

        ReadOutcome outcome = await ReadBoundedAsync(damaged);

        outcome.AssertTerminatedWithoutExhaustingMemory(corruption);
        if (outcome.Exception is null)
        {
            AssertExact(corruption, rows, outcome.Rows!);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task TrailerBitFlipsAreAlwaysRejected(int seed)
    {
        byte[] valid = await HostileFiles.WriteAsync(HostileFiles.Rows(40));
        var random = new Random(seed);
        byte[] damaged = (byte[])valid.Clone();
        int offset = random.Next(damaged.Length - 8, damaged.Length);
        damaged[offset] ^= (byte)(1 << random.Next(0, 8));

        ReadOutcome outcome = await ReadBoundedAsync(damaged);

        outcome.AssertTerminatedWithoutExhaustingMemory($"trailer flip at {offset}");
        outcome.Exception.ShouldNotBeNull($"trailer bit flip at {offset} (seed {seed})");
    }

    /// <summary>
    /// Anywhere in the file only termination is asserted: Parquet.Net does not verify page CRCs,
    /// so a flip inside a page can decode to a different well-formed value (see the modern
    /// <c>CorruptedParquetTests</c> and UPSTREAM_DEPENDENCY_LIMITATIONS.md).
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public async Task RandomBitFlipsTerminateWithoutExhaustingMemory(int seed)
    {
        byte[] valid = await HostileFiles.WriteAsync(HostileFiles.Rows(40));
        var random = new Random(seed);
        byte[] damaged = (byte[])valid.Clone();
        int flips = random.Next(1, 8);
        for (int i = 0; i < flips; i++)
        {
            damaged[random.Next(0, damaged.Length)] ^= (byte)(1 << random.Next(0, 8));
        }

        (await ReadBoundedAsync(damaged)).AssertTerminatedWithoutExhaustingMemory(
            $"bit flips (seed {seed})"
        );
    }

    // ── Column-chunk bounds ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("before-header", "outside the file data bounds")]
    [InlineData("in-footer", "outside the file data bounds")]
    [InlineData("past-footer", "extends beyond the file data bounds")]
    [InlineData("negative-size", "negative compressed size")]
    [InlineData("zero-size", "no compressed data")]
    [InlineData("contained", "ranges overlap")]
    [InlineData("overlap", "ranges overlap")]
    public async Task MalformedColumnChunkBoundsAreRejected(string mutation, string message)
    {
        byte[] valid = await HostileFiles.WriteAsync(HostileFiles.Rows(1));
        byte[] hostile = await HostileFiles.RewriteFooterAsync(
            valid,
            (metadata, footerStart) =>
            {
                ColumnMetaData first = metadata.RowGroups[0].Columns[0].MetaData!;
                ColumnMetaData second = metadata.RowGroups[0].Columns[1].MetaData!;
                switch (mutation)
                {
                    case "before-header":
                        first.DataPageOffset = -1;
                        break;
                    case "in-footer":
                        first.DataPageOffset = footerStart;
                        break;
                    case "past-footer":
                        first.TotalCompressedSize = long.MaxValue;
                        break;
                    case "negative-size":
                        first.TotalCompressedSize = -1;
                        break;
                    case "zero-size":
                        first.TotalCompressedSize = 0;
                        break;
                    case "contained":
                        first.TotalCompressedSize =
                            second.DataPageOffset - first.DataPageOffset + 1;
                        break;
                    case "overlap":
                        second.DataPageOffset = first.DataPageOffset;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
                }
            }
        );

        (await RejectsAsync(hostile)).Message.ShouldContain(message);
    }

    // ── Schema mismatches ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APhysicalTypeTheFooterMisreportsIsRejected()
    {
        byte[] valid = await HostileFiles.WriteAsync(HostileFiles.Rows(1));
        byte[] hostile = await HostileFiles.RewriteFooterAsync(
            valid,
            (m, _) => m.RowGroups[0].Columns[0].MetaData!.Type = ParquetPhysicalType.BYTE_ARRAY
        );

        InvalidDataException ex = await RejectsAsync(hostile);
        ex.Message.ShouldContain("physical type 'BYTE_ARRAY'");
        ex.Message.ShouldContain("expected 'INT32'");
    }

    [Fact]
    public async Task AColumnOfTheWrongClrTypeIsRejected()
    {
        var schema = new ParquetSchema(new DataField<long>("id"), new DataField<string>("name"));
        byte[] bytes = await WriteWithParquetNetAsync(schema);

        (await RejectsAsync(bytes)).Message.ShouldContain(
            "Column 'id' type 'System.Int64' does not match expected 'System.Int32'"
        );
    }

    [Fact]
    public async Task AReorderedColumnOfTheWrongClrTypeIsRejected()
    {
        var schema = new ParquetSchema(new DataField<string>("name"), new DataField<string>("id"));
        byte[] bytes = await WriteWithParquetNetAsync(schema);

        InvalidDataException ex = await RejectsAsync(bytes);
        ex.Message.ShouldContain("Column 'id' type");
        ex.Message.ShouldContain("does not match expected 'System.Int32'");
    }

    [Fact]
    public async Task AMissingRequiredColumnIsRejected()
    {
        byte[] bytes = await HostileFiles.WriteAsync(HostileFiles.Rows(3));

        using var stream = new MemoryStream(bytes, writable: false);
        InvalidDataException ex = await Should.ThrowAsync<InvalidDataException>(() =>
            RequiredExtraRowParquetLegacyExtensions.ReadParquetArrayAsync(stream)
        );
        ex.Message.ShouldContain("Required column 'extra' was not found");
    }

    /// <summary>
    /// The footer is read by seeking, so a forward-only stream is a caller error Parquet.Net 4.25
    /// refuses up front with <see cref="ArgumentException"/>, before anything is read or allocated.
    /// </summary>
    [Fact]
    public async Task ANonSeekableStreamIsRefusedBeforeAnythingIsRead()
    {
        byte[] bytes = await HostileFiles.WriteAsync(HostileFiles.Rows(3));

        using var stream = new ForwardOnlyStream(bytes);
        ArgumentException ex = await Should.ThrowAsync<ArgumentException>(() =>
            HostileRowParquetLegacyExtensions.ReadParquetArrayAsync(stream)
        );
        ex.Message.ShouldContain("seekable");
        stream.ReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task RepeatedAbortedReadsLeaveTheNextReadIntact()
    {
        byte[] valid = await HostileFiles.WriteAsync(HostileFiles.Rows(5));
        byte[] hostile = await HostileFiles.RewriteFooterAsync(
            valid,
            (m, _) => m.RowGroups[0].NumRows = 5_000
        );

        for (int i = 0; i < 50; i++)
        {
            await RejectsAsync(hostile);
        }

        await ReadsAsync(valid, rows: 5);
    }

    // ── Every Max* limit, behaviourally, with an at-limit control ──────────────────────────────

    [Fact]
    public async Task MaxRowGroupCountIsEnforced()
    {
        byte[] bytes = await HostileFiles.WriteAsync(
            HostileFiles.Rows(6),
            new ParquetSerializerOptions { RowGroupSize = 2 }
        );

        await ReadsAsync(bytes, 6, new ParquetSerializerOptions { MaxRowGroupCount = 3 });
        (
            await RejectsAsync(bytes, new ParquetSerializerOptions { MaxRowGroupCount = 2 })
        ).Message.ShouldContain("Row group count 3 is invalid or exceeds maximum allowed 2");
    }

    [Fact]
    public async Task MaxAllocationValuesIsEnforcedOnTheTotalAcrossRowGroups()
    {
        byte[] bytes = await HostileFiles.WriteAsync(
            HostileFiles.Rows(10),
            new ParquetSerializerOptions { RowGroupSize = 4 }
        );

        await ReadsAsync(bytes, 10, new ParquetSerializerOptions { MaxAllocationValues = 10 });
        (
            await RejectsAsync(bytes, new ParquetSerializerOptions { MaxAllocationValues = 9 })
        ).Message.ShouldContain("Total row count 10 exceeds maximum allowed 9");
    }

    [Fact]
    public async Task MaxAllocationBytesIsEnforcedBeforeColumnsAreAllocated()
    {
        var rows = Enumerable
            .Range(0, 100)
            .Select(i => new WideNumericRow
            {
                A = i,
                B = -i,
                C = i % 2 == 0 ? null : i,
            })
            .ToList();
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        byte[] bytes = stream.ToArray();

        // 100 rows of two longs and a nullable double: well under 10 KB, well over 1 KB.
        using (var control = new MemoryStream(bytes, writable: false))
        {
            WideNumericRow[] read =
                await WideNumericRowParquetLegacyExtensions.ReadParquetArrayAsync(
                    control,
                    new ParquetSerializerOptions { MaxAllocationBytes = 10_000 }
                );
            read.Length.ShouldBe(100);
        }

        using var hostile = new MemoryStream(bytes, writable: false);
        InvalidDataException ex = await Should.ThrowAsync<InvalidDataException>(() =>
            WideNumericRowParquetLegacyExtensions.ReadParquetArrayAsync(
                hostile,
                new ParquetSerializerOptions { MaxAllocationBytes = 1_000 }
            )
        );
        ex.Message.ShouldContain("exceeding MaxAllocationBytes (1000)");
    }

    [Fact]
    public async Task MaxDictionaryEntriesIsEnforcedOnTheDictionaryNotTheRows()
    {
        var rows = Enumerable
            .Range(0, 1_000)
            .Select(i => new HostileRow { Id = i, Name = i % 2 == 0 ? "first" : "second" })
            .ToList();
        byte[] bytes = await HostileFiles.WriteAsync(rows);

        await ReadsAsync(bytes, 1_000, new ParquetSerializerOptions { MaxDictionaryEntries = 2 });
        (
            await RejectsAsync(bytes, new ParquetSerializerOptions { MaxDictionaryEntries = 1 })
        ).Message.ShouldBe("Dictionary column 'name' entry count 2 exceeds maximum allowed 1.");
    }

    [Fact]
    public async Task MaxStringLengthBytesCountsUtf8BytesNotChars()
    {
        // U+1F600 is two UTF-16 code units but four UTF-8 bytes.
        byte[] bytes = await HostileFiles.WriteAsync(
            new List<HostileRow>
            {
                new() { Id = 1, Name = "\U0001F600" },
            }
        );

        await ReadsAsync(bytes, 1, new ParquetSerializerOptions { MaxStringLengthBytes = 4 });
        (
            await RejectsAsync(bytes, new ParquetSerializerOptions { MaxStringLengthBytes = 3 })
        ).Message.ShouldBe(
            "String column 'name' value at index 0 UTF-8 length 4 exceeds maximum allowed 3."
        );
    }

    [Fact]
    public async Task MaxDecompressedPageSizeBoundsPagesReachedByReadingOn()
    {
        // A dictionary-encoded column is two pages: a dictionary of a few bytes, then a data page
        // of about 8 KB. A limit between the two only trips on the page read after the seek (#360).
        string[] labels = ["alpha", "beta", "gamma"];
        var rows = Enumerable
            .Range(0, 4_000)
            .Select(i => new LabelRow { Label = labels[i % labels.Length] })
            .ToList();
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        byte[] bytes = stream.ToArray();

        long chunkStart;
        using (var input = new MemoryStream(bytes, writable: false))
        using (ParquetReader reader = await ParquetReader.CreateAsync(input))
        {
            chunkStart = reader.Metadata!.RowGroups[0].Columns[0].MetaData!.DataPageOffset;
        }

        using (var control = new MemoryStream(bytes, writable: false))
        {
            (await LabelRowParquetLegacyExtensions.ReadParquetArrayAsync(control)).Length.ShouldBe(
                4_000
            );
        }

        using var hostile = new MemoryStream(bytes, writable: false);
        InvalidDataException ex = await Should.ThrowAsync<InvalidDataException>(() =>
            LabelRowParquetLegacyExtensions.ReadParquetArrayAsync(
                hostile,
                new ParquetSerializerOptions { MaxDecompressedPageSize = 100 }
            )
        );
        ex.Message.ShouldContain("uncompressed size");
        ex.Message.ShouldNotContain($"offset {chunkStart} ");
    }

    [Fact]
    public async Task MaxDecompressionExpansionRatioIsEnforced()
    {
        // 4,000 zero doubles are a 32 KB plain page that Gzip shrinks to a few hundred bytes.
        var rows = Enumerable
            .Range(0, 4_000)
            .Select(i => new WideNumericRow
            {
                A = i,
                B = i,
                C = 0d,
            })
            .ToList();
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(
            stream,
            new ParquetSerializerOptions { CompressionMethod = ParquetCompressionMethod.Gzip }
        );
        byte[] bytes = stream.ToArray();

        using (var control = new MemoryStream(bytes, writable: false))
        {
            (
                await WideNumericRowParquetLegacyExtensions.ReadParquetArrayAsync(control)
            ).Length.ShouldBe(4_000);
        }

        using var hostile = new MemoryStream(bytes, writable: false);
        InvalidDataException ex = await Should.ThrowAsync<InvalidDataException>(() =>
            WideNumericRowParquetLegacyExtensions.ReadParquetArrayAsync(
                hostile,
                new ParquetSerializerOptions { MaxDecompressionExpansionRatio = 2 }
            )
        );
        ex.Message.ShouldContain("expands from");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static TheoryData<string> Names(IReadOnlyDictionary<string, Func<byte[], byte[]>> set)
    {
        var data = new TheoryData<string>();
        foreach (string name in set.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    private static async Task<InvalidDataException> RejectsAsync(
        byte[] bytes,
        ParquetSerializerOptions? options = null
    )
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return await Should.ThrowAsync<InvalidDataException>(() =>
            HostileRowParquetLegacyExtensions.ReadParquetArrayAsync(stream, options)
        );
    }

    private static async Task ReadsAsync(
        byte[] bytes,
        int rows,
        ParquetSerializerOptions? options = null
    )
    {
        using var stream = new MemoryStream(bytes, writable: false);
        HostileRow[] read = await HostileRowParquetLegacyExtensions.ReadParquetArrayAsync(
            stream,
            options
        );
        read.Length.ShouldBe(rows);
    }

    private static async Task<byte[]> WriteWithParquetNetAsync(ParquetSchema schema)
    {
        using var stream = new MemoryStream();
        using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream)) { }
        return stream.ToArray();
    }

    private static void AssertExact(
        string description,
        List<HostileRow> expected,
        HostileRow[] actual
    )
    {
        actual.Length.ShouldBe(
            expected.Count,
            $"{description}: a damaged file read back {actual.Length} rows instead of {expected.Count}."
        );
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Id.ShouldBe(expected[i].Id, $"{description}: row {i} id changed");
            actual[i].Name.ShouldBe(expected[i].Name, $"{description}: row {i} name changed");
        }
    }

    private static async Task<ReadOutcome> ReadBoundedAsync(byte[] bytes)
    {
        Task<ReadOutcome> read = Task.Run(async () =>
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                HostileRow[] rows = await HostileRowParquetLegacyExtensions.ReadParquetArrayAsync(
                    stream
                );
                return new ReadOutcome(rows, null, timedOut: false);
            }
            catch (Exception ex)
            {
                return new ReadOutcome(null, ex, timedOut: false);
            }
        });

        Task finished = await Task.WhenAny(read, Task.Delay(ReadTimeout));
        return finished == read ? await read : new ReadOutcome(null, null, timedOut: true);
    }

    private sealed class ReadOutcome
    {
        public ReadOutcome(HostileRow[]? rows, Exception? exception, bool timedOut)
        {
            Rows = rows;
            Exception = exception;
            TimedOut = timedOut;
        }

        public HostileRow[]? Rows { get; }

        public Exception? Exception { get; }

        public bool TimedOut { get; }

        public void AssertTerminatedWithoutExhaustingMemory(string description)
        {
            TimedOut.ShouldBeFalse($"{description}: the read did not finish within {ReadTimeout}.");
            (Exception is OutOfMemoryException).ShouldBeFalse(
                $"{description}: the reader exhausted memory rather than rejecting the file."
            );
            (Exception is OverflowException or IndexOutOfRangeException).ShouldBeFalse(
                $"{description}: a count was believed unchecked ({Exception?.GetType().Name}: {Exception?.Message})."
            );
        }
    }

    private sealed class ForwardOnlyStream : MemoryStream
    {
        public ForwardOnlyStream(byte[] bytes)
            : base(bytes, writable: false) { }

        public int ReadCount { get; private set; }

        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return base.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }
    }
}
