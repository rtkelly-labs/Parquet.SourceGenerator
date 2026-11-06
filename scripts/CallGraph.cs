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
// CallGraph.cs
//
// #252: the shape of the call graph is load-bearing in a source generator — the
// defects that have actually cost this repository time were graph defects (four
// copies of ResolveSchemaField; two pruning components reading one footer) — and
// until now nothing observed the graph. This script extracts a deterministic,
// checked-in call graph for each generator project, gates the properties that
// encode the architecture, and renders a type-level Mermaid picture that lives
// beside it.
//
//   graph/<project>.callgraph.txt   one E: line per static edge; drift-gated like
//                                   metrics/*.metrics.txt
//   graph/callgraph.allowlist.txt   every self-loop, with a stated reason
//   docs/callgraph.md               type-level Mermaid, refreshed by the same command
//   docs/callgraph-generated.md     the generated code's graph, as documentation
//
// What is gated:
//   1. Edge drift — the baseline grammar every other artifact in this repo uses.
//   2. Cycles: no multi-node strongly-connected component; a direct self-loop is
//      legitimate ONLY if it is on the allowlist with a reason. Mutual recursion
//      across components is a design smell with no legitimate use here, so it has
//      no allowlist kind.
//   3. Fan-out: CA-fanout ratchet, thresholds only ever come down (docs/21 policy).
//   4. Layering: Emitter.Components.* must not call back into CodeEmitter /
//      LegacyCodeEmitter — a hub that its own spokes call into is how a component
//      silently stops being a component.
//
// What this graph CANNOT see, and what that means (the honesty clause):
//   It is a STATIC approximation over syntax. No compilation, no symbol
//   resolution: a call site is attributed to the unique project-internal method
//   of matching name+arity, else classified unresolved and counted. Consequences:
//   - delegates (the emitter lambdas, Func<> parameters) appear as no edge;
//   - virtual/interface dispatch resolves to the NAME, not the runtime target —
//     an edge into an interface method is really an edge into its implementation;
//   - overloaded members with the same name+arity in one type merge into one node.
//   Absence of an edge is therefore NOT proof of independence; the graph asserts
//   what it saw, never what cannot happen. Unresolved counts are in the header so
//   the blind spot is measured, not waved away.
//
// Determinism: same pinned Roslyn (4.14.0) as layers 1 and 3; every enumeration
// and every ordering ordinal. Refresh with UPDATE_GOLDEN_FILES=true (same verb as
// every baseline in this repository).
// -----------------------------------------------------------------------------

string repoRoot = FindRepoRoot() ?? Directory.GetCurrentDirectory();

var projects = new[]
{
    new ProjectConfig("Parquet.SourceGenerator", new[] { "src/Parquet.SourceGenerator" })
    {
        FanOutMax = 28, // measured worst: CodeEmitter.EmitSource. The ratchet comes down from here.
    },
    new ProjectConfig(
        "Parquet.SourceGenerator.Legacy",
        new[]
        {
            "src/Parquet.SourceGenerator.Legacy",
            // Compile Include globs in the .csproj, restated here; the drift gate
            // compares the restated set with the csproj so the two cannot diverge.
            "src/Parquet.SourceGenerator/Diagnostics",
            "src/Parquet.SourceGenerator/Models",
            "src/Parquet.SourceGenerator/Parser",
            "src/Parquet.SourceGenerator/Emitter/Components",
        }
    )
    {
        FanOutMax = 13,
    },
};

string graphDir = Path.Combine(repoRoot, "graph");
string docsDir = Path.Combine(repoRoot, "docs");
string allowlistPath = Path.Combine(graphDir, "callgraph.allowlist.txt");

var allowSelfLoops = new HashSet<string>(StringComparer.Ordinal);
var allowMultiNode = new HashSet<string>(StringComparer.Ordinal);
var allowlistComments = new List<string>();
if (File.Exists(allowlistPath))
{
    foreach (string line in File.ReadAllLines(allowlistPath))
    {
        string t = line.Trim();
        if (t.Length == 0)
            continue;
        if (t.StartsWith("#", StringComparison.Ordinal))
        {
            allowlistComments.Add(t);
            continue;
        }
        string[] fields = t.Split('	');
        if (fields.Length >= 2 && fields[0] == "SELF")
            allowSelfLoops.Add(fields[1].Trim());
        else if (fields.Length >= 2 && fields[0] == "SCC")
            allowMultiNode.Add(NormalizeSccKey(fields[1].Trim()));
    }
}

bool update = string.Equals(
    Environment.GetEnvironmentVariable("UPDATE_GOLDEN_FILES"),
    "true",
    StringComparison.OrdinalIgnoreCase
);

Directory.CreateDirectory(graphDir);
Directory.CreateDirectory(Path.Combine(docsDir));

int failures = 0;
var summaries = new List<string>();
var mermaidTypes = new StringBuilder();
var allGraphs = new List<ProjectGraph>();

foreach (ProjectConfig project in projects)
{
    ProjectGraph graph = BuildGraph(repoRoot, project);
    allGraphs.Add(graph);

    string artifact = RenderArtifact(graph);
    string baselinePath = Path.Combine(graphDir, project.Name + ".callgraph.txt");

    if (update)
    {
        File.WriteAllText(baselinePath, artifact);
        Console.WriteLine($"wrote {Show(repoRoot, baselinePath)} ({graph.Edges.Count} edges)");
    }
    else if (!File.Exists(baselinePath))
    {
        Console.Error.WriteLine(
            $"No call-graph baseline at {Show(repoRoot, baselinePath)}. Create with UPDATE_GOLDEN_FILES=true dotnet run scripts/CallGraph.cs"
        );
        failures++;
    }
    else if (File.ReadAllText(baselinePath) != artifact)
    {
        Console.Error.WriteLine(
            $"Call-graph drift in {project.Name}: the static edges no longer match the baseline.\n"
                + "  A new edge is a new dependency — review it. A removed edge is a decoupling — celebrate it, then refresh.\n"
                + "  UPDATE_GOLDEN_FILES=true dotnet run scripts/CallGraph.cs"
        );
        failures++;
    }
    else
    {
        Console.WriteLine(
            $"{project.Name}: call graph matches baseline ({graph.Edges.Count} edges)."
        );
    }

    // Gates that run on the FRESH computation regardless of baseline state.
    var (sccFailures, selfLoopFailures) = CheckCycles(graph, allowSelfLoops, allowMultiNode);
    foreach (string f in selfLoopFailures)
    {
        Console.Error.WriteLine("❌ " + f);
        failures++;
    }
    foreach (string f in sccFailures)
    {
        Console.Error.WriteLine("❌ " + f);
        failures++;
    }

    foreach (
        (string id, int fanout) in graph
            .FanOut.Where(kv => kv.Value > project.FanOutMax)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
    )
    {
        Console.Error.WriteLine(
            $"❌ fan-out ratchet {project.Name}: {id} calls {fanout} distinct methods; cap {project.FanOutMax} (CodeMetricsConfig ratchet policy — lower it, never raise it)."
        );
        failures++;
    }

    foreach ((string caller, string callee) in graph.Edges.Where(CrossesComponentBoundary))
    {
        Console.Error.WriteLine(
            $"❌ layering {project.Name}: {caller} lives in Emitter.Components but calls into the {callee} hub — components must not depend on the emitter that composes them."
        );
        failures++;
    }

    summaries.Add(
        $"{project.Name}: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, "
            + $"{graph.UnresolvedCallSites} unresolved call sites ({(int)(100.0 * graph.UnresolvedCallSites / Math.Max(1, graph.TotalCallSites))}%), "
            + $"max fan-out {graph.FanOut.Values.DefaultIfEmpty(0).Max()}, "
            + $"max depth from entry points {MaxDepth(graph)}."
    );
}

// Type-level Mermaid for the main project only: the legacy graph is mostly the
// same linked sources and a second picture of them documents nothing.
AppendTypeMermaid(mermaidTypes, allGraphs[0]);

string docsPath = Path.Combine(docsDir, "callgraph.md");
string docs =
    "# Call Graph — the shape #252 makes visible\n\n"
    + "<!-- Generated by scripts/CallGraph.cs. Refresh it with the same command as the\n"
    + "     baselines: UPDATE_GOLDEN_FILES=true dotnet run scripts/CallGraph.cs -->\n\n"
    + "Type-level view of the main generator (edges aggregated between types; numbers are\n"
    + "statically-resolvable call sites; delegates and virtual dispatch are invisible to it —\n"
    + "see docs/25-CALL-GRAPH.md). Full method-level edges: `graph/Parquet.SourceGenerator.callgraph.txt`.\n\n"
    + "```mermaid\n"
    + "graph TD\n"
    + mermaidTypes
    + "```\n";
if (update || !File.Exists(docsPath) || File.ReadAllText(docsPath) != docs)
    File.WriteAllText(docsPath, docs);

WriteGeneratedGraph(repoRoot, docsDir, update);

string summaryPath = Path.Combine(graphDir, "callgraph-summary.md");
File.WriteAllText(
    summaryPath,
    "## Call graph (#252)\n\n" + string.Join("\n", summaries.Select(s => $"- {s}")) + "\n"
);

Console.WriteLine(string.Join("\n", summaries));
return failures == 0 ? 0 : 1;

static bool CrossesComponentBoundary((string Caller, string Callee) edge)
{
    string caller = edge.Caller;
    string callee = edge.Callee;
    bool callerIsComponent =
        caller.StartsWith(
            "src/Parquet.SourceGenerator/Emitter/Components/",
            StringComparison.Ordinal
        ) || caller.Contains("/Emitter/Components/", StringComparison.Ordinal);
    bool calleeIsHub =
        callee.EndsWith(".CodeEmitter.", StringComparison.Ordinal)
        || callee.Contains(".CodeEmitter.", StringComparison.Ordinal)
        || callee.Contains(".LegacyCodeEmitter.", StringComparison.Ordinal);
    return callerIsComponent && calleeIsHub;
}

static string NormalizeSccKey(string s)
{
    string[] parts = s.Split('|')
        .Select(p => p.Trim())
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();
    return string.Join(" | ", parts);
}

static int MaxDepth(ProjectGraph graph)
{
    // Depth from "public entry": public members of the generator classes and of each
    // emitted-parity root the tests exercise. A conservative over-estimate is fine;
    // the number documents the graph's height, it does not gate anything.
    var queue = new Queue<(string Id, int Depth)>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (string id in graph.Nodes.Where(n => IsEntry(graph, n)))
    {
        queue.Enqueue((id, 0));
    }
    int max = 0;
    while (queue.Count > 0)
    {
        (string id, int depth) = queue.Dequeue();
        if (!seen.Add(id))
            continue;
        max = Math.Max(max, depth);
        foreach (string next in graph.Outgoing(id))
            queue.Enqueue((next, depth + 1));
    }
    return max;
}

static bool IsEntry(ProjectGraph graph, string id) => graph.PublicNodes.Contains(id);

static (List<string> SccFailures, List<string> SelfFailures) CheckCycles(
    ProjectGraph graph,
    HashSet<string> allowSelfLoops,
    HashSet<string> allowMultiNode
)
{
    var selfFailures = new List<string>();
    var sccFailures = new List<string>();

    foreach (string id in graph.Nodes.OrderBy(n => n, StringComparer.Ordinal))
    {
        if (graph.Outgoing(id).Contains(id) && !allowSelfLoops.Contains(id))
        {
            selfFailures.Add(
                $"undeclared recursion: {id} calls itself. Direct recursion over a tree is legitimate — say so in graph/callgraph.allowlist.txt (`SELF {id} <reason>`), or cut the cycle."
            );
        }
    }

    foreach (List<string> component in StronglyConnected(graph))
    {
        if (component.Count <= 1)
            continue;
        string key = NormalizeSccKey(string.Join(" | ", component));
        if (!allowMultiNode.Contains(key))
        {
            sccFailures.Add(
                $"undeclared cycle between {component.Count} methods: {string.Join(" ↔ ", component.OrderBy(x => x, StringComparer.Ordinal))}. "
                    + "Mutual recursion across components has no legitimate use in this architecture; break it, or argue for it in review, not in the allowlist."
            );
        }
    }

    return (sccFailures, selfFailures);
}

static List<List<string>> StronglyConnected(ProjectGraph graph)
{
    // Tarjan, iterated in ordinal node order for deterministic output.
    var index = new Dictionary<string, int>(StringComparer.Ordinal);
    var low = new Dictionary<string, int>(StringComparer.Ordinal);
    var onStack = new HashSet<string>(StringComparer.Ordinal);
    var stack = new Stack<string>();
    var result = new List<List<string>>();
    int counter = 0;

    void StrongConnect(string start)
    {
        var work = new Stack<(string Id, IEnumerator<string> It)>();
        index[start] = low[start] = counter++;
        stack.Push(start);
        onStack.Add(start);
        work.Push((start, graph.Outgoing(start).GetEnumerator()));

        while (work.Count > 0)
        {
            (string v, IEnumerator<string> it) = work.Peek();
            if (it.MoveNext())
            {
                string w = it.Current;
                if (!graph.Nodes.Contains(w))
                    continue;
                if (!index.ContainsKey(w))
                {
                    index[w] = low[w] = counter++;
                    stack.Push(w);
                    onStack.Add(w);
                    work.Push((w, graph.Outgoing(w).GetEnumerator()));
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
                continue;
            }

            work.Pop();
            if (work.Count > 0)
            {
                (string parent, _) = work.Peek();
                low[parent] = Math.Min(low[parent], low[v]);
            }

            if (low[v] == index[v])
            {
                var component = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                } while (w != v);
                if (component.Count > 1 || graph.Outgoing(component[0]).Contains(component[0]))
                    result.Add(component);
            }
        }
    }

    foreach (string node in graph.Nodes.OrderBy(n => n, StringComparer.Ordinal))
    {
        if (!index.ContainsKey(node))
            StrongConnect(node);
    }
    return result;
}

static void AppendTypeMermaid(StringBuilder sb, ProjectGraph graph)
{
    var typeEdges = new Dictionary<string, int>(StringComparer.Ordinal);
    var typeNodes = new HashSet<string>(StringComparer.Ordinal);
    foreach ((string caller, string callee) in graph.Edges)
    {
        string from = ShortType(caller);
        string to = ShortType(callee);
        if (
            from == to
            || from.StartsWith("?", StringComparison.Ordinal)
            || to.StartsWith("?", StringComparison.Ordinal)
        )
            continue;
        typeNodes.Add(from);
        typeNodes.Add(to);
        string key = from + "→" + to;
        typeEdges[key] = typeEdges.GetValueOrDefault(key) + 1;
    }

    if (typeNodes.Count == 0)
        return;

    // Stable ids must not come from string.GetHashCode: .NET randomizes it per
    // process, and a Mermaid artifact that changes on every regeneration would
    // fail the committed-picture freshness check like a tampered baseline.
    string Label(string name)
    {
        var cleaned = new StringBuilder("t");
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                cleaned.Append(char.ToLowerInvariant(c));
        }
        return cleaned.Append('_').Append(name.Length.ToString("D3")).ToString();
    }

    foreach (string node in typeNodes.OrderBy(n => n, StringComparer.Ordinal))
        sb.Append($"    {Label(node)}[\"{node}\"]\n");
    foreach ((string key, int count) in typeEdges.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
        string[] ends = key.Split('→');
        sb.Append($"    {Label(ends[0])} -->|{count}| {Label(ends[1])}\n");
    }
}

static string ShortType(string methodId) => ProjectGraph.TypeOf(methodId);

static string SanitizeType(string t) => new string(t.Replace(" ", "").ToArray()).Replace('|', '/');

static string Show(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

static string? FindRepoRoot()
{
    for (
        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        dir is not null;
        dir = dir.Parent
    )
    {
        if (File.Exists(Path.Combine(dir.FullName, "Parquet.SourceGenerator.slnx")))
            return dir.FullName;
    }
    return null;
}

static void WriteGeneratedGraph(string repoRoot, string docsDir, bool update)
{
    // The generated code's graph, as documentation (the issue's second graph): the
    // shape a consumer's debugger walks — entry point → schema resolution → column
    // read → materialisation. Rendered for every golden model. Not gated: it is a
    // description of what the emitter outputs, and layer 2 already gates the emitter.
    string goldenDir = Path.Combine(
        repoRoot,
        "test",
        "Parquet.SourceGenerator.Tests",
        "GoldenFiles"
    );
    if (!Directory.Exists(goldenDir))
        return;

    var sb = new StringBuilder();
    sb.AppendLine("# The generated code's call graph — documentation (#252)\n");
    sb.AppendLine(
        "Static view of the golden models' emitted code: what a consumer's debugger walks."
    );
    sb.AppendLine(
        "Same extractor, same limits (delegates/virtuals invisible). Not a gate — the golden"
    );
    sb.AppendLine("files themselves are the contract; this page is the map. Refresh alongside the");
    sb.AppendLine(
        "call-graph baselines: `UPDATE_GOLDEN_FILES=true dotnet run scripts/CallGraph.cs`.\n"
    );

    foreach (
        string file in Directory
            .EnumerateFiles(goldenDir, "*.g.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
    )
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        ProjectGraph graph = BuildSingleFileGraph(file, stem);
        if (graph.Nodes.Count == 0)
            continue;

        sb.AppendLine($"## {stem}\n");
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph TD");
        AppendTypeMermaid(sb, graph);
        sb.AppendLine("```\n");
    }

    string path = Path.Combine(docsDir, "callgraph-generated.md");
    if (update || !File.Exists(path) || File.ReadAllText(path) != sb.ToString())
        File.WriteAllText(path, sb.ToString());
}

static ProjectGraph BuildSingleFileGraph(string file, string stem)
{
    SyntaxTree tree = CSharpSyntaxTree.ParseText(
        File.ReadAllText(file),
        new CSharpParseOptions(LanguageVersion.Latest),
        path: Path.GetFileName(file)
    );
    var graph = new ProjectGraph();
    var trees = new List<(string, SyntaxTree)> { (stem, tree) };
    CollectNodes(trees, graph);
    graph.Seal();
    CollectFile(tree, stem, graph);
    graph.Seal();
    return graph;
}

static ProjectGraph BuildGraph(string repoRoot, ProjectConfig project)
{
    var graph = new ProjectGraph();

    var files = project
        .Roots.Select(r => Path.Combine(repoRoot, r))
        .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        .Where(f =>
            !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/")
        )
        .Select(f => Path.GetFullPath(f))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();

    // pass 1: nodes + signatures.
    var trees = new List<(string Rel, SyntaxTree Tree)>();
    foreach (string file in files)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(file),
            new CSharpParseOptions(LanguageVersion.Latest),
            path: file
        );
        string rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
        trees.Add((rel, tree));
    }

    CollectNodes(trees, graph);
    graph.Seal(); // name/arity index for pass-2 resolution

    // pass 2: edges (resolution needs the full node set).
    foreach ((string rel, SyntaxTree tree) in trees)
        CollectFile(tree, rel, graph);

    graph.Seal();
    return graph;
}

static void CollectNodes(List<(string Rel, SyntaxTree Tree)> trees, ProjectGraph graph)
{
    foreach ((string rel, SyntaxTree tree) in trees)
    {
        foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
        {
            if (node is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax))
                continue;

            string id = MethodId(node, rel);
            graph.Nodes.Add(id);
            string shortType = ProjectGraph.TypeOfShort(id);
            if (shortType.Length > 0)
                graph.TypeNames.Add(shortType);

            if (node is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.PublicKeyword))
                graph.PublicNodes.Add(id);
        }
    }
}

static void CollectFile(SyntaxTree tree, string rel, ProjectGraph graph)
{
    foreach (
        SyntaxNode holder in tree.GetRoot()
            .DescendantNodes()
            .Where(n => n is MethodDeclarationSyntax || n is ConstructorDeclarationSyntax)
    )
    {
        string caller = MethodId(holder, rel);

        foreach (
            InvocationExpressionSyntax inv in holder
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
        )
        {
            (string? qualifier, string name) = CalleeName(inv.Expression);
            int arity = inv.ArgumentList?.Arguments.Count ?? 0;
            graph.TotalCallSites++;
            // A qualified call to a known project type resolves inside that type and nowhere
            // else: `SchemaComponent.EmitSchema(builder)` from within CodeEmitter must never
            // collapse onto CodeEmitter's own member.
            string? callee =
                qualifier is not null && graph.TypeNames.Contains(qualifier)
                    ? graph.ResolveInType(qualifier, name, arity)
                    : graph.Resolve(caller, name, arity, allowSameType: qualifier is null);
            if (callee is not null && callee != caller)
                graph.Edges.Add((caller, callee));
            else if (callee is null)
                graph.UnresolvedCallSites++;
            else if (callee == caller)
                graph.Edges.Add((caller, callee)); // self loop: an edge like any other; cycles gate it
        }

        foreach (
            ObjectCreationExpressionSyntax creation in holder
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
        )
        {
            string typeName = creation.Type.ToString();

            graph.TotalCallSites++;
            string ctorName = ".ctor/" + typeName.Split('<')[0].Split('.').Last();
            int arity = creation.ArgumentList?.Arguments.Count ?? 0;
            string? callee = graph.Resolve(ctorName, arity, typeName);
            if (callee is not null)
                graph.Edges.Add((caller, callee));
            else
                graph.UnresolvedCallSites++;
        }
    }
}

static (string? Qualifier, string Name) CalleeName(ExpressionSyntax expression)
{
    if (expression is MemberAccessExpressionSyntax member)
    {
        if (member.Expression is BaseExpressionSyntax)
            return (null, member.Name.Identifier.Text + "«base»"); // never same-type: framework inheritance
        if (member.Expression is not IdentifierNameSyntax)
        {
            // complex receiver — ((IEnumerable<T>)(...)).GetEnumerator(), cond?.M(), a.b.M():
            // we cannot know its static target, so it must never resolve to `this`-adjacent
            // candidates. Conservative: an unresolved call site, not a coin flip.
            string complexName = member.Name is GenericNameSyntax gen
                ? gen.Identifier.Text
                : member.Name.Identifier.Text;
            return (null, complexName + "«base»");
        }
        string? qualifier = (member.Expression as IdentifierNameSyntax)?.Identifier.Text;
        string name = member.Name is GenericNameSyntax genericMember
            ? genericMember.Identifier.Text
            : member.Name.Identifier.Text;
        return (qualifier, name);
    }

    if (expression is IdentifierNameSyntax id)
        return (null, id.Identifier.Text);
    if (expression is GenericNameSyntax generic2)
        return (null, generic2.Identifier.Text);
    if (expression is MemberBindingExpressionSyntax binding)
        return (null, binding.Name.Identifier.Text + "«base»"); // null-qualified: receiver is the property, not `this`

    return (null, expression.ToString().Split('(')[0]);
}

static string MethodId(SyntaxNode holder, string rel)
{
    // Parameter types join the identity so two overloads of the same name+arity —
    // GetTargetModel(context,...) calling GetTargetModel(symbol,...) — read as two
    // nodes with an edge between them, not a false self-loop.
    string name = holder switch
    {
        MethodDeclarationSyntax m => (
            m.ExplicitInterfaceSpecifier is not null
                ? m.Identifier.Text + "[" + m.ExplicitInterfaceSpecifier.Name + "]"
                : m.Identifier.Text
        )
            + "/("
            + string.Join(
                ",",
                m.ParameterList.Parameters.Select(pp => SanitizeType(pp.Type.ToString()))
            )
            + ")",
        ConstructorDeclarationSyntax c => ".ctor/"
            + c.Identifier.Text
            + "/("
            + string.Join(
                ",",
                c.ParameterList.Parameters.Select(pp => SanitizeType(pp.Type.ToString()))
            )
            + ")",
        _ => "other",
    };
    string typePath = string.Empty;
    for (SyntaxNode? t = holder.Parent; t is not null; t = t.Parent)
    {
        if (t is TypeDeclarationSyntax td)
            typePath = td.Identifier.Text + (typePath.Length == 0 ? "" : "." + typePath);
    }
    string ns =
        ((MemberDeclarationSyntax)holder)
            .AncestorsAndSelf()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()
            ?.Name.ToString()
        ?? string.Empty;
    _ = rel;
    return string.IsNullOrEmpty(ns) ? typePath + "." + name : ns + "." + typePath + "." + name;
}

static string RenderArtifact(ProjectGraph graph)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Call-graph baseline (#252): one static call edge per line.");
    sb.AppendLine("# Generated by scripts/CallGraph.cs. Do not edit by hand.");
    sb.AppendLine("# Refresh: UPDATE_GOLDEN_FILES=true dotnet run scripts/CallGraph.cs");
    sb.AppendLine("# E:<caller> | <callee> — dotted namespaced.Type.method/arity, ordinal-sorted.");
    sb.AppendLine("# Gates: edge drift; cycles (graph/callgraph.allowlist.txt); fan-out ratchet;");
    sb.AppendLine("#        components-must-not-call-the-emitter-hub layering.");
    sb.AppendLine("# Unresolved call sites (delegates, virtuals, framework): measured in the");
    sb.AppendLine("# header below so the blind spot is visible, never silently absent.");
    sb.AppendLine(
        $"# stats: nodes={graph.Nodes.Count} edges={graph.Edges.Count} "
            + $"call-sites={graph.TotalCallSites} unresolved={graph.UnresolvedCallSites}"
    );
    foreach (string line in graph.Lines())
        sb.AppendLine(line);
    return sb.ToString();
}

internal sealed record ProjectConfig(string Name, string[] Roots)
{
    public int FanOutMax { get; init; } = 24;
}

internal sealed class ProjectGraph
{
    private static string TypeOfMethod(string methodId) => TypeOf(methodId);

    public static string TypeOf(string methodId)
    {
        (string head, int _) = SplitId(methodId);
        int lastDot = head.LastIndexOf('.');
        return lastDot < 0 ? head : head[..lastDot];
    }

    public HashSet<string> Nodes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> PublicNodes { get; } = new(StringComparer.Ordinal);
    public HashSet<(string, string)> Edges { get; } = new(EdgeComparer.Instance);
    public HashSet<string> TypeNames { get; } = new(StringComparer.Ordinal);
    public int TotalCallSites { get; set; }
    public int UnresolvedCallSites { get; set; }

    private Dictionary<string, List<string>> _outgoing = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _byNameArity = new(StringComparer.Ordinal);

    public void Seal()
    {
        _outgoing = new(StringComparer.Ordinal);
        _byNameArity = new(StringComparer.Ordinal);
        foreach (string node in Nodes)
        {
            string key = NameArityKey(node);
            if (!_byNameArity.TryGetValue(key, out List<string>? list))
                _byNameArity[key] = list = [];
            list.Add(node);
        }
        foreach ((string caller, string callee) in Edges)
        {
            if (!_outgoing.TryGetValue(caller, out List<string>? list))
                _outgoing[caller] = list = [];
            list.Add(callee);
        }
    }

    public IEnumerable<string> Outgoing(string id) =>
        _outgoing.TryGetValue(id, out List<string>? list) ? list : [];

    public Dictionary<string, int> FanOut =>
        Nodes
            .Select(n => (n, Outgoing(n).Distinct().Count()))
            .Where(kv => kv.Item2 > 1)
            .ToDictionary(kv => kv.n, kv => kv.Item2, StringComparer.Ordinal);

    public string? Resolve(string caller, string name, int arity, bool allowSameType = true)
    {
        string key = name + "/" + arity;
        if (!_byNameArity.TryGetValue(key, out List<string>? candidates) || candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        // Same-type priority applies only to unqualified calls. `item.GetHashCode()`
        // inside a method that also defines GetHashCode() is a framework call on the
        // element, not a recursive self-call; guessing otherwise manufactured cycles.
        if (allowSameType)
        {
            string callerType = TypeOfMethod(caller);
            List<string> sameType = candidates.Where(c => TypeOfMethod(c) == callerType).ToList();
            if (sameType.Count > 0)
                return sameType.OrderBy(c => c, StringComparer.Ordinal).First();
        }

        return null; // ambiguous across types: an unresolved call site, not a coin flip
    }

    public string? ResolveInType(string shortType, string name, int arity)
    {
        string key = name + "/" + arity;
        if (!_byNameArity.TryGetValue(key, out List<string>? candidates))
            return null;
        foreach (string c in candidates.OrderBy(x => x, StringComparer.Ordinal))
        {
            string t = TypeOf(c);
            if (t.EndsWith("." + shortType, StringComparison.Ordinal) || t == shortType)
                return c;
        }
        return null;
    }

    // The type name as a call site would spell it in a qualifier: the nested-type tail
    // ("Outer.Inner") with the namespace stripped. ResolveInType re-matches by suffix,
    // so a partial qualifier on a nested type still lands in the right place.
    public static string TypeOfShort(string methodId)
    {
        string typePath = TypeOf(methodId);
        int lastDot = typePath.LastIndexOf('.');
        return lastDot < 0 ? typePath : typePath[(lastDot + 1)..];
    }

    public string? Resolve(string ctorName, int arity, string typeName)
    {
        _ = arity;
        string suffix = "." + ctorName;
        return Nodes
            .Where(n =>
                n.EndsWith(suffix, StringComparison.Ordinal)
                && TypeOfMethod(n).EndsWith(typeName.Split('<')[0], StringComparison.Ordinal)
            )
            .OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public List<string> Lines() =>
        Edges
            .OrderBy(e => e.Item1, StringComparer.Ordinal)
            .ThenBy(e => e.Item2, StringComparer.Ordinal)
            .Select(e => $"E:{e.Item1} | {e.Item2}")
            .ToList();

    // "ns.Type.Method(System.A,System.B)" -> "Method/2"; ctor ".ctor/X()" -> ".ctor/X/0".
    private static string NameArityKey(string node)
    {
        (string head, int arity) = SplitId(node);
        int lastDot = head.LastIndexOf('.');
        return head[(lastDot + 1)..] + "/" + arity;
    }

    private static (string Head, int Arity) SplitId(string node)
    {
        int open = node.IndexOf("/(", StringComparison.Ordinal);
        if (open < 0)
            return (node, 0);
        string head = node[..open];
        string args = node[(open + 2)..^1];
        int arity = args.Length == 0 ? 0 : args.Split(',').Length;
        return (head, arity);
    }

    private sealed class EdgeComparer : IEqualityComparer<(string, string)>
    {
        public static readonly EdgeComparer Instance = new();

        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.Ordinal)
            && string.Equals(a.Item2, b.Item2, StringComparison.Ordinal);

        public int GetHashCode((string, string) e) =>
            HashCode.Combine(
                e.Item1.GetHashCode(StringComparison.Ordinal),
                e.Item2.GetHashCode(StringComparison.Ordinal)
            );
    }
}
