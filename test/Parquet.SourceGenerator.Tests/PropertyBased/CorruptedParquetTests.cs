using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.SourceGenerator.Tests.PropertyBased;

/// <summary>
/// Serialises the corruption suite against every other collection so its allocation ceiling is
/// measured against a quiet process rather than whatever else happened to be running.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CorruptedParquetSuite
{
    public const string Name = "corrupted-parquet";
}

/// <summary>
/// Controlled corruption of otherwise valid Parquet files.
/// </summary>
/// <remarks>
/// The contract under test is deliberately weak and therefore checkable: a corrupt file may be
/// rejected with any exception, and it may — if the damage happens to miss everything the reader
/// consults — still read back correctly. What it may never do is hang, exhaust memory, or hand back
/// data that differs from the truth without saying so. Silent corruption is the only outcome that is
/// always a bug.
/// </remarks>
[Collection(CorruptedParquetSuite.Name)]
public sealed class CorruptedParquetTests
{
    /// <summary>How long a read of a small corrupt file may take before it counts as a hang.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The allocation ceiling for reading one corrupt file. The baseline file is a few tens of
    /// kilobytes, so anything approaching this means a length or count field in the file was
    /// believed without being validated.
    /// </summary>
    private const long AllocationCeilingBytes = 256L * 1024 * 1024;

    private static readonly FuzzCase BaselineCase = FuzzCase.FromSeed(
        FuzzConfig.DefaultBaseSeed
    ) with
    {
        RowCount = 40,
        RowGroupSize = 16,
        Compression = ParquetCompressionMethod.Snappy,
        Columns = FuzzColumns.All.Select(c => c.Name).ToList(),
        EncodingHints = new Dictionary<string, ParquetColumnEncoding>(StringComparer.Ordinal),
    };

    public static TheoryData<string> Corruptions
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in CorruptionCatalog.Names)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task CorruptFileIsRejectedOrReadExactly(string corruption)
    {
        IReadOnlyList<FuzzWideRecord> rows = BaselineCase.BuildRows();
        byte[] valid = await FuzzRunner.WriteWithGeneratedWriterAsync(rows, BaselineCase);
        byte[] damaged = CorruptionCatalog.Apply(corruption, valid);

        await AssertFailsCleanlyOrReadsExactlyAsync(corruption, damaged, rows);
    }

    public static TheoryData<long> BitFlipSeeds
    {
        get
        {
            var data = new TheoryData<long>();
            foreach (long seed in FuzzConfig.Seeds().Take(8))
            {
                data.Add(seed);
            }

            return data;
        }
    }

    /// <summary>
    /// Random bit flips in the eight-byte trailer — the footer length and the closing <c>PAR1</c>.
    /// </summary>
    /// <remarks>
    /// This is the one region where "reject it" is an achievable contract. Every byte of the trailer
    /// is structural: change the magic and the file is not Parquet, change the length and the Thrift
    /// footer no longer starts where the file says it does. Deeper in the metadata a flipped byte can
    /// still decode to a well-formed footer that merely points somewhere else, which is why the
    /// whole-file test below asserts only termination.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BitFlipSeeds))]
    public async Task TrailerBitFlipsAreAlwaysRejected(long seed)
    {
        IReadOnlyList<FuzzWideRecord> rows = BaselineCase.BuildRows();
        byte[] valid = await FuzzRunner.WriteWithGeneratedWriterAsync(rows, BaselineCase);

        var rng = new FuzzRandom(seed);
        byte[] damaged = (byte[])valid.Clone();
        int offset = rng.Next(damaged.Length - 8, damaged.Length);
        damaged[offset] ^= (byte)(1 << rng.Next(0, 8));

        await AssertRejectedAsync($"trailer bit flip at {offset} (seed {seed})", damaged);
    }

    /// <summary>
    /// Bit flips anywhere in the file, including inside data pages.
    /// </summary>
    /// <remarks>
    /// The weaker contract here is the honest one. Parquet's page CRCs are optional and Parquet.Net
    /// does not verify them, so a flipped byte inside an encoded page can decode to a different but
    /// perfectly well-formed value — the fuzzer found exactly that, a microsecond timestamp that came
    /// back two minutes early with no error anywhere. No amount of care in the generated reader can
    /// detect that, so this test asserts only what the reader is actually responsible for: finishing,
    /// and not exhausting memory. Detecting page-level corruption would mean writing and verifying
    /// CRCs, which is an upstream Parquet.Net feature request rather than a generator concern.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BitFlipSeeds))]
    public async Task RandomBitFlipsAnywhereTerminateWithoutExhaustingMemory(long seed)
    {
        IReadOnlyList<FuzzWideRecord> rows = BaselineCase.BuildRows();
        byte[] valid = await FuzzRunner.WriteWithGeneratedWriterAsync(rows, BaselineCase);

        var rng = new FuzzRandom(seed);
        byte[] damaged = (byte[])valid.Clone();
        int flips = rng.Next(1, 8);
        for (int i = 0; i < flips; i++)
        {
            damaged[rng.Next(0, damaged.Length)] ^= (byte)(1 << rng.Next(0, 8));
        }

        ReadOutcome outcome = await ReadAsync(damaged);

        Assert.False(
            outcome.TimedOut,
            $"bit flips (seed {seed}): read did not finish within {ReadTimeout}."
        );
        AssertBoundedFailure($"bit flips (seed {seed})", outcome);
    }

    [Fact]
    public async Task FooterClaimingMoreRowsThanTheFileHoldsDoesNotSilentlySucceed()
    {
        // Truncating inside the data while leaving the footer intact leaves a file whose metadata
        // promises more rows than the pages can supply — the classic inconsistent-row-count shape.
        IReadOnlyList<FuzzWideRecord> rows = BaselineCase.BuildRows();
        byte[] valid = await FuzzRunner.WriteWithGeneratedWriterAsync(rows, BaselineCase);
        byte[] damaged = CorruptionCatalog.Apply("data-region-hole", valid);

        await AssertFailsCleanlyOrReadsExactlyAsync("data-region-hole", damaged, rows);
    }

    [Fact]
    public async Task ColumnWithAnUnexpectedPhysicalTypeIsRejected()
    {
        byte[] bytes = await WriteWithSubstitutedFieldAsync(
            "i32",
            new DataField("i32", typeof(string), isNullable: false),
            _ => "not-an-int"
        );

        await AssertRejectedAsync("i32 written as UTF8", bytes);
    }

    [Fact]
    public async Task TimestampColumnWithoutItsLogicalAnnotationIsRejected()
    {
        byte[] bytes = await WriteWithSubstitutedFieldAsync(
            "ts_micros",
            new DataField("ts_micros", typeof(long), isNullable: false),
            _ => 1_700_000_000_000_000L
        );

        await AssertRejectedAsync("ts_micros written as a bare INT64", bytes);
    }

    [Fact]
    public async Task DecimalColumnWithADifferentScaleIsRejectedOrReadExactly()
    {
        IReadOnlyList<FuzzWideRecord> rows = BaselineCase.BuildRows();
        byte[] bytes = await WriteWithSubstitutedFieldAsync(
            "money",
            new DecimalDataField("money", 38, 18, isNullable: false),
            row => row.Money
        );

        // A wider decimal is still a decimal, so the reader is allowed to accept it — but only if the
        // values it hands back are the ones in the file.
        await AssertFailsCleanlyOrReadsExactlyAsync(
            "money widened to DECIMAL(38,18)",
            bytes,
            rows,
            columnsToCompare: ["money", "row_index"]
        );
    }

    /// <summary>
    /// Writes the baseline rows with one field replaced by an incompatible declaration, which is how
    /// the suite manufactures files carrying annotations the generated reader never emits.
    /// </summary>
    private static async Task<byte[]> WriteWithSubstitutedFieldAsync(
        string columnName,
        DataField replacement,
        Func<FuzzWideRecord, object?> value
    )
    {
        IReadOnlyList<FuzzWideRecord> rows = BaselineCase.BuildRows();

        var columns = new List<ReferenceColumn>();
        foreach (FuzzColumn column in FuzzColumns.All)
        {
            columns.Add(
                column.Name == columnName
                    ? new ReferenceColumn(replacement, rows.Select(value).ToList())
                    : ReferenceColumn.FromFuzz(column, rows)
            );
        }

        return await IndependentParquetEngine.WriteColumnsAsync(
            columns,
            rows.Count,
            rowGroupSize: 16,
            ParquetCompressionMethod.Snappy
        );
    }

    private static async Task AssertRejectedAsync(string description, byte[] bytes)
    {
        ReadOutcome outcome = await ReadAsync(bytes);

        Assert.False(outcome.TimedOut, $"{description}: read did not finish within {ReadTimeout}.");
        Assert.True(
            outcome.Exception is not null,
            $"{description}: the reader accepted a file it cannot possibly interpret correctly."
        );
        AssertBoundedFailure(description, outcome);
    }

    private static async Task AssertFailsCleanlyOrReadsExactlyAsync(
        string description,
        byte[] bytes,
        IReadOnlyList<FuzzWideRecord> expected,
        IReadOnlyList<string>? columnsToCompare = null
    )
    {
        ReadOutcome outcome = await ReadAsync(bytes);

        Assert.False(outcome.TimedOut, $"{description}: read did not finish within {ReadTimeout}.");
        AssertBoundedFailure(description, outcome);

        if (outcome.Exception is not null)
        {
            return;
        }

        List<FuzzWideRecord> actual = outcome.Rows!;
        Assert.True(
            actual.Count == expected.Count,
            $"{description}: read succeeded but returned {actual.Count} rows instead of "
                + $"{expected.Count}. A corrupt file must be rejected, not silently truncated."
        );

        IEnumerable<FuzzColumn> columns = columnsToCompare is null
            ? FuzzColumns.All
            : columnsToCompare.Select(name => FuzzColumns.ByName[name]);

        foreach (FuzzColumn column in columns)
        {
            for (int i = 0; i < expected.Count; i++)
            {
                object? want = column.NormalizedValue(expected[i]);
                object? got = column.NormalizedValue(actual[i]);
                Assert.True(
                    FuzzCompare.Equal(want, got),
                    $"{description}: read succeeded but column '{column.Name}' row {i} came back as "
                        + $"{FuzzValues.Describe(got)} instead of {FuzzValues.Describe(want)}. "
                        + "A corrupt file must never be accepted with changed values."
                );
            }
        }
    }

    private static void AssertBoundedFailure(string description, ReadOutcome outcome)
    {
        Assert.False(
            outcome.Exception is OutOfMemoryException,
            $"{description}: the reader exhausted memory rather than rejecting the file."
        );
        Assert.True(
            outcome.AllocatedBytes < AllocationCeilingBytes,
            $"{description}: reading allocated "
                + $"{(outcome.AllocatedBytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)} MB, "
                + $"over the {AllocationCeilingBytes / (1024 * 1024)} MB ceiling for a file of "
                + "a few tens of kilobytes."
        );
    }

    private static async Task<ReadOutcome> ReadAsync(byte[] bytes)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        using var cts = new CancellationTokenSource(ReadTimeout);

        Task<List<FuzzWideRecord>> read = Task.Run(
            async () =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return await FuzzWideRecordParquetExtensions.ReadParquetAsync(
                    stream,
                    cancellationToken: cts.Token
                );
            },
            CancellationToken.None
        );

        Task finished = await Task.WhenAny(read, Task.Delay(ReadTimeout + TimeSpan.FromSeconds(5)));
        if (finished != read)
        {
            return new ReadOutcome(null, null, TimedOut: true, 0);
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        try
        {
            return new ReadOutcome(await read, null, TimedOut: false, allocated);
        }
        catch (Exception ex)
        {
            return new ReadOutcome(null, ex, TimedOut: false, allocated);
        }
    }

    private sealed record ReadOutcome(
        List<FuzzWideRecord>? Rows,
        Exception? Exception,
        bool TimedOut,
        long AllocatedBytes
    );
}

/// <summary>
/// The catalogue of deterministic structural corruptions applied to a valid Parquet file.
/// </summary>
public static class CorruptionCatalog
{
    private static readonly Dictionary<string, Func<byte[], byte[]>> Mutations = new(
        StringComparer.Ordinal
    )
    {
        ["empty"] = _ => [],
        ["magic-only"] = _ => "PAR1"u8.ToArray(),
        ["truncate-half"] = bytes => bytes[..(bytes.Length / 2)],
        ["truncate-one-byte"] = bytes => bytes[..(bytes.Length - 1)],
        ["truncate-footer-magic"] = bytes => bytes[..(bytes.Length - 4)],
        ["truncate-footer-length"] = bytes => bytes[..(bytes.Length - 8)],
        ["clobber-footer-magic"] = bytes => Patch(bytes, bytes.Length - 4, "XXXX"u8),
        ["clobber-header-magic"] = bytes => Patch(bytes, 0, "NOPE"u8),
        ["footer-length-zero"] = bytes => Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(0)),
        ["footer-length-huge"] = bytes =>
            Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(int.MaxValue)),
        ["footer-length-negative"] = bytes =>
            Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(-1)),
        ["footer-length-overshoots-file"] = bytes =>
            Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(bytes.Length + 1024)),
        ["footer-metadata-zeroed"] = ZeroFooterMetadata,
        ["footer-metadata-shifted"] = bytes => Patch(bytes, FooterStart(bytes), [0xFF, 0xFF]),
        ["data-region-hole"] = bytes => Patch(bytes, 8, new byte[Math.Min(512, bytes.Length / 4)]),
        ["header-magic-in-footer-position"] = bytes => Patch(bytes, bytes.Length - 8, "PAR1PAR1"u8),
    };

    /// <summary>Gets the names of every catalogued corruption.</summary>
    public static IReadOnlyList<string> Names { get; } =
        Mutations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Applies one catalogued corruption to a copy of the supplied file.</summary>
    public static byte[] Apply(string name, byte[] bytes) => Mutations[name]((byte[])bytes.Clone());

    private static byte[] Patch(byte[] bytes, int offset, ReadOnlySpan<byte> replacement)
    {
        byte[] copy = (byte[])bytes.Clone();
        if (offset < 0 || offset >= copy.Length)
        {
            return copy;
        }

        replacement[..Math.Min(replacement.Length, copy.Length - offset)]
            .CopyTo(copy.AsSpan(offset));
        return copy;
    }

    /// <summary>
    /// The offset the Thrift footer starts at, read out of the file's own footer length. A corrupt
    /// value here is exactly what several of the mutations above are testing, so the reader of this
    /// value clamps rather than trusting it.
    /// </summary>
    private static int FooterStart(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return 0;
        }

        int length = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int start = bytes.Length - 8 - length;
        return start < 4 || start >= bytes.Length ? 4 : start;
    }

    private static byte[] ZeroFooterMetadata(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        int start = FooterStart(copy);
        int end = Math.Max(start, copy.Length - 8);
        Array.Clear(copy, start, end - start);
        return copy;
    }
}
