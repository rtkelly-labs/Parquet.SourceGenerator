using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests.PropertyBased;

/// <summary>
/// Property-based coverage of the supported flat schema envelope: random schemas, random values,
/// random row-group layouts and random encoding/compression combinations, checked against an
/// independent engine.
/// </summary>
public sealed class SupportedSchemaPropertyTests
{
    /// <summary>The seeds this run explores.</summary>
    public static TheoryData<long> Seeds
    {
        get
        {
            var data = new TheoryData<long>();
            foreach (long seed in FuzzConfig.Seeds())
            {
                data.Add(seed);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RandomSupportedCaseAgreesWithIndependentEngine(long seed)
    {
        FuzzCase fuzzCase = FuzzCase.FromSeed(seed);

        foreach ((string name, Func<FuzzCase, Task<string?>> check) in FuzzRunner.Checks)
        {
            string? failure = await FuzzRunner.SafeAsync(check, fuzzCase);
            if (failure is null)
            {
                continue;
            }

            FuzzCase minimized = await FuzzShrinker.ShrinkAsync(
                fuzzCase,
                candidate => FuzzRunner.SafeAsync(check, candidate)
            );
            string minimizedFailure = await FuzzRunner.SafeAsync(check, minimized) ?? failure;
            string fixturePath = FuzzConfig.SaveFailure(minimized, $"{name}: {minimizedFailure}");

            Assert.Fail(
                $"""
                Property '{name}' failed.

                  original : {fuzzCase.Describe()}
                  minimised: {minimized.Describe()}
                  failure  : {minimizedFailure}

                Reproduce with:
                  {minimized.ReproCommand()}

                A minimised fixture was written to:
                  {fixturePath}
                Copy it into test/Parquet.SourceGenerator.Tests/FuzzFixtures/ to make the regression permanent.
                """
            );
        }
    }

    [Theory]
    [MemberData(nameof(RegressionFixtures))]
    public async Task CommittedRegressionFixtureStillPasses(string fixtureName)
    {
        string path = Path.Combine(FuzzConfig.FixtureDirectory, fixtureName);
        FuzzCase fuzzCase = FuzzCase.FromJson(IOFile.ReadAllText(path));

        string? failure = await FuzzRunner.RunAllAsync(fuzzCase);

        Assert.True(
            failure is null,
            $"Regression fixture '{fixtureName}' failed again: {failure}\n"
                + $"  case: {fuzzCase.Describe()}\n"
                + $"  repro: {fuzzCase.ReproCommand()}"
        );
    }

    /// <summary>Every committed fixture, so a new one is picked up by dropping in a file.</summary>
    public static TheoryData<string> RegressionFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (
                string path in Directory
                    .EnumerateFiles(FuzzConfig.FixtureDirectory, "*.json")
                    .OrderBy(p => p, StringComparer.Ordinal)
            )
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Fact]
    public void FixtureDirectoryIsNotEmpty()
    {
        Assert.NotEmpty(Directory.EnumerateFiles(FuzzConfig.FixtureDirectory, "*.json"));
    }

    [Fact]
    public void SameSeedProducesIdenticalCaseAndRows()
    {
        const long seed = 424_242;
        FuzzCase first = FuzzCase.FromSeed(seed);
        FuzzCase second = FuzzCase.FromSeed(seed);

        Assert.Equal(first.Describe(), second.Describe());

        IReadOnlyList<FuzzWideRecord> firstRows = first.BuildRows();
        IReadOnlyList<FuzzWideRecord> secondRows = second.BuildRows();
        Assert.Equal(firstRows.Count, secondRows.Count);

        for (int i = 0; i < firstRows.Count; i++)
        {
            foreach (FuzzColumn column in FuzzColumns.All)
            {
                Assert.True(
                    FuzzCompare.Equal(
                        column.NormalizedValue(firstRows[i]),
                        column.NormalizedValue(secondRows[i])
                    ),
                    $"column '{column.Name}' row {i} differs between two generations of seed {seed}"
                );
            }
        }
    }

    [Fact]
    public void ShrinkingRowCountLeavesEarlierRowsUntouched()
    {
        FuzzCase full = FuzzCase.FromSeed(987_654) with { RowCount = 40 };
        FuzzCase shrunk = full with { RowCount = 3 };

        IReadOnlyList<FuzzWideRecord> fullRows = full.BuildRows();
        IReadOnlyList<FuzzWideRecord> shrunkRows = shrunk.BuildRows();

        for (int i = 0; i < shrunkRows.Count; i++)
        {
            foreach (FuzzColumn column in FuzzColumns.All)
            {
                Assert.True(
                    FuzzCompare.Equal(
                        column.NormalizedValue(fullRows[i]),
                        column.NormalizedValue(shrunkRows[i])
                    ),
                    $"column '{column.Name}' row {i} changed when the case was shrunk; "
                        + "shrinking must not perturb the values it keeps"
                );
            }
        }
    }

    [Fact]
    public async Task ShrinkerMinimisesToOneRowAndOneColumn()
    {
        // A synthetic property that fails exactly when 'i32' carries a fuzzed value: the smallest
        // case that still fails is one row with 'i32' as the only fuzzed column.
        FuzzCase failing = FuzzCase.FromSeed(FuzzConfig.DefaultBaseSeed) with
        {
            RowCount = 200,
            Columns = FuzzColumns.All.Select(c => c.Name).ToList(),
        };

        static Task<string?> StillFails(FuzzCase candidate)
        {
            bool fails =
                candidate.Columns.Contains("i32") && candidate.BuildRows().Any(r => r.I32 != 0);
            return Task.FromResult<string?>(fails ? "synthetic failure" : null);
        }

        FuzzCase minimized = await FuzzShrinker.ShrinkAsync(failing, StillFails);

        Assert.Equal(["i32"], minimized.Columns);
        Assert.True(
            minimized.RowCount <= 2,
            $"expected the shrinker to reach 1-2 rows, reached {minimized.RowCount}"
        );
        Assert.NotNull(await StillFails(minimized));
    }

    [Fact]
    public void DefaultSeedsAreDeterministicAndStable()
    {
        // Guards the CI contract: with no environment override the suite always runs this exact
        // seed set, so a green run means the same thing on every machine.
        Assert.Equal(FuzzConfig.DefaultBaseSeed, FuzzConfig.BaseSeed);
        Assert.Equal(FuzzConfig.DefaultCaseCount, FuzzConfig.CaseCount);

        long[] seeds = FuzzConfig.Seeds().ToArray();
        Assert.Equal(FuzzConfig.DefaultCaseCount, seeds.Length);
        Assert.Equal(FuzzConfig.DefaultBaseSeed, seeds[0]);
        Assert.Equal(seeds.Length, seeds.Distinct().Count());
        Assert.Equal(seeds, FuzzConfig.Seeds().ToArray());
    }

    [Fact]
    public void CaseSerialisesAndDeserialisesUnchanged()
    {
        FuzzCase original = FuzzCase.FromSeed(13_579);

        FuzzCase round = FuzzCase.FromJson(original.ToJson());

        Assert.Equal(original.Describe(), round.Describe());
    }

    /// <summary>
    /// A valid Parquet file that omits an optional column reads back with that column as nulls.
    /// </summary>
    /// <remarks>
    /// The fuzzer originally found this as a gap — the reader rejected such files outright — and
    /// this test pinned that behaviour so the suite stayed green while it was open. #168 fixed it:
    /// a column the file does not carry is now materialised as all-null instead of being handed to
    /// Parquet.Net as a lookup for a field that is not there. The test is inverted accordingly and
    /// now asserts the defaults, per its own original instructions.
    /// </remarks>
    [Fact]
    public async Task AbsentNullableColumnMaterialisesAsNulls()
    {
        FuzzCase fuzzCase = FuzzCase.FromSeed(2_468) with
        {
            RowCount = 5,
            Columns = FuzzColumns.All.Select(c => c.Name).ToList(),
        };
        IReadOnlyList<FuzzWideRecord> rows = fuzzCase.BuildRows();
        List<FuzzColumn> columns = FuzzColumns.All.Where(c => c.Name != "opt_text").ToList();

        byte[] bytes = await IndependentParquetEngine.WriteAsync(
            rows,
            columns,
            rowGroupSize: 2,
            ParquetCompressionMethod.Snappy
        );

        using var stream = new MemoryStream(bytes, writable: false);
        List<FuzzWideRecord> read = await FuzzWideRecordParquetExtensions.ReadParquetAsync(stream);

        Assert.Equal(rows.Count, read.Count);
        Assert.All(read, r => Assert.Null(r.OptText));

        // The columns the file does carry must be unaffected by the absent one.
        Assert.Equal(rows.Select(r => r.I64).ToList(), read.Select(r => r.I64).ToList());
    }

    [Fact]
    public async Task GeneratedReaderRejectsAFileMissingARequiredColumn()
    {
        FuzzCase fuzzCase = FuzzCase.FromSeed(1_357) with { RowCount = 3 };
        IReadOnlyList<FuzzWideRecord> rows = fuzzCase.BuildRows();
        List<FuzzColumn> columns = FuzzColumns.All.Where(c => c.Name != "i64").ToList();

        byte[] bytes = await IndependentParquetEngine.WriteAsync(
            rows,
            columns,
            rowGroupSize: 8,
            ParquetCompressionMethod.None
        );

        using var stream = new MemoryStream(bytes, writable: false);
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await FuzzWideRecordParquetExtensions.ReadParquetAsync(stream)
        );

        Assert.Contains("i64", exception.Message, StringComparison.Ordinal);
    }
}
