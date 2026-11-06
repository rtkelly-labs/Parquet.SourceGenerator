#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// -----------------------------------------------------------------------------
// Duplication.cs
//
// Layer 3 of #251. Measures token-level duplication across the hand-written sources under
// src/, checks it in as a deterministic ordinal artifact (metrics/duplication.txt), and gates
// CI on drift — the `*.api.txt` / `metrics/*.metrics.txt` pattern layers 1 and 2 established.
// See docs/23-DUPLICATION.md for the design and its known limits.
//
// What it computes: for every method body, a stream of normalized token units (identifiers
// collapse to a placeholder, keywords and operators keep their syntax kind, literals keep
// their text). A sliding window of `WindowTokens` units is hashed; a hash shared by two or
// more distinct methods is a duplicated span. Runs are merged maximally, and clusters
// shorter than `MinSpanTokens` are discarded — the knob that decides whether the detector
// sees structure or punctuation.
//
// What it deliberately does NOT measure: emitted (generated) code. Generated output is
// supposed to repeat itself — one ResolveSchemaField per model is the design, not a defect —
// so golden files, generated .cs, and anything outside src/ are out of scope entirely.
// Gating on emitted duplication would produce a permanent false positive, and "reported but
// never gated" was rejected too: there is no decision the number could inform.
//
// Calibration (docs/23-DUPLICATION.md): the settings below were chosen against the repo's
// demonstrated failure — the hand-rolled copies of ResolveSchemaField that all broke when
// #196 added a parameter. Run `--root <checkout-of-8d4a097>/src --report -` and the detector
// names the cross-file pair. A configuration that cannot find the copies that already broke
// the build three times is not fit for this repo.
//
// Determinism: the Roslyn version that tokenizes is pinned above (same pin as CodeMetrics.cs),
// file enumeration and every ordering is StringComparer.Ordinal, and the artifact carries no
// line numbers — position changes that leave duplication intact must not trip the gate.
// -----------------------------------------------------------------------------

const int WindowTokens = 16;
const int MinSpanTokens = 40;

string srcRoot = Path.Combine("src");
string baselinePath = Path.Combine("metrics", "duplication.txt");
string? reportPath = null;
string? summaryPath = null;
bool noBaseline = false;

var argv = Environment.GetCommandLineArgs();
for (int i = 1; i < argv.Length; i++)
{
    switch (argv[i])
    {
        case "--root":
            srcRoot = Require(argv, ref i, "--root");
            noBaseline = true; // calibrating a foreign tree has no baseline to drift against
            break;
        case "--report":
            reportPath = Require(argv, ref i, "--report");
            break;
        case "--summary":
            summaryPath = Require(argv, ref i, "--summary");
            break;
        case "--check":
            noBaseline = false;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {argv[i]}");
            return 2;
    }
}

if (!Directory.Exists(srcRoot))
{
    Console.Error.WriteLine($"Source root not found: {srcRoot} (run from the repository root)");
    return 2;
}

var files = Directory
    .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
    .Where(f => !InIgnoredDir(f))
    .Select(f => Path.GetFullPath(f))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

// id -> the ordered token-unit stream of that method, and the runs found by grouping.
var streams = new List<(string Id, string Path, ImmutableArray<string> Units)>();

foreach (string file in files)
{
    SyntaxTree tree = CSharpSyntaxTree.ParseText(
        File.ReadAllText(file),
        new CSharpParseOptions(LanguageVersion.Latest),
        path: file
    );

    string rel = ToRelative(file);

    foreach (CSharpSyntaxNode holder in CollectHolders(tree.GetRoot()))
    {
        ImmutableArray<string> units = TokenUnits(holder);
        if (units.Length < WindowTokens)
            continue;

        streams.Add((MethodId(holder, rel), rel, units));
    }
}

// Group windows by hash: hash -> list of (stream index, window start).
var groups = new Dictionary<string, List<(int Stream, int Start)>>(StringComparer.Ordinal);
for (int s = 0; s < streams.Count; s++)
{
    ImmutableArray<string> units = streams[s].Units;
    for (int w = 0; w + WindowTokens <= units.Length; w++)
    {
        string key = Hash(units, w, WindowTokens);
        if (!groups.TryGetValue(key, out List<(int, int)>? hits))
            groups[key] = hits = [];
        hits.Add((s, w));
    }
}

// A duplicated block of L tokens contributes L-W+1 *different* window hashes, each holding
// the same (streamA, streamB, diagonal) alignment. Accumulate window starts per alignment
// across all groups first; then a maximal consecutive run of starts along one diagonal is
// exactly one duplicated span, of length runLength - 1 + WindowTokens tokens.
var alignments = new Dictionary<(int, int, int), List<int>>(capacity: 8192);

foreach (KeyValuePair<string, List<(int Stream, int Start)>> group in groups)
{
    if (group.Value.Select(m => m.Stream).Distinct().Count() < 2)
        continue;

    List<(int Stream, int Start)> members = group
        .Value.OrderBy(m => m.Stream)
        .ThenBy(m => m.Start)
        .ToList();

    for (int i = 0; i < members.Count; i++)
    {
        for (int j = i + 1; j < members.Count; j++)
        {
            (int streamA, int startA) = members[i];
            (int streamB, int startB) = members[j];
            if (streamA == streamB)
                continue;

            var key = (streamA, streamB, startA - startB);
            if (!alignments.TryGetValue(key, out List<int>? starts))
                alignments[key] = starts = [];
            starts.Add(startA);
        }
    }
}

var clusters = new List<Cluster>();
foreach (KeyValuePair<(int, int, int), List<int>> kv in alignments)
{
    (int streamA, int streamB, int _) = kv.Key;
    kv.Value.Sort();
    foreach ((int _, int runLength) in ConsecutiveRuns(kv.Value))
    {
        int spanTokens = runLength - 1 + WindowTokens;
        if (spanTokens < MinSpanTokens)
            continue;

        string idA = streams[streamA].Id;
        string idB = streams[streamB].Id;
        List<string> ids = string.CompareOrdinal(idA, idB) <= 0 ? [idA, idB] : [idB, idA];
        clusters.Add(new Cluster(spanTokens, 2, string.Join(" | ", ids)));
    }
}

// Collapse duplicate views of the same span (adjacent windows of one run produce several
// groups with identical member sets): keep the largest span per member-signature key.
var best = new Dictionary<string, Cluster>(StringComparer.Ordinal);
foreach (Cluster c in clusters)
{
    if (!best.TryGetValue(c.Key, out Cluster? existing) || c.Span > existing.Span)
        best[c.Key] = c;
}

List<Cluster> final = best
    .Values.OrderByDescending(c => c.Span)
    .ThenByDescending(c => c.Copies)
    .ThenBy(c => c.Key, StringComparer.Ordinal)
    .ToList();

int totalTokens = final.Sum(c => c.Span * c.Copies);
Console.WriteLine(
    $"Duplication: {final.Count} cluster(s), {totalTokens} duplicated token(s), "
        + $"worst span {final.FirstOrDefault()?.Span ?? 0} tokens across "
        + $"{final.FirstOrDefault()?.Copies ?? 0} methods."
);

string artifact = RenderArtifact(final, totalTokens);

if (reportPath is not null)
{
    if (reportPath == "-")
        Console.Write(artifact);
    else
        File.WriteAllText(reportPath, artifact);
}

if (noBaseline)
    return 0;

bool update = string.Equals(
    Environment.GetEnvironmentVariable("UPDATE_GOLDEN_FILES"),
    "true",
    StringComparison.OrdinalIgnoreCase
);

if (update)
{
    Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
    File.WriteAllText(baselinePath, artifact);
    Console.WriteLine($"Wrote {baselinePath} ({final.Count} clusters).");
    return 0;
}

if (!File.Exists(baselinePath))
{
    Console.Error.WriteLine(
        $"No duplication baseline at {baselinePath}. "
            + "Create one with UPDATE_GOLDEN_FILES=true dotnet run scripts/Duplication.cs"
    );
    return 1;
}

string current = File.ReadAllText(baselinePath);
if (current == artifact)
{
    Console.WriteLine("Duplication baseline matches.");
    if (summaryPath is not null)
        File.WriteAllText(summaryPath, RenderSummary(final, totalTokens, Array.Empty<string>()));
    return 0;
}

List<string> added = MissingLines(artifact, current);
List<string> removed = MissingLines(current, artifact);

var message = new StringBuilder();
message.AppendLine("Duplication drift: the checked-in baseline no longer matches src/.");
foreach (string line in added)
    message.AppendLine($"  + new/changed cluster: {line}");
foreach (string line in removed)
    message.AppendLine($"  - gone/shrunk cluster:  {line}");
message.AppendLine(
    "Either the duplication is real (fold it, or record the reason in docs/23-DUPLICATION.md)\n"
        + "or the baseline is stale: UPDATE_GOLDEN_FILES=true dotnet run scripts/Duplication.cs"
);
Console.Error.Write(message);

if (summaryPath is not null)
    File.WriteAllText(summaryPath, RenderSummary(final, totalTokens, added));
return 1;

static bool InIgnoredDir(string path)
{
    string normalized = path.Replace('\\', '/');
    return normalized.Contains("/bin/") || normalized.Contains("/obj/");
}

static string Require(string[] argv, ref int i, string flag)
{
    if (i + 1 >= argv.Length)
    {
        Console.Error.WriteLine($"{flag} requires a value");
        Environment.Exit(2);
    }
    return argv[++i];
}

static string ToRelative(string full)
{
    string baseDir = Path.GetFullPath(".");
    string rel = Path.GetRelativePath(baseDir, full).Replace('\\', '/');
    return rel;
}

// The bodies we compare: methods and their local functions ride along with the method's
// token stream, which is what the duplication actually looks like from the outside.
static IEnumerable<CSharpSyntaxNode> CollectHolders(SyntaxNode root)
{
    foreach (SyntaxNode node in root.DescendantNodes())
    {
        if (node is MethodDeclarationSyntax || node is ConstructorDeclarationSyntax)
            yield return (CSharpSyntaxNode)node;
    }
}

static ImmutableArray<string> TokenUnits(CSharpSyntaxNode holder)
{
    var units = ImmutableArray.CreateBuilder<string>();
    foreach (SyntaxToken token in holder.DescendantTokens())
    {
        if (token.IsKind(SyntaxKind.None))
            continue;

        if (token.IsKeyword())
        {
            units.Add("K" + (int)token.Kind());
        }
        else if (token.Value is not null)
        {
            // Literal text survives: two blocks that only LOOK alike because their control
            // flow matches are not duplicates; two blocks with the same strings and numbers are.
            units.Add("T" + token.Text);
        }
        else if (token.Text.All(char.IsDigit))
        {
            units.Add("T" + token.Text);
        }
        else
        {
            // Identifiers, operators, punctuation: kind only. Renames do not un-duplicate code.
            units.Add("K" + (int)token.Kind());
        }
    }
    return units.ToImmutable();
}

static string Hash(ImmutableArray<string> units, int start, int length)
{
    const ulong Offset = 14695981039346656037;
    const ulong Prime = 1099511628211;
    ulong h = Offset;
    for (int i = start; i < start + length; i++)
    {
        foreach (char c in units[i])
        {
            h ^= c;
            h *= Prime;
        }
        h *= Prime; // separator
    }
    return h.ToString("x16");
}

static IEnumerable<(int Start, int Length)> ConsecutiveRuns(List<int> sortedStarts)
{
    int runStart = sortedStarts[0];
    int runLength = 1;
    for (int i = 1; i < sortedStarts.Count; i++)
    {
        if (sortedStarts[i] == sortedStarts[i - 1] + 1)
        {
            runLength++;
            continue;
        }
        yield return (runStart, runLength);
        runStart = sortedStarts[i];
        runLength = 1;
    }
    yield return (runStart, runLength);
}

static string MethodId(CSharpSyntaxNode holder, string rel)
{
    string name;
    string typePath = string.Empty;
    if (holder is MethodDeclarationSyntax m)
        name = m.Identifier.Text;
    else if (holder is ConstructorDeclarationSyntax c)
        name = $".ctor/{c.Identifier.Text}";
    else
        name = holder.GetType().Name;

    for (SyntaxNode? t = holder.Parent; t is not null; t = t.Parent)
    {
        if (t is TypeDeclarationSyntax td)
            typePath = td.Identifier.Text + (typePath.Length == 0 ? "" : "." + typePath);
    }

    int arity =
        holder is MethodDeclarationSyntax mm && mm.TypeParameterList?.Parameters.Count > 0
            ? mm.TypeParameterList.Parameters.Count
            : 0;

    return $"{rel}:{typePath}.{name}{(arity > 0 ? "`" + arity : "")}";
}

static string RenderArtifact(List<Cluster> clusters, int totalTokens)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Duplication baseline for hand-written code under src/ (layer 3 of #251).");
    sb.AppendLine("# Generated by scripts/Duplication.cs. Do not edit by hand.");
    sb.AppendLine("# Refresh: UPDATE_GOLDEN_FILES=true dotnet run scripts/Duplication.cs");
    sb.AppendLine($"# K: cluster — duplicated token span x copies — ordinal-sorted method ids.");
    sb.AppendLine($"# Method ids are RELATIVE-PATH:Type.Method. No line numbers: a refactor that");
    sb.AppendLine("# moves code without changing overlap must not trip this gate.");
    sb.AppendLine("# Emitted/generated code is out of scope by design (docs/23-DUPLICATION.md).");
    sb.AppendLine($"# totals: clusters={clusters.Count} duplicated-tokens={totalTokens}");
    foreach (Cluster c in clusters)
        sb.AppendLine($"K | span={c.Span} copies={c.Copies} | {c.Key}");
    return sb.ToString();
}

static string RenderSummary(List<Cluster> clusters, int totalTokens, IReadOnlyList<string> added)
{
    var sb = new StringBuilder();
    sb.AppendLine("## Duplication (layer 3 of #251)");
    sb.AppendLine();
    sb.AppendLine(
        $"**{clusters.Count} clusters, {totalTokens} duplicated tokens.** "
            + "Baseline state: "
            + (added.Count == 0 ? "match." : $"drift with {added.Count} new/changed cluster(s).")
    );
    sb.AppendLine();
    int shown = 0;
    foreach (Cluster c in clusters)
    {
        if (++shown > 15)
        {
            sb.AppendLine($"- … {clusters.Count - 14} smaller clusters elided");
            break;
        }
        sb.AppendLine($"- span {c.Span} x {c.Copies} copies: {Truncate(c.Key, 160)}");
    }
    return sb.ToString();
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

static List<string> MissingLines(string candidate, string reference)
{
    var referenceData = new HashSet<string>(StringComparer.Ordinal);
    foreach (string line in reference.Split('\n'))
    {
        if (line.StartsWith("K |", StringComparison.Ordinal))
            referenceData.Add(line.TrimEnd('\r'));
    }

    return candidate
        .Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("K |", StringComparison.Ordinal) && !referenceData.Contains(l))
        .ToList();
}

internal sealed record Cluster(int Span, int Copies, string Key);
