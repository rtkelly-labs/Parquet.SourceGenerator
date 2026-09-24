using Microsoft.CodeAnalysis;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>
/// The build-time API-contract rule. It is an error rather than a warning on purpose: the
/// contract in <c>docs/18-API-CHANGE-CONTRACT.md</c> is that nothing enters a governed surface
/// without appearing in a catalogue file, and a warning is something a build can carry.
/// (<c>PARQAPI001</c>, the emitted-API gate over checked-in golden files, was retired when the
/// golden output became a derived CI artifact; the ID is not reused.)
/// </summary>
public static class ApiGateDiagnostics
{
    /// <summary>Diagnostic category.</summary>
    public const string Category = "ApiSurface";

    /// <summary>
    /// Text appended to the message. It names the escape hatch explicitly, because a gate whose
    /// only documented answer is "do the paperwork" gets disabled the first time someone spikes.
    /// </summary>
    public const string EscapeHatch =
        "To land this on a spike or experiment branch instead, add a docs/api/LEDGER.md entry for it "
        + "marked '**Unapproved-by-design:**' — that suppresses this error locally, and CI rejects "
        + "the marker on main so the branch cannot merge while it is present.";

    /// <summary>PARQAPI002 — a member widened past <c>private</c> with no line in <c>src/api/seams.txt</c>.</summary>
    public static readonly DiagnosticDescriptor UncataloguedInternalSeam = new(
        id: "PARQAPI002",
        title: "Internal seam is not catalogued",
        messageFormat: "Internal seam is not catalogued: '{0}' is widened past private for "
            + "cross-component reuse but is absent from src/api/seams.txt. Add that line verbatim "
            + "to src/api/seams.txt and add a docs/api/LEDGER.md entry with '**Surface:** seam' and "
            + "the 'internal' semver bucket. A catalogue line without a ledger entry is rejected by "
            + "CI. If the member does not need to be reachable from another component, make it "
            + "private instead. "
            + EscapeHatch,
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Members widened past 'private' so another component can call them are the "
            + "repository's internal seams. They are catalogued in src/api/seams.txt using the same "
            + "one-per-line grammar as the emitted API baselines. See "
            + "docs/18-API-CHANGE-CONTRACT.md."
    );
}
