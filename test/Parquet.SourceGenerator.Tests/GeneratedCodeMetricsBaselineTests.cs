using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parquet.SourceGenerator.ApiGates;
using Shouldly;
using Xunit;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Binds the three artefacts the golden suite owns for each model — the emitted source
/// (<c>*.g.cs</c>), its public surface (<c>*.api.txt</c>) and its metrics
/// (<c>*.metrics.txt</c>, layer 2 of #251) — so they cannot be added, renamed or refreshed
/// independently of one another.
///
/// The numbers themselves are produced and gated by <c>scripts/CodeMetrics.cs</c>, which needs a
/// full Roslyn compilation of the emitted code and therefore cannot run inside this suite. What is
/// checked here is everything that does NOT need recomputation: that a baseline exists for every
/// golden model, that its declared member count is the one
/// <see cref="GeneratedApiBaseline.CountMembers(string)"/> gives for the sibling <c>.api.txt</c>
/// (there must be exactly one definition of "an emitted public member" — see #244), that the
/// size-per-capability ratio is arithmetically consistent with the counts it is derived from, and
/// that the file is in the ordinal order the artefact's determinism contract requires.
///
/// See docs/quality/generated-code-metrics.md.
/// </summary>
public sealed class GeneratedCodeMetricsBaselineTests
{
    private static readonly string GoldenFilesDir = IOPath.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "..",
        "..",
        "..",
        "GoldenFiles"
    );

    public static TheoryData<string> GoldenModels()
    {
        var data = new TheoryData<string>();
        foreach (string path in GoldenSourceFiles())
        {
            string name = IOPath.GetFileName(path);
            data.Add(name[..^".g.cs".Length]);
        }

        return data;
    }

    [Fact]
    public void EveryGoldenModelIsDiscovered()
    {
        // A zero-length theory silently passes, which would turn every assertion below into a
        // no-op the first time the directory layout moved.
        GoldenSourceFiles().Count.ShouldBe(7);
    }

    [Theory]
    [MemberData(nameof(GoldenModels))]
    public void EveryGoldenModelHasAMetricsBaselineAndAModelDeclaration(string stem)
    {
        IOFile
            .Exists(IOPath.Combine(GoldenFilesDir, stem + ".metrics.txt"))
            .ShouldBeTrue(
                $"GoldenFiles/{stem}.metrics.txt is missing. Refresh it with "
                    + "UPDATE_GOLDEN_FILES=true dotnet run scripts/CodeMetrics.cs."
            );

        // The emitted code is a fragment that extends a consumer-written type; without that
        // declaration it cannot be compiled and therefore cannot be measured.
        IOFile
            .Exists(IOPath.Combine(GoldenFilesDir, "Models", ModelFileNameFor(stem)))
            .ShouldBeTrue(
                $"GoldenFiles/Models/{ModelFileNameFor(stem)} is missing; scripts/CodeMetrics.cs "
                    + "compiles the golden file against it."
            );
    }

    [Theory]
    [MemberData(nameof(GoldenModels))]
    public void MemberCountOnTheSummaryLineIsTheApiBaselineMemberCount(string stem)
    {
        Dictionary<string, string> summary = SummaryFields(stem);
        int expected = GeneratedApiBaseline.CountMembers(
            IOFile
                .ReadAllText(IOPath.Combine(GoldenFilesDir, stem + ".api.txt"))
                .Replace("\r\n", "\n", StringComparison.Ordinal)
        );

        int.Parse(summary["MEMBERS"], CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(GoldenModels))]
    public void SizePerCapabilityRatioIsConsistentWithTheCountsItIsDerivedFrom(string stem)
    {
        Dictionary<string, string> summary = SummaryFields(stem);
        int members = int.Parse(summary["MEMBERS"], CultureInfo.InvariantCulture);
        long executableLines = long.Parse(summary["ELOC"], CultureInfo.InvariantCulture);

        summary["ELOC_PER_MEMBER"]
            .ShouldBe(
                ((double)executableLines / members).ToString("F1", CultureInfo.InvariantCulture)
            );
    }

    [Theory]
    [MemberData(nameof(GoldenModels))]
    public void EntriesAreInOrdinalOrder(string stem)
    {
        List<string> keys = BaselineLines(stem)
            .Where(line =>
                line.Length > 0
                && line[0] != '#'
                && !line.StartsWith("S:", StringComparison.Ordinal)
            )
            .Select(line => line[..line.LastIndexOf(" | ", StringComparison.Ordinal)])
            .ToList();

        keys.ShouldNotBeEmpty();
        keys.ShouldBe(keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    private static List<string> GoldenSourceFiles() =>
        IODirectory
            .GetFiles(GoldenFilesDir, "*.g.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string ModelFileNameFor(string stem) =>
        (
            stem.EndsWith("ParquetLegacyExtensions", StringComparison.Ordinal)
                ? stem[..^"ParquetLegacyExtensions".Length]
                : stem[..^"ParquetExtensions".Length]
        ) + ".cs";

    private static string[] BaselineLines(string stem) =>
        IOFile
            .ReadAllText(IOPath.Combine(GoldenFilesDir, stem + ".metrics.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

    private static Dictionary<string, string> SummaryFields(string stem)
    {
        string line = BaselineLines(stem).Single(l => l.StartsWith("S:", StringComparison.Ordinal));
        int bar = line.LastIndexOf(" | ", StringComparison.Ordinal);
        line[2..bar].ShouldBe(stem);

        return line[(bar + 3)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }
}
