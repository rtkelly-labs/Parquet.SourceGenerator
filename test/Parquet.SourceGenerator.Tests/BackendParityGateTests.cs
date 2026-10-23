extern alias LegacyGenerator;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.ApiGates;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;
using LegacyIncrementalGenerator = LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The legacy/modern API parity gate (#493, docs/guides/legacy-support.md § Enforcement). Each
/// golden model is run through both real generators; the two emitted surfaces are rendered with the
/// same <c>.api.txt</c> renderer the derived outputs use, normalised (namespace and extension class
/// name), and diffed. Every difference must be in <c>src/api/legacy-parity-allowlist.txt</c>, and
/// every allowlist entry must still be a difference.
/// </summary>
public sealed class BackendParityGateTests
{
    private const string ModelNamespace = "SampleDomain.Models";
    private const string AllowlistPath = "src/api/legacy-parity-allowlist.txt";

    /// <summary>
    /// The golden models that GoldenCorpus emits without the driver (straight from a hand-built
    /// model). The parity gate needs a declaration both generators can run over, so these mirror
    /// GoldenCorpus's property lists; <see cref="ModernDriverSurfaceMatchesThePublishedGoldenApi"/>
    /// fails if one drifts from the published golden API.
    /// </summary>
    private static readonly Dictionary<string, string> UndrivenModelSources = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["OrderEventParquetExtensions.g.cs"] = """
            using Parquet.SourceGenerator;

            namespace SampleDomain.Models;

            [ParquetSerializable]
            public partial class OrderEvent
            {
                [ParquetColumn("id")] public int Id { get; set; }
                [ParquetColumn("name")] public string? Name { get; set; }
                [ParquetColumn("score")] public double Score { get; set; }
                [ParquetColumn("price"), ParquetDecimal(18, 4)] public decimal Price { get; set; }
                [ParquetColumn("created_at"), ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
                public System.DateTime CreatedAt { get; set; }
                [ParquetColumn("duration")] public System.TimeSpan Duration { get; set; }
                [ParquetColumn("correlation_id")] public System.Guid CorrelationId { get; set; }
                [ParquetColumn("optional_guid")] public System.Guid? OptionalGuid { get; set; }
                [ParquetColumn("payload")] public byte[]? Payload { get; set; }
            }
            """,
        ["ScalarMetricParquetExtensions.g.cs"] = """
            using Parquet.SourceGenerator;

            namespace SampleDomain.Models;

            public enum ProcessStatus
            {
                Pending = 0,
                Complete = 1,
            }

            [ParquetSerializable]
            public partial class ScalarMetric
            {
                [ParquetColumn("row_id")] public long RowId { get; set; }
                [ParquetColumn("is_valid")] public bool Flag { get; set; }
                [ParquetColumn("maybe_flag")] public bool? NullableFlag { get; set; }
                [ParquetColumn("status")] public ProcessStatus StatusCode { get; set; }
                [ParquetColumn("opt_status")] public ProcessStatus? OptionalStatus { get; set; }
                [ParquetColumn("tiny_num")] public byte TinyNum { get; set; }
                [ParquetColumn("short_num")] public short ShortNum { get; set; }
                [ParquetColumn("float_val")] public float FloatVal { get; set; }
            }
            """,
        ["LegacyRecordParquetLegacyExtensions.g.cs"] = """
            using Parquet.SourceGenerator;

            namespace SampleDomain.Models;

            public enum AccessLevel
            {
                None = 0,
                Read = 1,
            }

            [ParquetSerializable]
            public partial class LegacyRecord
            {
                [ParquetColumn("id")] public int Id { get; set; }
                [ParquetColumn("desc")] public string? Description { get; set; }
                [ParquetColumn("raw_data")] public byte[]? RawData { get; set; }
                [ParquetColumn("level")] public AccessLevel Level { get; set; }
            }
            """,
    };

    private static readonly Lazy<IReadOnlyList<ParityModelSurface>> Surfaces = new(BuildSurfaces);

    [Fact]
    public void LegacySurfaceDiffersFromModernOnlyByAllowlistedEntries()
    {
        IReadOnlyList<ParityAllowlistEntry> allowlist = BackendParityGate.ParseAllowlist(
            IOFile.ReadAllText(Path.Combine(FindRepoRoot(), AllowlistPath))
        );

        ParityGateResult result = BackendParityGate.Verify(Surfaces.Value, allowlist);

        // Examined-N assertions: every golden model is compared, and both surfaces are non-trivial.
        result.ModelsExamined.ShouldBe(GoldenCorpus.All.Count);
        result.LinesExamined.ShouldBeGreaterThan(100);
        result.Differences.ShouldBe(allowlist.Count);
        result.Errors.ShouldBeEmpty(string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ModernDriverSurfaceMatchesThePublishedGoldenApi()
    {
        // The gate must judge the surface the derived outputs publish. Driver-generated goldens are
        // the same declaration by construction; the three EmitSource goldens are re-declared above.
        foreach (GoldenEmission emission in GoldenCorpus.All.Where(IsModern))
        {
            string model = ModelName(emission);
            ParityModelSurface surface = Surfaces.Value.Single(s => s.Model == model);
            surface.Modern.ShouldBe(
                BackendParityGate.NormalizeApi(GoldenCorpus.ApiOf(emission), ModelNamespace),
                $"{emission.FileName}: the parity corpus's modern surface drifted from the golden API."
            );
        }
    }

    [Fact]
    public void LegacyDriverSurfaceMatchesThePublishedLegacyGoldenApi()
    {
        GoldenEmission legacy = GoldenCorpus.Get("LegacyRecordParquetLegacyExtensions.g.cs");
        ParityModelSurface surface = Surfaces.Value.Single(s => s.Model == "LegacyRecord");
        surface.Legacy.ShouldNotBeNull();
        surface.Legacy.ShouldBe(
            BackendParityGate.NormalizeApi(GoldenCorpus.ApiOf(legacy), ModelNamespace)
        );
    }

    [Fact]
    public void PermanentEntriesAreModernOnlyAndAbsentFromLegacy()
    {
        // Batches() is a recorded owner decision (#490): the legacy backend must not emit it at all,
        // so a multi-target caller gets a compile error rather than a runtime NotSupportedException.
        IReadOnlyList<ParityAllowlistEntry> permanent = BackendParityGate
            .ParseAllowlist(IOFile.ReadAllText(Path.Combine(FindRepoRoot(), AllowlistPath)))
            .Where(e => e.Kind == ParityEntryKind.Permanent)
            .ToList();

        permanent.ShouldNotBeEmpty();
        foreach (ParityAllowlistEntry entry in permanent)
        {
            entry.Direction.ShouldBe(ParityDirection.ModernOnly);
            entry.Line.ShouldContain(".AsBatches(");
            IReadOnlyList<string>? legacy = Surfaces
                .Value.Single(s => s.Model == entry.Model)
                .Legacy;
            (legacy ?? Array.Empty<string>()).ShouldNotContain(entry.Line);
        }
    }

    [Fact]
    public void NormalizationMapsTheLegacyExtensionClassOntoTheModernName()
    {
        BackendParityGate
            .Normalize(
                "static SampleDomain.Models.LegacyRecordParquetLegacyExtensions.WriteParquetAsync(this System.Collections.Generic.IEnumerable<LegacyRecord> items) -> System.Threading.Tasks.Task",
                ModelNamespace
            )
            .ShouldBe(
                "static LegacyRecordParquetExtensions.WriteParquetAsync(this System.Collections.Generic.IEnumerable<LegacyRecord> items) -> System.Threading.Tasks.Task"
            );
    }

    // ---------------------------------------------------------------------------------------
    // Negative controls: each removes or forges evidence and the gate must go red.
    // ---------------------------------------------------------------------------------------

    private const string Header = "[temporary #494] test section\n";

    private static ParityModelSurface Model(string[] modern, string[]? legacy) =>
        new("Row", modern, legacy);

    [Fact]
    public void NegativeControlInjectedMemberDifferenceFails()
    {
        // The real corpus with one extra member injected into the modern side of one model.
        IReadOnlyList<ParityAllowlistEntry> allowlist = BackendParityGate.ParseAllowlist(
            IOFile.ReadAllText(Path.Combine(FindRepoRoot(), AllowlistPath))
        );
        const string Injected = "OrderEventParquetReader.Injected() -> void";
        var surfaces = Surfaces
            .Value.Select(s =>
                s.Model == "OrderEvent"
                    ? new ParityModelSurface(s.Model, s.Modern.Append(Injected).ToList(), s.Legacy)
                    : s
            )
            .ToList();

        ParityGateResult result = BackendParityGate.Verify(surfaces, allowlist);

        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldContain("Unlisted backend difference");
        result.Errors[0].ShouldContain("modern-only OrderEvent: " + Injected);
    }

    [Fact]
    public void NegativeControlRemovingARealDifferenceFromTheAllowlistFails()
    {
        IReadOnlyList<ParityAllowlistEntry> allowlist = BackendParityGate.ParseAllowlist(
            IOFile.ReadAllText(Path.Combine(FindRepoRoot(), AllowlistPath))
        );
        allowlist.Count.ShouldBeGreaterThan(0);
        ParityAllowlistEntry removed = allowlist[0];

        ParityGateResult result = BackendParityGate.Verify(
            Surfaces.Value,
            allowlist.Skip(1).ToList()
        );

        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldContain("Unlisted backend difference");
        result.Errors[0].ShouldContain(removed.Describe());
    }

    [Fact]
    public void NegativeControlStaleAllowlistEntryFails()
    {
        string content =
            IOFile.ReadAllText(Path.Combine(FindRepoRoot(), AllowlistPath))
            + "\n"
            + Header
            + "modern-only OrderEvent: OrderEventParquetReader.NoSuchMember() -> void\n";

        ParityGateResult result = BackendParityGate.Verify(
            Surfaces.Value,
            BackendParityGate.ParseAllowlist(content)
        );

        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldContain("no longer corresponds to a difference");
        result.Errors[0].ShouldContain("NoSuchMember");
    }

    [Fact]
    public void NegativeControlZeroModelsFails()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            BackendParityGate.Verify(
                Array.Empty<ParityModelSurface>(),
                Array.Empty<ParityAllowlistEntry>()
            )
        );
        ex.Message.ShouldContain("examined 0 models");
    }

    [Fact]
    public void NegativeControlEntryThatClosedOnLegacyIsStale()
    {
        // The legacy backend gains the member (the gap closed), and the entry is still listed.
        IReadOnlyList<ParityAllowlistEntry> allowlist = BackendParityGate.ParseAllowlist(
            Header + "modern-only Row: RowParquet.From(Stream s) -> RowParquetReader\n"
        );

        ParityGateResult result = BackendParityGate.Verify(
            [
                Model(
                    ["RowParquet.From(Stream s) -> RowParquetReader"],
                    ["RowParquet.From(Stream s) -> RowParquetReader"]
                ),
            ],
            allowlist
        );

        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldContain("can only shrink");
    }

    [Fact]
    public void NegativeControlPermanentMemberPresentOnLegacyFails()
    {
        IReadOnlyList<ParityAllowlistEntry> allowlist = BackendParityGate.ParseAllowlist(
            "[permanent #490] batches are modern-only\n"
                + "modern-only Row: RowParquetReader.AsBatches() -> X\n"
        );

        ParityGateResult result = BackendParityGate.Verify(
            [Model(["RowParquetReader.AsBatches() -> X"], ["RowParquetReader.AsBatches() -> X"])],
            allowlist
        );

        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldContain("not present-and-throwing");
    }

    [Fact]
    public void WholeModelEntryCoversAModelTheLegacyBackendDoesNotEmit()
    {
        IReadOnlyList<ParityAllowlistEntry> allowlist = BackendParityGate.ParseAllowlist(
            Header + "modern-only Row: *\n"
        );

        BackendParityGate.Verify([Model(["RowParquet"], null)], allowlist).Errors.ShouldBeEmpty();
        BackendParityGate
            .Verify([Model(["RowParquet"], ["RowParquet"])], allowlist)
            .Errors.ShouldHaveSingleItem()
            .ShouldContain("can only shrink");
        BackendParityGate
            .Verify([Model(["RowParquet"], null)], Array.Empty<ParityAllowlistEntry>())
            .Errors.ShouldHaveSingleItem()
            .ShouldContain("modern-only Row: *");
    }

    [Fact]
    public void LegacyOnlyMemberMustBeListed()
    {
        ParityGateResult result = BackendParityGate.Verify(
            [
                Model(
                    ["RowParquet"],
                    ["RowParquet", "static RowParquetExtensions.ReadParquetAsync() -> T"]
                ),
            ],
            Array.Empty<ParityAllowlistEntry>()
        );

        result
            .Errors.ShouldHaveSingleItem()
            .ShouldContain("legacy-only Row: static RowParquetExtensions.ReadParquetAsync() -> T");
    }

    [Theory]
    [InlineData("modern-only Row: X", "outside any")]
    [InlineData("[permanent #490] decision\nlegacy-only Row: X", "permanent legacy-only")]
    [InlineData(
        "[temporary] no issue\nmodern-only Row: X",
        "neither a section header nor an entry"
    )]
    [InlineData("[temporary #494] r\nmodern Row X", "neither a section header nor an entry")]
    public void MalformedAllowlistIsRejected(string content, string expected)
    {
        Should
            .Throw<FormatException>(() => BackendParityGate.ParseAllowlist(content))
            .Message.ShouldContain(expected);
    }

    [Fact]
    public void DuplicateAllowlistEntryFails()
    {
        IReadOnlyList<ParityAllowlistEntry> allowlist = BackendParityGate.ParseAllowlist(
            Header + "modern-only Row: A\nmodern-only Row: A\n"
        );

        BackendParityGate
            .Verify([Model(["A"], [])], allowlist)
            .Errors.ShouldHaveSingleItem()
            .ShouldContain("duplicate");
    }

    // ---------------------------------------------------------------------------------------

    private static List<ParityModelSurface> BuildSurfaces()
    {
        var surfaces = new List<ParityModelSurface>();
        foreach (GoldenEmission emission in GoldenCorpus.All)
        {
            string model = ModelName(emission);
            string source =
                emission.ModelSource
                ?? (
                    UndrivenModelSources.TryGetValue(emission.FileName, out string? declared)
                        ? declared
                        : throw new InvalidOperationException(
                            $"{emission.FileName} has no declaration for the parity gate to run both generators over."
                        )
                );
            bool referenceArrow = emission.FileName.StartsWith("Arrow", StringComparison.Ordinal);

            IReadOnlyList<string>? modern = Emit(
                new ParquetIncrementalGenerator(),
                source,
                model,
                referenceArrow
            );
            modern.ShouldNotBeNull($"The modern generator emitted nothing for {model}.");
            IReadOnlyList<string>? legacy = Emit(
                new LegacyIncrementalGenerator(),
                source,
                model,
                referenceArrow
            );
            surfaces.Add(new ParityModelSurface(model, modern, legacy));
        }

        return surfaces;
    }

    /// <summary>
    /// Runs one generator over the declaration and renders the API of every source it generates
    /// for <paramref name="model"/> (the serializer file and, for Arrow, the bridge file). Returns
    /// <see langword="null"/> when it generates nothing for the model.
    /// </summary>
    private static List<string>? Emit(
        IIncrementalGenerator generator,
        string source,
        string model,
        bool referenceArrow
    )
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ParityGateAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            GoldenCorpus.References(referenceArrow),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        ImmutableArray<GeneratedSourceResult> generated = driver
            .GetRunResult()
            .Results.Single()
            .GeneratedSources;

        string prefix = ModelNamespace + "." + model + ".";
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        bool any = false;
        foreach (GeneratedSourceResult file in generated)
        {
            if (!file.HintName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            any = true;
            lines.UnionWith(
                BackendParityGate.NormalizeApi(
                    GeneratedApiBaseline.Create(file.SourceText.ToString()),
                    ModelNamespace
                )
            );
        }

        return any ? lines.ToList() : null;
    }

    private static bool IsModern(GoldenEmission emission) =>
        !emission.FileName.EndsWith("LegacyExtensions.g.cs", StringComparison.Ordinal);

    private static string ModelName(GoldenEmission emission)
    {
        string stem = emission.FileName.Substring(0, emission.FileName.Length - ".g.cs".Length);
        string suffix = IsModern(emission) ? "ParquetExtensions" : "ParquetLegacyExtensions";
        return stem.Substring(0, stem.Length - suffix.Length);
    }

    private static string FindRepoRoot()
    {
        for (
            DirectoryInfo? dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            dir is not null;
            dir = dir.Parent
        )
        {
            if (IOFile.Exists(Path.Combine(dir.FullName, "Parquet.SourceGenerator.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
