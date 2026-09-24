using Microsoft.CodeAnalysis.Text;
using Parquet.SourceGenerator.ApiGates;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Unit tests for the two pieces of the API change contract that are pure logic: reading a
/// catalogue file, and deciding whether an <c>**Unapproved-by-design:**</c> ledger entry covers a
/// signature (<c>docs/quality/api-change-contract.md</c>).
/// </summary>
/// <remarks>
/// The gates themselves are proven by the build: <c>PARQAPI001</c> runs over
/// <c>GoldenFiles/*.g.cs</c> in this project's own compilation, and <c>PARQAPI002</c> over the
/// three <c>src/</c> projects. What is not proven by the build is the matching rule — an
/// over-eager one would silently exempt members nobody meant to exempt, and that failure is
/// invisible precisely because it produces no error.
/// </remarks>
public sealed class ApiChangeContractTests
{
    private const string SpikeLine =
        "static SampleDomain.Models.OrderEventParquetExtensions.SpikeCount(this System.IO.Stream stream, int scale = 2) -> int";

    [Fact]
    public void CatalogueSkipsBlankLinesCommentsAndTheNullableHeader()
    {
        var catalogue = ApiCatalogue.Read(
            SourceText.From(
                """
                #nullable enable
                # A comment explaining the file.

                Sample.Space.Widget
                  static Sample.Space.Widget.Read() -> int
                """
            )
        );

        catalogue.Count.ShouldBe(2);
        catalogue.ShouldContain("Sample.Space.Widget");
        catalogue.ShouldContain("static Sample.Space.Widget.Read() -> int");
    }

    [Theory]
    [InlineData("Sample.Space.Widget", "Widget")]
    [InlineData("static Sample.Space.Widget.Read(int a) -> int", "Read")]
    [InlineData("Sample.Space.Widget.Widget(int a) -> void", "Widget")]
    [InlineData("Sample.Space.Widget.Label.get -> string?", "get")]
    [InlineData("const Sample.Space.Widget.Limit = 512 -> int", "Limit")]
    [InlineData("static Sample.Space.Widget.Map<T>(T value) -> T", "Map")]
    public void SimpleNameIsTheMemberIdentifier(string line, string expected) =>
        ApiCatalogue.SimpleName(line).ShouldBe(expected);

    [Fact]
    public void AnEntryWithoutTheMarkerExemptsNothing()
    {
        ApiLedger ledger = ApiLedger.Read(
            SourceText.From(
                """
                ### 2026-09-11 — `SpikeCount(Stream, int)`
                - **Surface:** emitted
                - **Semver:** additive-minor
                """
            )
        );

        ledger.IsEmpty.ShouldBeTrue();
        ledger.IsExempt(SpikeLine).ShouldBeFalse();
    }

    [Fact]
    public void AMarkedEntryExemptsBySignatureShapeInTheHeading()
    {
        ApiLedger ledger = ApiLedger.Read(
            SourceText.From(
                """
                ### 2026-09-11 — `SpikeCount(Stream, int)`
                - **Surface:** emitted
                - **Unapproved-by-design:** measuring before committing to the shape.
                """
            )
        );

        ledger.IsExempt(SpikeLine).ShouldBeTrue();
    }

    [Fact]
    public void AMarkedEntryExemptsByBareMemberNameAndByVerbatimCatalogueLine()
    {
        ApiLedger
            .Read(
                SourceText.From(
                    "### 2026-09-11 — `SpikeCount`\n- **Unapproved-by-design:** spike.\n"
                )
            )
            .IsExempt(SpikeLine)
            .ShouldBeTrue();

        ApiLedger
            .Read(
                SourceText.From(
                    $"### 2026-09-11 — spike\n- **Unapproved-by-design:** `{SpikeLine}`\n"
                )
            )
            .IsExempt(SpikeLine)
            .ShouldBeTrue();
    }

    [Fact]
    public void MatchingIsNotSubstringBased()
    {
        // `Spike` is a prefix of `SpikeCount`, and `Count` a suffix. Neither may exempt it: one
        // hatch entry silencing an unrelated member is the failure mode that makes an escape hatch
        // a hole instead of a hatch.
        ApiLedger ledger = ApiLedger.Read(
            SourceText.From(
                "### 2026-09-11 — `Spike` and `Count` and `SpikeCounter`\n"
                    + "- **Unapproved-by-design:** spike.\n"
            )
        );

        ledger.IsExempt(SpikeLine).ShouldBeFalse();
    }

    [Fact]
    public void TheMarkerOnlyAppliesToItsOwnEntry()
    {
        ApiLedger ledger = ApiLedger.Read(
            SourceText.From(
                """
                ### 2026-09-11 — `SpikeCount(Stream, int)`
                - **Unapproved-by-design:** spike.

                ### 2026-09-10 — `Approved(Stream)`
                - **Semver:** additive-minor
                """
            )
        );

        ledger.IsExempt(SpikeLine).ShouldBeTrue();
        ledger.IsExempt("static Ns.T.Approved(System.IO.Stream stream) -> int").ShouldBeFalse();
    }

    [Fact]
    public void TheCheckedInLedgerCarriesNoEscapeHatchEntry()
    {
        // A committed `**Unapproved-by-design:**` marker suppresses PARQAPI001/PARQAPI002 for the
        // signature it names. CI rejects it on main; this fails a developer's local run too.
        string ledgerPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "docs",
            "api",
            "LEDGER.md"
        );

        System.IO.File.Exists(ledgerPath).ShouldBeTrue($"Ledger not found at {ledgerPath}");
        ApiLedger
            .Read(SourceText.From(System.IO.File.ReadAllText(ledgerPath)))
            .IsEmpty.ShouldBeTrue(
                "docs/api/LEDGER.md carries an '**Unapproved-by-design:**' entry, which cannot merge to main."
            );
    }
}
