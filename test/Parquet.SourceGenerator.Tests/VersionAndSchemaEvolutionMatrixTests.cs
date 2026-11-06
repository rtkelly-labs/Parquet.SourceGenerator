using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// The producer/consumer/version/schema matrix for issue #168.
//
// Two properties make this a matrix rather than a pile of round-trip tests. First, every cell is
// *declared* — producer, producer version, consumer, schema case and expected outcome are written
// down in DeclaredCases, and the theory is driven off that declaration, so a cell cannot quietly
// stop being exercised. Second, the producer and format versions in the recorded report are read
// out of each file's own footer rather than copied from a manifest, so the report says what was
// actually read, not what someone believed was in the fixture directory.
//
// Half the producers here are not Parquet.Net: PyArrow 25.0.0 at format 1.0 and 2.6, and DuckDB.
// That is deliberate — a matrix written entirely against the same writer that the reader ships with
// proves only that the library agrees with itself.

/// <summary>Baseline writer schema: the "old" producer in the evolution cases.</summary>
[ParquetSerializable]
public partial record EvoBaseline
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; init; }

    [ParquetColumn("name", Order = 2)]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("score", Order = 3)]
    public double? Score { get; init; }
}

/// <summary>
/// The "new" consumer schema: the baseline's columns in a different order plus two optional columns
/// a baseline file cannot contain.
/// </summary>
[ParquetSerializable]
public partial record EvoExtended
{
    [ParquetColumn("score", Order = 1)]
    public double? Score { get; init; }

    [ParquetColumn("id", Order = 2)]
    public int Id { get; init; }

    [ParquetColumn("name", Order = 3)]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("added_note", Order = 4)]
    public string? AddedNote { get; init; }

    [ParquetColumn("added_count", Order = 5)]
    public int? AddedCount { get; init; }

    [ParquetColumn("added_amount", Order = 6)]
    [ParquetDecimal(18, 4)]
    public decimal? AddedAmount { get; init; }

    [ParquetColumn("added_payload", Order = 7)]
    public byte[]? AddedPayload { get; init; }
}

/// <summary>A consumer that knows fewer columns than the file carries.</summary>
[ParquetSerializable]
public partial record EvoSubset
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; init; }

    [ParquetColumn("name", Order = 2)]
    public string Name { get; init; } = string.Empty;
}

/// <summary>A consumer whose required column no supported producer writes.</summary>
[ParquetSerializable]
public partial record EvoRequiresAbsentColumn
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; init; }

    [ParquetColumn("absent_required", Order = 2)]
    public long AbsentRequired { get; init; }
}

/// <summary>The canonical fixture schema plus optional columns no fixture producer wrote.</summary>
[ParquetSerializable]
public partial record EvoFixtureUser
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; init; }

    [ParquetColumn("name", Order = 2)]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("score", Order = 3)]
    public double Score { get; init; }

    [ParquetColumn("is_active", Order = 4)]
    public bool IsActive { get; init; }

    [ParquetColumn("created_at_ms", Order = 5)]
    public long CreatedAtMs { get; init; }

    [ParquetColumn("email", Order = 6)]
    public string? Email { get; init; }

    [ParquetColumn("retry_count", Order = 7)]
    public int? RetryCount { get; init; }
}

/// <summary>The fixture schema declared back to front, to force name-based resolution.</summary>
[ParquetSerializable]
public partial record EvoReversedFixtureUser
{
    [ParquetColumn("created_at_ms", Order = 1)]
    public long CreatedAtMs { get; init; }

    [ParquetColumn("is_active", Order = 2)]
    public bool IsActive { get; init; }

    [ParquetColumn("score", Order = 3)]
    public double Score { get; init; }

    [ParquetColumn("name", Order = 4)]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("id", Order = 5)]
    public int Id { get; init; }
}

/// <summary>
/// Requires a column that exists in the nested fixture only as a repeated group, which is outside
/// the flat envelope. Resolution must reject it by name rather than half-read the group.
/// </summary>
[ParquetSerializable]
public partial record EvoNestedProbe
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; init; }

    [ParquetColumn("tags", Order = 2)]
    public string Tags { get; init; } = string.Empty;
}

/// <summary>One declared cell of the compatibility matrix.</summary>
public sealed record CompatibilityMatrixCase(
    string CaseId,
    string Producer,
    string DeclaredProducerVersion,
    string Consumer,
    string SchemaCase,
    CompatibilityOutcome ExpectedOutcome,
    Func<Task<MatrixObservation>> Run
);

/// <summary>What actually happened when a cell ran.</summary>
public sealed record MatrixObservation(
    CompatibilityOutcome Outcome,
    string ProducerVersion,
    string FormatVersion,
    string Detail
);

[Collection("CompatibilityMatrix")]
public sealed class VersionAndSchemaEvolutionMatrixTests
    : IClassFixture<CompatibilityMatrixReportFixture>
{
    private static readonly string TestDataRoot =
        Environment.GetEnvironmentVariable("PARQUET_TEST_DATA_ROOT")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));

    // Deliberately NOT PARQUET_TEST_DATA_CSHARP_ROOT. CI regenerates test/data_csharp on every run
    // with the solution's currently pinned Parquet.Net, so a fixture read through that variable is
    // produced by the current writer no matter what the directory is called. The older-producer
    // cells need files a 6.0.3 writer really wrote, so they read from a committed directory that
    // nothing regenerates. See tools/ParquetNetLegacyFixtures.
    private static readonly string TestDataProducersRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data_producers")
    );

    private static readonly string BenchmarkDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "benchmarks", "data")
    );

    private const string GeneratedWriter = "generated-writer";
    private const string SequentialReader = "generated-reader/sequential";
    private const string ParallelReader = "generated-reader/parallel";
    private const string StreamingReader = "generated-reader/streaming";

    // ──────────────────────────────────────────────────────────
    //  DECLARED MATRIX
    // ──────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, CompatibilityMatrixCase> DeclaredCases { get; } =
        BuildDeclaredCases().ToDictionary(c => c.CaseId, StringComparer.Ordinal);

    public static TheoryData<string> CaseIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string id in DeclaredCases.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                data.Add(id);
            }

            return data;
        }
    }

    private static List<CompatibilityMatrixCase> BuildDeclaredCases() =>
        [
            // ── Current generated writer → current generated readers ──────────────────────────
            new(
                "gen/identical/sequential",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                SequentialReader,
                "identical",
                CompatibilityOutcome.Compatible,
                GeneratedIdenticalAsync
            ),
            new(
                "gen/reordered/sequential",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                SequentialReader,
                "column-reordering",
                CompatibilityOutcome.Compatible,
                GeneratedReorderedAsync
            ),
            new(
                "gen/missing-optional/sequential",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                SequentialReader,
                "missing-optional-columns",
                CompatibilityOutcome.CompatibleWithNulls,
                () => GeneratedMissingOptionalAsync(SequentialReader)
            ),
            new(
                "gen/missing-optional/parallel",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                ParallelReader,
                "missing-optional-columns",
                CompatibilityOutcome.CompatibleWithNulls,
                () => GeneratedMissingOptionalAsync(ParallelReader)
            ),
            new(
                "gen/missing-optional/streaming",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                StreamingReader,
                "missing-optional-columns",
                CompatibilityOutcome.CompatibleWithNulls,
                () => GeneratedMissingOptionalAsync(StreamingReader)
            ),
            new(
                "gen/missing-optional-multi-row-group/parallel",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                ParallelReader,
                "missing-optional-columns+multi-row-group",
                CompatibilityOutcome.CompatibleWithNulls,
                GeneratedMissingOptionalMultiRowGroupAsync
            ),
            new(
                "gen/extra-unknown-columns/sequential",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                SequentialReader,
                "extra-unknown-columns",
                CompatibilityOutcome.Compatible,
                GeneratedExtraColumnsAsync
            ),
            new(
                "gen/missing-required/sequential",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                SequentialReader,
                "missing-required-column",
                CompatibilityOutcome.RejectedWithClearError,
                GeneratedMissingRequiredAsync
            ),
            new(
                "gen/foreign-producer-metadata/sequential",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                SequentialReader,
                "producer-metadata-differences",
                CompatibilityOutcome.Compatible,
                ForeignProducerMetadataAsync
            ),
            new(
                "gen/compression-variants/sequential",
                GeneratedWriter,
                "Parquet.Net 6.1.0",
                SequentialReader,
                "compression-variants",
                CompatibilityOutcome.Compatible,
                CompressionVariantsAsync
            ),
            // ── PyArrow 25.0.0, format 1.0 ────────────────────────────────────────────────────
            new(
                "pyarrow-1.0/identical/sequential",
                "PyArrow",
                "25.0.0",
                SequentialReader,
                "identical",
                CompatibilityOutcome.Compatible,
                () => FixtureIdenticalAsync(V1("01_small_flat_primitives.parquet"), 100)
            ),
            new(
                "pyarrow-1.0/missing-optional/sequential",
                "PyArrow",
                "25.0.0",
                SequentialReader,
                "missing-optional-columns",
                CompatibilityOutcome.CompatibleWithNulls,
                () =>
                    FixtureMissingOptionalAsync(
                        V1("01_small_flat_primitives.parquet"),
                        100,
                        SequentialReader
                    )
            ),
            new(
                "pyarrow-1.0/extra-unknown-columns/sequential",
                "PyArrow",
                "25.0.0",
                SequentialReader,
                "extra-unknown-columns",
                CompatibilityOutcome.Compatible,
                () => FixtureExtraColumnsAsync(V1("01_small_flat_primitives.parquet"), 100)
            ),
            new(
                "pyarrow-1.0/missing-required/sequential",
                "PyArrow",
                "25.0.0",
                SequentialReader,
                "missing-required-column",
                CompatibilityOutcome.RejectedWithClearError,
                () =>
                    FixtureMissingRequiredAsync(
                        V1("01_small_flat_primitives.parquet"),
                        "absent_required"
                    )
            ),
            new(
                "pyarrow-1.0/nested-outside-envelope/sequential",
                "PyArrow",
                "25.0.0",
                SequentialReader,
                "nested-repeated-columns",
                CompatibilityOutcome.RejectedWithClearError,
                NestedOutsideEnvelopeAsync
            ),
            // ── PyArrow 25.0.0, format 2.6 (data page v2) ─────────────────────────────────────
            new(
                "pyarrow-2.6/identical/sequential",
                "PyArrow",
                "25.0.0",
                SequentialReader,
                "identical",
                CompatibilityOutcome.Compatible,
                () => FixtureIdenticalAsync(V2("01_small_flat_primitives.parquet"), 100)
            ),
            new(
                "pyarrow-2.6/reordered/sequential",
                "PyArrow",
                "25.0.0",
                SequentialReader,
                "column-reordering",
                CompatibilityOutcome.Compatible,
                () => FixtureReorderedAsync(V2("01_small_flat_primitives.parquet"), 100)
            ),
            new(
                "pyarrow-2.6/missing-optional/parallel",
                "PyArrow",
                "25.0.0",
                ParallelReader,
                "missing-optional-columns",
                CompatibilityOutcome.CompatibleWithNulls,
                () =>
                    FixtureMissingOptionalAsync(
                        V2("01_small_flat_primitives.parquet"),
                        100,
                        ParallelReader
                    )
            ),
            // ── Older Parquet.Net producer (6.0.3, committed under test/data_producers) ───────
            new(
                "parquet-net-6.0.3/identical/sequential",
                "Parquet.Net",
                "6.0.3",
                SequentialReader,
                "identical",
                CompatibilityOutcome.Compatible,
                () => FixtureIdenticalAsync(ParquetNet603("01_small_flat_primitives.parquet"), 100)
            ),
            new(
                "parquet-net-6.0.3/missing-optional/sequential",
                "Parquet.Net",
                "6.0.3",
                SequentialReader,
                "missing-optional-columns",
                CompatibilityOutcome.CompatibleWithNulls,
                () =>
                    FixtureMissingOptionalAsync(
                        ParquetNet603("01_small_flat_primitives.parquet"),
                        100,
                        SequentialReader
                    )
            ),
            new(
                "parquet-net-6.0.3/extra-unknown-columns/streaming",
                "Parquet.Net",
                "6.0.3",
                StreamingReader,
                "extra-unknown-columns",
                CompatibilityOutcome.Compatible,
                () =>
                    FixtureExtraColumnsStreamingAsync(
                        ParquetNet603("01_small_flat_primitives.parquet"),
                        100
                    )
            ),
            // ── DuckDB ────────────────────────────────────────────────────────────────────────
            new(
                "duckdb/identical/sequential",
                "DuckDB",
                "1.5.4",
                SequentialReader,
                "identical",
                CompatibilityOutcome.Compatible,
                DuckDbLineItemAsync
            ),
        ];

    // ──────────────────────────────────────────────────────────
    //  THE MATRIX ITSELF
    // ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task MatrixCellBehavesAsDeclared(string caseId)
    {
        CompatibilityMatrixCase declared = DeclaredCases[caseId];

        MatrixObservation observed = await declared.Run();

        Assert.True(
            declared.ExpectedOutcome == observed.Outcome,
            $"{caseId}: declared {declared.ExpectedOutcome}, observed {observed.Outcome} ({observed.Detail})"
        );

        // The producer version in the report comes from the file footer wherever there is a file,
        // so a fixture silently regenerated by a different producer shows up here rather than
        // being papered over by the declaration.
        Assert.Contains(
            declared.DeclaredProducerVersion.Split(' ')[^1],
            observed.ProducerVersion,
            StringComparison.OrdinalIgnoreCase
        );

        CompatibilityMatrixRecorder.Record(
            new CompatibilityMatrixResult(
                caseId,
                declared.Producer,
                observed.ProducerVersion,
                declared.Consumer,
                CompatibilityMatrixRecorder.ParquetNetVersion,
                observed.FormatVersion,
                declared.SchemaCase,
                observed.Outcome,
                observed.Detail
            )
        );
    }

    /// <summary>
    /// The declaration is the contract, so it has to stay whole: every axis populated, no duplicate
    /// cells, and every schema case in the documented list actually exercised by something.
    /// </summary>
    [Fact]
    public void DeclaredMatrixCoversEveryDocumentedAxis()
    {
        List<CompatibilityMatrixCase> cases = BuildDeclaredCases();

        Assert.Equal(
            cases.Count,
            cases.Select(c => c.CaseId).Distinct(StringComparer.Ordinal).Count()
        );
        Assert.All(
            cases,
            c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Producer));
                Assert.False(string.IsNullOrWhiteSpace(c.DeclaredProducerVersion));
                Assert.False(string.IsNullOrWhiteSpace(c.Consumer));
                Assert.False(string.IsNullOrWhiteSpace(c.SchemaCase));
            }
        );

        string[] requiredSchemaCases =
        [
            "identical",
            "column-reordering",
            "extra-unknown-columns",
            "missing-optional-columns",
            "missing-required-column",
            "nested-repeated-columns",
            "producer-metadata-differences",
            "compression-variants",
        ];
        foreach (string schemaCase in requiredSchemaCases)
        {
            Assert.Contains(
                cases,
                c => c.SchemaCase.StartsWith(schemaCase, StringComparison.Ordinal)
            );
        }

        // A matrix built only from the writer that ships with the reader proves nothing about
        // interoperability, so at least two producer families outside Parquet.Net must appear.
        string[] foreignProducers = cases
            .Where(c =>
                !c.Producer.Contains("Parquet.Net", StringComparison.Ordinal)
                && c.Producer != GeneratedWriter
            )
            .Select(c => c.Producer)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            foreignProducers.Length >= 2,
            $"expected at least two non-Parquet.Net producers, found: {string.Join(", ", foreignProducers)}"
        );

        // Every reader entry point resolves schemas from its own emitted copy of the resolver, so
        // each has to appear somewhere in the matrix.
        foreach (string consumer in new[] { SequentialReader, ParallelReader, StreamingReader })
        {
            Assert.Contains(cases, c => c.Consumer == consumer);
        }
    }

    // ──────────────────────────────────────────────────────────
    //  GENERATED-WRITER CASES
    // ──────────────────────────────────────────────────────────

    private static List<EvoBaseline> BaselineRows() =>
        [
            new()
            {
                Id = 1,
                Name = "one",
                Score = 1.5,
            },
            new()
            {
                Id = 2,
                Name = "two",
                Score = null,
            },
            new()
            {
                Id = 3,
                Name = "three",
                Score = 3.5,
            },
        ];

    private static async Task<MemoryStream> WriteBaselineAsync(int? rowGroupSize = null)
    {
        var stream = new MemoryStream();
        List<EvoBaseline> rows = rowGroupSize is null
            ? BaselineRows()
            : Enumerable
                .Range(0, 250)
                .Select(i => new EvoBaseline
                {
                    Id = i,
                    Name = "row_" + i.ToString(CultureInfo.InvariantCulture),
                    Score = i % 5 == 0 ? null : i * 0.5,
                })
                .ToList();

        if (rowGroupSize is int size)
        {
            await rows.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = size }
            );
        }
        else
        {
            await rows.WriteParquetAsync(stream);
        }

        stream.Position = 0;
        return stream;
    }

    private static async Task<MatrixObservation> GeneratedIdenticalAsync()
    {
        using MemoryStream stream = await WriteBaselineAsync();
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoBaseline> read = await EvoBaselineParquetExtensions.ReadParquetAsync(stream);
        ParquetCompatibilityOracle.AssertEquivalent(BaselineRows(), read);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            $"{read.Count} rows round-tripped"
        );
    }

    private static async Task<MatrixObservation> GeneratedReorderedAsync()
    {
        // EvoExtended declares score/id/name in the opposite order to the file, so index 0 and
        // index 2 miss the positional fast path and have to resolve by name.
        using MemoryStream stream = await WriteBaselineAsync();
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoExtended> read = await EvoExtendedParquetExtensions.ReadParquetAsync(stream);
        AssertBaselineColumnsSurvived(read);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            "score/id/name resolved by name against a reordered file"
        );
    }

    private static async Task<MatrixObservation> GeneratedMissingOptionalAsync(string consumer)
    {
        using MemoryStream stream = await WriteBaselineAsync();
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoExtended> read = await ReadExtendedAsync(stream, consumer);
        AssertBaselineColumnsSurvived(read);
        AssertAddedColumnsAreNull(read);

        return new MatrixObservation(
            CompatibilityOutcome.CompatibleWithNulls,
            createdBy,
            formatVersion,
            "added_note/added_count/added_amount/added_payload absent from the file, materialised as null"
        );
    }

    private static async Task<MatrixObservation> GeneratedMissingOptionalMultiRowGroupAsync()
    {
        using MemoryStream stream = await WriteBaselineAsync(rowGroupSize: 40);
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoExtended> read = await EvoExtendedParquetExtensions.ReadParquetParallelAsync(
            stream
        );
        Assert.Equal(250, read.Count);
        AssertAddedColumnsAreNull(read);
        Assert.Equal(Enumerable.Range(0, 250), read.OrderBy(r => r.Id).Select(r => r.Id));

        return new MatrixObservation(
            CompatibilityOutcome.CompatibleWithNulls,
            createdBy,
            formatVersion,
            "250 rows over 7 row groups read in parallel with four absent optional columns"
        );
    }

    private static async Task<MatrixObservation> GeneratedExtraColumnsAsync()
    {
        var rows = new List<EvoExtended>
        {
            new()
            {
                Id = 1,
                Name = "one",
                Score = 1.5,
                AddedNote = "note",
                AddedCount = 7,
                AddedAmount = 12.3456m,
                AddedPayload = [1, 2, 3],
            },
            new()
            {
                Id = 2,
                Name = "two",
                Score = null,
                AddedNote = null,
                AddedCount = null,
                AddedAmount = null,
                AddedPayload = null,
            },
        };

        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream);
        stream.Position = 0;
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoSubset> read = await EvoSubsetParquetExtensions.ReadParquetAsync(stream);

        Assert.Equal(2, read.Count);
        Assert.Equal([1, 2], read.Select(r => r.Id));
        Assert.Equal(["one", "two"], read.Select(r => r.Name));

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            "five unknown columns ignored, two known columns materialised"
        );
    }

    private static async Task<MatrixObservation> GeneratedMissingRequiredAsync()
    {
        using MemoryStream stream = await WriteBaselineAsync();
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EvoRequiresAbsentColumnParquetExtensions.ReadParquetAsync(stream)
        );
        AssertClearMissingColumnError(exception, "absent_required");

        return new MatrixObservation(
            CompatibilityOutcome.RejectedWithClearError,
            createdBy,
            formatVersion,
            exception.Message
        );
    }

    /// <summary>
    /// A file carrying a foreign producer's footer metadata: a different <c>created_by</c> string is
    /// not something Parquet.Net lets a caller set, but arbitrary key/value metadata is, and the
    /// contract is that neither changes how a supported schema is interpreted.
    /// </summary>
    private static async Task<MatrixObservation> ForeignProducerMetadataAsync()
    {
        var rows = new List<Dictionary<string, object>>();
        foreach (EvoBaseline row in BaselineRows())
        {
            rows.Add(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = row.Id,
                    ["name"] = row.Name,
                    ["score"] = row.Score!,
                }
            );
        }

        using var stream = new MemoryStream();
        await global::Parquet.Serialization.ParquetSerializer.SerializeUntypedAsync(
            rows.ConvertAll(r => (IDictionary<string, object?>)r!),
            EvoBaselineParquetExtensions.Schema,
            stream,
            customMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["writer.model"] = "some-other-engine",
                ["writer.version"] = "99.99.99",
                ["pandas"] = "{\"index_columns\": [], \"column_indexes\": []}",
                ["ARROW:schema"] = "not-a-real-arrow-schema",
            }
        );
        stream.Position = 0;

        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);
        List<EvoBaseline> read = await EvoBaselineParquetExtensions.ReadParquetAsync(stream);
        ParquetCompatibilityOracle.AssertEquivalent(BaselineRows(), read);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            "four foreign key/value metadata entries ignored; values unchanged"
        );
    }

    private static async Task<MatrixObservation> CompressionVariantsAsync()
    {
        ParquetCompressionMethod[] methods =
        [
            ParquetCompressionMethod.None,
            ParquetCompressionMethod.Snappy,
            ParquetCompressionMethod.Gzip,
            ParquetCompressionMethod.Zstd,
            ParquetCompressionMethod.Brotli,
            ParquetCompressionMethod.Lz4,
        ];

        string createdBy = "unknown";
        string formatVersion = "unknown";
        foreach (ParquetCompressionMethod method in methods)
        {
            using var stream = new MemoryStream();
            await BaselineRows()
                .WriteParquetAsync(
                    stream,
                    new ParquetSerializerOptions { CompressionMethod = method }
                );
            stream.Position = 0;
            (createdBy, formatVersion) = await ReadFooterAsync(stream);

            List<EvoExtended> read = await EvoExtendedParquetExtensions.ReadParquetAsync(stream);
            AssertBaselineColumnsSurvived(read);
            AssertAddedColumnsAreNull(read);
        }

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            $"{methods.Length} compression codecs round-tripped through the evolved reader"
        );
    }

    // ──────────────────────────────────────────────────────────
    //  FIXTURE (FOREIGN PRODUCER) CASES
    // ──────────────────────────────────────────────────────────

    private static string V1(string name) => Path.Combine(TestDataRoot, "v1", name);

    private static string V2(string name) => Path.Combine(TestDataRoot, "v2", name);

    private static string ParquetNet603(string name) =>
        Path.Combine(TestDataProducersRoot, "parquet-net-6.0.3", name);

    private static async Task<MatrixObservation> FixtureIdenticalAsync(
        string path,
        int expectedRows
    )
    {
        await using FileStream stream = OpenFixture(path);
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<TestUserRecord> read = await TestUserRecordParquetExtensions.ReadParquetAsync(stream);
        Assert.Equal(expectedRows, read.Count);
        Assert.Equal("user_0", read[0].Name);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            $"{read.Count} rows read from {Path.GetFileName(path)}"
        );
    }

    private static async Task<MatrixObservation> FixtureReorderedAsync(
        string path,
        int expectedRows
    )
    {
        await using FileStream stream = OpenFixture(path);
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoReversedFixtureUser> read =
            await EvoReversedFixtureUserParquetExtensions.ReadParquetAsync(stream);

        Assert.Equal(expectedRows, read.Count);
        // Positional resolution would transpose id and created_at_ms, which differ by orders of
        // magnitude, so this pair is what makes the assertion meaningful.
        Assert.Equal(0, read[0].Id);
        Assert.Equal(1_700_000_000_000L, read[0].CreatedAtMs);
        Assert.Equal("user_0", read[0].Name);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            "model declares the fixture's five columns in reverse order"
        );
    }

    private static async Task<MatrixObservation> FixtureMissingOptionalAsync(
        string path,
        int expectedRows,
        string consumer
    )
    {
        await using FileStream stream = OpenFixture(path);
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoFixtureUser> read = consumer switch
        {
            ParallelReader => await EvoFixtureUserParquetExtensions.ReadParquetParallelAsync(
                stream
            ),
            StreamingReader => await CollectAsync(
                EvoFixtureUserParquetExtensions.ReadParquetStreamAsync(stream)
            ),
            _ => await EvoFixtureUserParquetExtensions.ReadParquetAsync(stream),
        };

        Assert.Equal(expectedRows, read.Count);
        Assert.All(
            read,
            row =>
            {
                Assert.Null(row.Email);
                Assert.Null(row.RetryCount);
            }
        );
        Assert.Equal("user_0", read[0].Name);
        Assert.Equal(1_700_000_000_000L, read[0].CreatedAtMs);

        return new MatrixObservation(
            CompatibilityOutcome.CompatibleWithNulls,
            createdBy,
            formatVersion,
            "email/retry_count absent from the fixture, materialised as null"
        );
    }

    private static async Task<MatrixObservation> FixtureExtraColumnsAsync(
        string path,
        int expectedRows
    )
    {
        await using FileStream stream = OpenFixture(path);
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoSubset> read = await EvoSubsetParquetExtensions.ReadParquetAsync(stream);
        Assert.Equal(expectedRows, read.Count);
        Assert.Equal(0, read[0].Id);
        Assert.Equal("user_0", read[0].Name);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            "three unknown fixture columns ignored"
        );
    }

    private static async Task<MatrixObservation> FixtureExtraColumnsStreamingAsync(
        string path,
        int expectedRows
    )
    {
        await using FileStream stream = OpenFixture(path);
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<EvoSubset> read = await CollectAsync(
            EvoSubsetParquetExtensions.ReadParquetStreamAsync(stream)
        );
        Assert.Equal(expectedRows, read.Count);
        Assert.Equal("user_0", read[0].Name);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            "streamed row-group by row-group with three unknown columns ignored"
        );
    }

    private static async Task<MatrixObservation> FixtureMissingRequiredAsync(
        string path,
        string missingColumn
    )
    {
        await using FileStream stream = OpenFixture(path);
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EvoRequiresAbsentColumnParquetExtensions.ReadParquetAsync(stream)
        );
        AssertClearMissingColumnError(exception, missingColumn);

        return new MatrixObservation(
            CompatibilityOutcome.RejectedWithClearError,
            createdBy,
            formatVersion,
            exception.Message
        );
    }

    /// <summary>
    /// Nested and repeated columns are outside the documented envelope. The reader must say which
    /// column it could not satisfy rather than reading a repeated group as if it were flat.
    /// </summary>
    private static async Task<MatrixObservation> NestedOutsideEnvelopeAsync()
    {
        await using FileStream stream = OpenFixture(V1("04_nested_lists_maps.parquet"));
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EvoNestedProbeParquetExtensions.ReadParquetAsync(stream)
        );
        AssertClearMissingColumnError(exception, "tags");

        return new MatrixObservation(
            CompatibilityOutcome.RejectedWithClearError,
            createdBy,
            formatVersion,
            exception.Message
        );
    }

    private static async Task<MatrixObservation> DuckDbLineItemAsync()
    {
        await using FileStream stream = OpenFixture(
            Path.Combine(BenchmarkDataRoot, "tpch_lineitem_sf001.parquet")
        );
        (string createdBy, string formatVersion) = await ReadFooterAsync(stream);

        List<TpchLineItemRecord> read = await TpchLineItemRecordParquetExtensions.ReadParquetAsync(
            stream
        );

        Assert.Equal(60175, read.Count);
        Assert.Equal(1L, read[0].OrderKey);
        Assert.Equal(24710.35m, read[0].ExtendedPrice);

        return new MatrixObservation(
            CompatibilityOutcome.Compatible,
            createdBy,
            formatVersion,
            $"{read.Count} rows over 16 columns including decimals and dates"
        );
    }

    // ──────────────────────────────────────────────────────────
    //  SHARED HELPERS
    // ──────────────────────────────────────────────────────────

    private static FileStream OpenFixture(string path)
    {
        Assert.True(System.IO.File.Exists(path), $"Fixture not found: {path}");
        return System.IO.File.OpenRead(path);
    }

    /// <summary>
    /// Reads the producer identity and format version out of the file's own footer. This is what
    /// makes the recorded matrix a record of what ran rather than a restatement of the declaration.
    /// </summary>
    private static async Task<(string CreatedBy, string FormatVersion)> ReadFooterAsync(
        Stream stream
    )
    {
        long position = stream.Position;
        stream.Position = 0;
        try
        {
            await using global::Parquet.ParquetReader reader =
                await global::Parquet.ParquetReader.CreateAsync(stream, leaveStreamOpen: true);
            string createdBy = reader.Metadata?.CreatedBy ?? "unknown";
            string formatVersion = (reader.Metadata?.Version ?? 0).ToString(
                CultureInfo.InvariantCulture
            );
            return (createdBy, $"parquet-format v{formatVersion}");
        }
        finally
        {
            stream.Position = position == 0 ? 0 : position;
            stream.Position = 0;
        }
    }

    private static async Task<List<EvoExtended>> ReadExtendedAsync(
        Stream stream,
        string consumer
    ) =>
        consumer switch
        {
            ParallelReader => await EvoExtendedParquetExtensions.ReadParquetParallelAsync(stream),
            StreamingReader => await CollectAsync(
                EvoExtendedParquetExtensions.ReadParquetStreamAsync(stream)
            ),
            _ => await EvoExtendedParquetExtensions.ReadParquetAsync(stream),
        };

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private static void AssertBaselineColumnsSurvived(List<EvoExtended> read)
    {
        List<EvoBaseline> expected = BaselineRows();
        Assert.Equal(expected.Count, read.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, read[i].Id);
            Assert.Equal(expected[i].Name, read[i].Name);
            Assert.Equal(expected[i].Score, read[i].Score);
        }
    }

    private static void AssertAddedColumnsAreNull(List<EvoExtended> read) =>
        Assert.All(
            read,
            row =>
            {
                Assert.Null(row.AddedNote);
                Assert.Null(row.AddedCount);
                Assert.Null(row.AddedAmount);
                Assert.Null(row.AddedPayload);
            }
        );

    /// <summary>
    /// "Fails clearly" means the caller can identify the column from the message alone. A bare
    /// NullReferenceException or a Parquet.Net internal message would not qualify.
    /// </summary>
    private static void AssertClearMissingColumnError(Exception exception, string column)
    {
        Assert.Contains("Required column", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{column}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "was not found in the Parquet file schema",
            exception.Message,
            StringComparison.Ordinal
        );
    }
}
