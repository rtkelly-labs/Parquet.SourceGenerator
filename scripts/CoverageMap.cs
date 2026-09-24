#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// -----------------------------------------------------------------------------
// CoverageMap.cs
//
// Issue #259: the checked-in coverage envelope for accepted property kinds. This is a contract
// map, not a line-coverage report. The accepted side is read from the parser and both generator
// entry points; the evidence side names the tests that prove emission, compilation, and runtime
// round-tripping. A new accepted kind, backend, or compound dial therefore fails the gate until
// someone records the evidence and its remaining risk.
//
// Usage:
//   dotnet run scripts/CoverageMap.cs             # compare the checked-in map
//   dotnet run scripts/CoverageMap.cs -- --update # refresh docs/reference/coverage-map.md
// -----------------------------------------------------------------------------

const string OutputPath = "docs/reference/coverage-map.md";

string repoRoot = FindRepositoryRoot();
Directory.SetCurrentDirectory(repoRoot);

bool update = args.Contains("--update", StringComparer.Ordinal);
if (args.Any(argument => argument is not "--update"))
{
    Console.Error.WriteLine("Usage: dotnet run scripts/CoverageMap.cs [-- --update]");
    return 2;
}

SourceContract contract = SourceContract.Read();
List<CoverageRow> rows = BuildRows(contract);
string rendered = Render(contract, rows).Replace("\r\n", "\n", StringComparison.Ordinal);

if (update)
{
    WriteIfChanged(OutputPath, rendered);
    Console.WriteLine($"Wrote {OutputPath} ({rows.Count} matrix rows).");
    return 0;
}

if (!File.Exists(OutputPath))
{
    Console.Error.WriteLine(
        $"Coverage map missing: {OutputPath}. Refresh it with dotnet run scripts/CoverageMap.cs -- --update."
    );
    return 1;
}

string expected = File.ReadAllText(OutputPath);
if (!string.Equals(expected, rendered, StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        $"Coverage map drifted: {OutputPath}. Refresh it with dotnet run scripts/CoverageMap.cs -- --update and review the diff."
    );
    return 1;
}

Console.WriteLine($"Coverage map matches source-derived contract ({rows.Count} matrix rows).");
return 0;

static List<CoverageRow> BuildRows(SourceContract contract)
{
    var rows = new List<CoverageRow>();

    foreach ((string type, string kind) in contract.LeafTypes)
    {
        foreach (Backend backend in Enum.GetValues<Backend>())
        {
            bool accepted =
                backend == Backend.V6 || !contract.ClassicUnsupportedTypes.Contains(type);
            rows.Add(
                EvidenceCatalog.Leaf(
                    type,
                    kind,
                    backend,
                    accepted,
                    contract.ClassicUnsupportedTypes.Contains(type)
                )
            );
        }
    }

    foreach (Backend backend in Enum.GetValues<Backend>())
    {
        IReadOnlySet<string> backendCompoundKinds =
            backend == Backend.V6 ? contract.ModernCompoundKinds : contract.LegacyCompoundKinds;
        bool structShipping = backendCompoundKinds.Contains("Struct");
        bool listShipping = backendCompoundKinds.Contains("List");
        bool mapParser = contract.CompoundKinds.Contains("Map");

        rows.Add(
            EvidenceCatalog.Compound(
                "Struct",
                "nested reference struct",
                "required or nullable",
                backend,
                structShipping,
                parserOnly: false,
                highRisk: false
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "Struct",
                "nested value struct",
                "required",
                backend,
                structShipping,
                parserOnly: false,
                highRisk: false
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "Struct",
                "nullable value struct",
                "nullable",
                backend,
                structShipping,
                parserOnly: false,
                highRisk: true
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "Struct",
                "struct in struct",
                "required or nullable",
                backend,
                structShipping,
                parserOnly: false,
                highRisk: false
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "List",
                "root list of leaf values",
                "collection and element required or nullable",
                backend,
                listShipping,
                parserOnly: false,
                highRisk: false
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "List",
                "root list of reference structs",
                "collection and child leaves required or nullable",
                backend,
                listShipping,
                parserOnly: false,
                highRisk: false
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "List",
                "list of value structs",
                "collection nullable",
                backend,
                accepted: false,
                parserOnly: contract.ListValueStructParserPath,
                highRisk: true
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "List",
                "list of nested lists",
                "collection nullable",
                backend,
                accepted: false,
                parserOnly: true,
                highRisk: true
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "List",
                "list nested inside a struct",
                "collection nullable",
                backend,
                accepted: false,
                parserOnly: contract.NestedListPipelineGuard,
                highRisk: true
            )
        );
        rows.Add(
            EvidenceCatalog.Compound(
                "Map",
                "string-keyed map",
                "map and value nullable",
                backend,
                accepted: false,
                parserOnly: mapParser,
                highRisk: true
            )
        );
    }

    ValidateDerivedRows(contract, rows);
    return rows.OrderBy(row => row.Key, StringComparer.Ordinal).ToList();
}

static void ValidateDerivedRows(SourceContract contract, IReadOnlyList<CoverageRow> rows)
{
    string[] propertyKinds = contract
        .PropertyKinds.OrderBy(kind => kind, StringComparer.Ordinal)
        .ToArray();
    string[] mappedKinds = rows.Select(row => row.Kind)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(kind => kind, StringComparer.Ordinal)
        .ToArray();
    string[] missingKinds = propertyKinds.Except(mappedKinds, StringComparer.Ordinal).ToArray();
    if (missingKinds.Length > 0)
    {
        throw new InvalidOperationException(
            $"Coverage map has no row for PropertyKind values: {string.Join(", ", missingKinds)}."
        );
    }

    string[] unexpectedKinds = mappedKinds.Except(propertyKinds, StringComparer.Ordinal).ToArray();
    if (unexpectedKinds.Length > 0)
    {
        throw new InvalidOperationException(
            $"Coverage map contains kinds that PropertyKind.cs does not define: {string.Join(", ", unexpectedKinds)}."
        );
    }

    foreach (CoverageRow row in rows)
    {
        foreach (string evidencePath in row.EvidencePaths)
        {
            if (!File.Exists(evidencePath))
            {
                throw new InvalidOperationException(
                    $"Coverage row {row.Key} names missing evidence file {evidencePath}."
                );
            }
        }
    }

    string[] expectedBackends = ["V4/V5", "V6"];
    string[] actualBackends = rows.Select(row => row.Backend)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    if (!expectedBackends.SequenceEqual(actualBackends, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"Coverage map backend rows drifted. Expected {string.Join(", ", expectedBackends)}; got {string.Join(", ", actualBackends)}."
        );
    }

    if (
        !contract.PropertyKinds.Contains("Struct")
        || !contract.PropertyKinds.Contains("List")
        || !contract.PropertyKinds.Contains("Map")
    )
    {
        throw new InvalidOperationException(
            "PropertyKind must retain Struct, List, and Map so compound envelope rows remain meaningful."
        );
    }

    if (!contract.NullableContractPresent)
    {
        throw new InvalidOperationException(
            "TargetParser.IsNullableColumn is missing. The coverage map cannot claim a nullability contract."
        );
    }
}

static string Render(SourceContract contract, IReadOnlyList<CoverageRow> rows)
{
    var builder = new StringBuilder();
    builder.AppendLine("<!-- generated by scripts/CoverageMap.cs; do not edit by hand -->");
    builder.AppendLine("# Coverage envelope map (#259)");
    builder.AppendLine();
    builder.AppendLine(
        "This map records the accepted model envelope and the evidence currently checked into the repository. It is deliberately not a claim of complete behavioural coverage."
    );
    builder.AppendLine();
    builder.AppendLine("## How to read it");
    builder.AppendLine();
    builder.AppendLine(
        "The accepted columns come from `PropertyKind`, `TargetParser`, and the two generator entry points. `Shipping` means the real generator path accepts the row. `Parser-only` means the recursive parser test seam can build it, but no shipping emitter consumes it. `Rejected` is an intentional compile-time boundary."
    );
    builder.AppendLine();
    builder.AppendLine(
        "`both` in the nullability column means the required and nullable forms are represented by the same parser/emitter branch and have evidence for both where the row is marked covered. `partial` means the classic backend has direct emitter or package evidence, but not a dedicated round-trip for that exact type."
    );
    builder.AppendLine();
    builder.AppendLine(
        $"Source-derived facts: `{contract.PropertyKinds.Count}` PropertyKind values, `{contract.LeafTypes.Count}` leaf type families, modern compound dial `{FormatDial(contract.ModernCompoundKinds)}`, classic compound dial `{FormatDial(contract.LegacyCompoundKinds)}`, and classic exclusions `{string.Join(", ", contract.ClassicUnsupportedTypes.OrderBy(value => value, StringComparer.Ordinal))}`."
    );
    builder.AppendLine();
    builder.AppendLine("## Matrix");
    builder.AppendLine();
    builder.AppendLine(
        "| Kind | Type or shape | Nesting | Nullability | Backend | Acceptance | Emitted | Compiled | Round-tripped | V5 evidence | Risk | Evidence |"
    );
    builder.AppendLine("|:---|:---|:---|:---|:---:|:---|:---:|:---:|:---:|:---:|:---|:---|");

    foreach (CoverageRow row in rows)
    {
        builder.AppendLine(
            $"| `{row.Kind}` | `{row.TypeOrShape}` | {row.Nesting} | {row.Nullability} | {row.Backend} | {row.Acceptance} | {row.Emitted} | {row.Compiled} | {row.RoundTripped} | {row.V5Evidence} | {row.Risk} | {FormatEvidence(row.EvidencePaths)} |"
        );
    }

    builder.AppendLine();
    builder.AppendLine("## Risk-ranked gaps and regression-sensitive coverage");
    builder.AppendLine();
    builder.AppendLine(
        "The rows below need attention because they are rejected, parser-only, partially evidenced, or carry a known regression risk. Risk ranks the cost of silently emitting or reconstructing the wrong schema/value shape, not the amount of code involved."
    );
    builder.AppendLine();
    builder.AppendLine("| Rank | Matrix row | Why it is here | Next evidence |");
    builder.AppendLine("|:---:|:---|:---|:---|");

    foreach (
        CoverageRow row in rows.Where(NeedsRiskReview)
            .OrderBy(row => RiskRank(row.Risk))
            .ThenBy(row => row.Key, StringComparer.Ordinal)
    )
    {
        builder.AppendLine(
            $"| {row.Risk} | `{row.Key}` | {row.RiskReason} | {FormatEvidence(row.EvidencePaths)} |"
        );
    }

    builder.AppendLine();
    builder.AppendLine("## Boundaries and limitations");
    builder.AppendLine();
    builder.AppendLine(
        "The v6 shipping pipeline currently emits flat leaves, nested structs, root-level lists of leaf values, and root-level lists of attributed reference structs whose members are leaves. Maps and broader recursive compound trees remain parser-only or intentionally rejected by the pipeline."
    );
    builder.AppendLine();
    builder.AppendLine(
        "The classic generator uses the v4/v5 `DataColumn` API. Its checked-in evidence proves the shipped package path for a representative flat model and exercises the legacy emitter broadly, but it does not turn every classic row into a dedicated runtime round-trip. The two `ReadOnlyMemory<T>` rows are rejected by `PARQ011`, because the classic API does not support those representations."
    );
    builder.AppendLine();
    builder.AppendLine(
        "Issue #255 remains a high-risk regression-sensitive row even though it now has compile and round-trip evidence. Nullable value-type struct members need both a presence test and an explicit `.Value` unwrap on write, plus a definition-level presence gate on read. The map keeps that row visible so a future emitter change cannot make the test evidence disappear into an aggregate claim."
    );
    builder.AppendLine();
    builder.AppendLine(
        "The gate checks source-derived acceptance, evidence-file existence, stable row keys, and byte-for-byte rendered output. It does not prove that every named test still exercises the exact row, nor does it replace the behavioural, golden, package-consumption, interop, AOT, or line/branch coverage gates."
    );
    builder.AppendLine();
    builder.AppendLine(
        "Refresh after an intentional envelope or evidence change with `dotnet run scripts/CoverageMap.cs -- --update`, then review the generated diff."
    );
    return builder.ToString();
}

static bool NeedsRiskReview(CoverageRow row) =>
    row.Risk != "Low" || row.TypeOrShape == "nullable value struct";

static int RiskRank(string risk) =>
    risk.StartsWith("Critical", StringComparison.Ordinal) ? 0
    : risk.StartsWith("High", StringComparison.Ordinal) ? 1
    : risk.StartsWith("Medium", StringComparison.Ordinal) ? 2
    : 3;

static string FormatEvidence(IReadOnlyList<string> paths) =>
    string.Join("<br>", paths.Select(path => $"[`{path}`](../../{path})"));

static string FormatDial(IReadOnlySet<string> kinds) =>
    kinds.Count == 0
        ? "None"
        : string.Join(" | ", kinds.OrderBy(value => value, StringComparer.Ordinal));

static void WriteIfChanged(string path, string content)
{
    if (
        File.Exists(path)
        && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal)
    )
        return;

    string? directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);
    File.WriteAllText(path, content, new UTF8Encoding(false));
}

static string FindRepositoryRoot()
{
    string? current = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(current))
    {
        if (File.Exists(Path.Combine(current, "Parquet.SourceGenerator.slnx")))
            return current;
        current = Directory.GetParent(current)?.FullName;
    }

    current = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(current))
    {
        if (File.Exists(Path.Combine(current, "Parquet.SourceGenerator.slnx")))
            return current;
        current = Directory.GetParent(current)?.FullName;
    }

    throw new InvalidOperationException("Could not locate the repository root.");
}

enum Backend
{
    V6,
    V4V5,
}

sealed record CoverageRow(
    string Kind,
    string TypeOrShape,
    string Nesting,
    string Nullability,
    string Backend,
    string Acceptance,
    string Emitted,
    string Compiled,
    string RoundTripped,
    string V5Evidence,
    string Risk,
    string RiskReason,
    IReadOnlyList<string> EvidencePaths
)
{
    public string Key => string.Join("/", Kind, TypeOrShape, Nesting, Nullability, Backend);
}

sealed record SourceContract(
    IReadOnlyList<string> PropertyKinds,
    IReadOnlyList<(string Type, string Kind)> LeafTypes,
    IReadOnlySet<string> CompoundKinds,
    IReadOnlySet<string> ModernCompoundKinds,
    IReadOnlySet<string> LegacyCompoundKinds,
    IReadOnlySet<string> ClassicUnsupportedTypes,
    bool NullableContractPresent,
    bool ListValueStructParserPath,
    bool NestedListPipelineGuard
)
{
    private const string ParserPath = "src/Parquet.SourceGenerator/Parser/TargetParser.cs";
    private const string CompoundKindsPath = "src/Parquet.SourceGenerator/Parser/CompoundKinds.cs";
    private const string PropertyModelPath = "src/Parquet.SourceGenerator/Models/PropertyModel.cs";
    private const string ModernGeneratorPath =
        "src/Parquet.SourceGenerator/ParquetIncrementalGenerator.cs";
    private const string LegacyGeneratorPath =
        "src/Parquet.SourceGenerator.Legacy/ParquetLegacyIncrementalGenerator.cs";

    public static SourceContract Read()
    {
        SyntaxTree modelTree = Parse(PropertyModelPath);
        SyntaxTree parserTree = Parse(ParserPath);
        SyntaxTree compoundKindsTree = Parse(CompoundKindsPath);
        SyntaxTree modernTree = Parse(ModernGeneratorPath);
        SyntaxTree legacyTree = Parse(LegacyGeneratorPath);

        EnumDeclarationSyntax propertyKind = modelTree
            .GetRoot()
            .DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == "PropertyKind");
        List<string> propertyKinds = propertyKind
            .Members.Select(member => member.Identifier.ValueText)
            .ToList();

        HashSet<string> passthrough = ExtractStringSet(parserTree, "SupportedPassthroughTypes");
        HashSet<string> classicUnsupported = ExtractStringSet(
            parserTree,
            "ClassicApiUnsupportedTypes"
        );
        Dictionary<string, string> dedicatedKinds = ExtractDedicatedKinds(parserTree);
        if (dedicatedKinds.Count < 6)
            throw new InvalidOperationException(
                $"Dedicated kinds incomplete: {string.Join(", ", dedicatedKinds.Select(pair => $"{pair.Key}={pair.Value}"))}"
            );
        List<(string Type, string Kind)> leaves = passthrough
            .OrderBy(type => type, StringComparer.Ordinal)
            .Select(type =>
                (type, dedicatedKinds.TryGetValue(type, out string? kind) ? kind : "Primitive")
            )
            .ToList();
        leaves.AddRange(
            dedicatedKinds
                .Where(pair => !passthrough.Contains(pair.Key))
                .Select(pair => (pair.Key, pair.Value))
        );
        leaves.Add(("byte[]", "ByteArray"));
        leaves.Add(("enum", "Enum"));
        HashSet<string> compoundKinds = ExtractEnumNames(compoundKindsTree, "CompoundKinds");
        HashSet<string> modernCompoundKinds = ExtractCompoundDial(modernTree);
        HashSet<string> legacyCompoundKinds = ExtractCompoundDial(legacyTree);

        string parserText = File.ReadAllText(ParserPath);
        return new SourceContract(
            propertyKinds,
            leaves.OrderBy(item => item.Type, StringComparer.Ordinal).ToList(),
            compoundKinds,
            modernCompoundKinds,
            legacyCompoundKinds,
            classicUnsupported,
            parserText.Contains("private static bool IsNullableColumn", StringComparison.Ordinal)
                && parserText.Contains("NullableAnnotation.Annotated", StringComparison.Ordinal)
                && parserText.Contains("NullableAnnotation.NotAnnotated", StringComparison.Ordinal),
            parserText.Contains("CompoundIsValueType", StringComparison.Ordinal)
                && parserText.Contains("return false", StringComparison.Ordinal),
            parserText.Contains("scope.CompoundDepth > 0", StringComparison.Ordinal)
        );
    }

    private static SyntaxTree Parse(string path) =>
        CSharpSyntaxTree.ParseText(
            File.ReadAllText(path),
            new CSharpParseOptions(LanguageVersion.Latest),
            path: path
        );

    private static HashSet<string> ExtractStringSet(SyntaxTree tree, string variableName)
    {
        VariableDeclaratorSyntax variable = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == variableName);
        InitializerExpressionSyntax? initializer = variable
            .Initializer?.Value.DescendantNodesAndSelf()
            .OfType<InitializerExpressionSyntax>()
            .LastOrDefault();
        if (initializer is null)
            throw new InvalidOperationException(
                $"{variableName} no longer has a literal initializer."
            );

        return initializer
            .DescendantNodesAndSelf()
            .OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(literal => literal.Token.ValueText)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ExtractDedicatedKinds(SyntaxTree tree)
    {
        MethodDeclarationSyntax method = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == "TryClassifyKind");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (
            AssignmentExpressionSyntax assignment in method
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(assignment => assignment.Left.ToString() == "kind")
        )
        {
            string kind = assignment.Right.ToString().Split('.').Last();
            SwitchSectionSyntax? section = assignment
                .Ancestors()
                .OfType<SwitchSectionSyntax>()
                .FirstOrDefault();
            if (section is null)
                continue;

            foreach (
                LiteralExpressionSyntax literal in section
                    .Labels.SelectMany(label =>
                        label.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>()
                    )
                    .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            )
            {
                result[literal.Token.ValueText] = kind;
            }
        }

        return result;
    }

    private static HashSet<string> ExtractEnumNames(SyntaxTree tree, string enumName)
    {
        EnumDeclarationSyntax declaration = tree.GetRoot()
            .DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText == enumName);
        return declaration
            .Members.Select(member => member.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractCompoundDial(SyntaxTree tree) =>
        tree.GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access =>
                access.Expression.ToString().Contains("CompoundKinds", StringComparison.Ordinal)
            )
            .Select(access => access.Name.Identifier.ValueText)
            .Where(name => name is "Struct" or "List" or "Map")
            .ToHashSet(StringComparer.Ordinal);
}

static class EvidenceCatalog
{
    private const string TypeMatrix =
        "test/Parquet.SourceGenerator.Tests/GeneratedTypeMatrixTests.cs";
    private const string TypeCoverage = "test/Parquet.SourceGenerator.Tests/TypeCoverageTests.cs";
    private const string LegacyEmitter = "test/Parquet.SourceGenerator.Tests/LegacyEmitterTests.cs";
    private const string Golden =
        "test/Parquet.SourceGenerator.Tests/GoldenCodeGenRegressionTests.cs";
    private const string PackageLegacy = "test/PackageConsumptionLegacy/Program.cs";
    private const string NestedStruct =
        "test/Parquet.SourceGenerator.Tests/NestedStructRoundTripTests.cs";
    private const string NestedList =
        "test/Parquet.SourceGenerator.Tests/NestedListRoundTripTests.cs";
    private const string CompoundParser =
        "test/Parquet.SourceGenerator.Tests/CompoundModelParsingTests.cs";
    private const string LegacyCompoundParser =
        "test/Parquet.SourceGenerator.Tests/LegacyCompoundModelParsingTests.cs";

    public static CoverageRow Leaf(
        string type,
        string kind,
        Backend backend,
        bool accepted,
        bool classicRejected
    )
    {
        string backendName = backend == Backend.V6 ? "V6" : "V4/V5";
        if (backend == Backend.V6)
        {
            return new CoverageRow(
                kind,
                type,
                "flat",
                "required or nullable",
                backendName,
                "Shipping",
                "yes",
                "yes",
                "yes",
                "n/a",
                "Low",
                "The v6 type matrix exercises required and nullable leaf branches.",
                [TypeMatrix, TypeCoverage]
            );
        }

        if (classicRejected)
        {
            return new CoverageRow(
                kind,
                type,
                "flat",
                "required or nullable",
                backendName,
                "Rejected",
                "no",
                "no",
                "no",
                "PARQ011",
                "High",
                "The classic parser must reject the v6-only representation before emission.",
                [LegacyCompoundParser]
            );
        }

        return new CoverageRow(
            kind,
            type,
            "flat",
            "required or nullable",
            backendName,
            accepted ? "Shipping" : "Rejected",
            accepted ? "partial" : "no",
            accepted ? "partial" : "no",
            accepted ? "partial" : "no",
            accepted ? "partial" : "PARQ006",
            accepted ? "Medium" : "High",
            accepted
                ? "The legacy emitter and package smoke test cover representative flat fields, not this exact type in every row."
                : "The classic backend does not accept this member shape.",
            [LegacyEmitter, Golden, PackageLegacy]
        );
    }

    public static CoverageRow Compound(
        string kind,
        string shape,
        string nullability,
        Backend backend,
        bool accepted,
        bool parserOnly,
        bool highRisk
    )
    {
        string backendName = backend == Backend.V6 ? "V6" : "V4/V5";
        string acceptance =
            accepted ? "Shipping"
            : parserOnly ? "Parser-only"
            : "Rejected";
        bool covered = accepted && !parserOnly;
        string emitted = covered ? "yes" : "no";
        string compiled = covered ? "yes" : "no";
        string roundTripped = covered ? "yes" : "no";
        string v5 = backend == Backend.V4V5 ? (parserOnly ? "parser only" : "no") : "n/a";
        string risk =
            highRisk ? "High"
            : covered ? "Low"
            : "High";
        string reason = highRisk
            ? shape == "nullable value struct"
                ? "#255 regression-sensitive: nullable value ancestors need a presence test, .Value on write, and a definition-level gate on read."
                : "The recursive shape is visible to the parser but lacks shipping emitter/runtime evidence."
            : covered
                ? "The generated golden and runtime test cover this compound shape."
                : "The backend or pipeline dial intentionally keeps this shape outside shipping coverage.";
        IReadOnlyList<string> evidence =
            backend == Backend.V4V5
                ? [LegacyCompoundParser, LegacyEmitter]
                : shape switch
                {
                    "nested reference struct"
                    or "nested value struct"
                    or "nullable value struct"
                    or "struct in struct" => [NestedStruct, Golden],
                    "root list of leaf values" or "root list of reference structs" =>
                    [
                        NestedList,
                        Golden,
                    ],
                    _ => [CompoundParser, LegacyCompoundParser],
                };

        return new CoverageRow(
            kind,
            shape,
            "compound",
            nullability,
            backendName,
            acceptance,
            emitted,
            compiled,
            roundTripped,
            v5,
            risk,
            reason,
            evidence
        );
    }
}
