using System.Globalization;
using Parquet.SourceGenerator.ApiGates;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Unit tests for the signature-only baseline renderer that backs the <c>.api.txt</c> files sitting
/// next to the golden generated sources (issue #215). The golden tests prove the baselines match
/// the emitters; these prove the renderer's own contract — visibility, format and, above all,
/// determinism — on inputs small enough to read.
/// </summary>
public sealed class GeneratedApiBaselineTests
{
    private const string Sample = """
        #nullable enable
        namespace Sample.Space;

        public static partial class Widget
        {
            public static readonly global::Parquet.Schema.ParquetSchema Schema = null!;

            private struct Hidden
            {
                public int NotApi => 1;
            }

            internal const int AlsoNotApi = 3;

            public const int Limit = 512;

            public static async global::System.Threading.Tasks.Task<
                global::System.Collections.Generic.List<int>
            > ReadAsync(
                global::System.IO.Stream stream,
                int maxDegreeOfParallelism = -1,
                global::System.Threading.CancellationToken cancellationToken = default
            )
            {
                await global::System.Threading.Tasks.Task.Yield();
                return new();
            }

            public static void Write(this global::System.IO.Stream stream, string? label) { }

            public readonly struct Batch
            {
                public int RowCount { get; }

                public string? Label { get; init; }

                internal Batch(int rowCount) => RowCount = rowCount;
            }
        }
        """;

    [Fact]
    public void BaselineStartsWithTheNullableHeaderAndListsOneMemberPerLine()
    {
        string baseline = GeneratedApiBaseline.Create(Sample);

        string[] lines = baseline.Split('\n');
        Assert.Equal(GeneratedApiBaseline.NullableHeader, lines[0]);
        Assert.Equal(string.Empty, lines[^1]);
        Assert.All(lines[1..^1], line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public void SignaturesUseThePublicApiGrammarWithGlobalQualifiersStripped()
    {
        string baseline = GeneratedApiBaseline.Create(Sample);

        Assert.Contains(
            "static Sample.Space.Widget.ReadAsync(System.IO.Stream stream, int maxDegreeOfParallelism = -1, "
                + "System.Threading.CancellationToken cancellationToken = default) -> "
                + "System.Threading.Tasks.Task<System.Collections.Generic.List<int>>\n",
            baseline,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "static Sample.Space.Widget.Write(this System.IO.Stream stream, string? label) -> void\n",
            baseline,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "static readonly Sample.Space.Widget.Schema -> Parquet.Schema.ParquetSchema\n",
            baseline,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "const Sample.Space.Widget.Limit = 512 -> int\n",
            baseline,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void NestedTypesAreListedAndAccessorsSplitIntoOneLineEach()
    {
        string baseline = GeneratedApiBaseline.Create(Sample);

        Assert.Contains("Sample.Space.Widget.Batch\n", baseline, StringComparison.Ordinal);
        Assert.Contains(
            "Sample.Space.Widget.Batch.RowCount.get -> int\n",
            baseline,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Sample.Space.Widget.Batch.Label.get -> string?\n",
            baseline,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Sample.Space.Widget.Batch.Label.init -> void\n",
            baseline,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void NonPublicDeclarationsAndPublicMembersOfPrivateTypesAreExcluded()
    {
        string baseline = GeneratedApiBaseline.Create(Sample);

        Assert.DoesNotContain("Hidden", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("NotApi", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("AlsoNotApi", baseline, StringComparison.Ordinal);
        // The internal constructor of a public struct is not public surface either.
        Assert.DoesNotContain("Widget.Batch.Batch(", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderingIsOrdinalAndIndependentOfTheAmbientCulture()
    {
        // A culture-sensitive sort reorders these; StringComparer.Ordinal does not. The repository
        // has been bitten by exactly this before (the MA0002 fix in tools/BenchmarkSummaryGenerator).
        const string CaseSensitive = """
            namespace N;

            public static class T
            {
                public static void Item() { }

                public static void ITEM() { }

                public static void iTem() { }

                public static void Ítem() { }
            }
            """;

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            string turkish = GeneratedApiBaseline.Create(CaseSensitive);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = GeneratedApiBaseline.Create(CaseSensitive);

            Assert.Equal(invariant, turkish);

            string[] members = turkish.Split('\n').Skip(1).Where(line => line.Length > 0).ToArray();
            Assert.Equal(members.OrderBy(line => line, StringComparer.Ordinal).ToArray(), members);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void RenderingIsStableAcrossRepeatedRuns()
    {
        Assert.Equal(GeneratedApiBaseline.Create(Sample), GeneratedApiBaseline.Create(Sample));
    }

    [Fact]
    public void PartialTypeSplitAcrossTwoDeclarationsYieldsASingleTypeEntry()
    {
        const string Partials = """
            namespace N;

            public static partial class T
            {
                public static void A() { }
            }

            public static partial class T
            {
                public static void B() { }
            }
            """;

        string baseline = GeneratedApiBaseline.Create(Partials);

        Assert.Equal(3, GeneratedApiBaseline.CountMembers(baseline));
        Assert.Equal(
            1,
            baseline.Split('\n').Count(l => string.Equals(l, "N.T", StringComparison.Ordinal))
        );
    }

    [Fact]
    public void EnumMembersCarryTheirValueAndImplicitOrdinalsAreResolved()
    {
        const string Enums = """
            namespace N;

            public enum E
            {
                Zero,
                One,
                Ten = 10,
                Eleven,
            }
            """;

        string baseline = GeneratedApiBaseline.Create(Enums);

        Assert.Contains("N.E.Zero = 0 -> N.E\n", baseline, StringComparison.Ordinal);
        Assert.Contains("N.E.One = 1 -> N.E\n", baseline, StringComparison.Ordinal);
        Assert.Contains("N.E.Ten = 10 -> N.E\n", baseline, StringComparison.Ordinal);
        Assert.Contains("N.E.Eleven = 11 -> N.E\n", baseline, StringComparison.Ordinal);
    }
}
