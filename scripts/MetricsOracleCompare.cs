#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

// -----------------------------------------------------------------------------
// MetricsOracleCompare.cs
//
// The comparison half of #254: checks Microsoft's own Metrics.exe output against
// the checked-in baselines produced by scripts/CodeMetrics.cs, at TYPE level, and
// fails when the two disagree. `CodeMetrics.cs` and `Metrics.exe` both derive from
// Roslyn's CodeAnalysisMetricData — so they should agree by construction, and this
// is the only thing in the repository that checks they actually do. If the bespoke
// computation is subtly wrong (a different enumeration of members, a different
// treatment of accessors or partials), every baseline number is wrong in the same
// direction and the drift gate would hold a wrong baseline stable forever. This
// script is the independent oracle that catches that, on the same logic PyArrow,
// DuckDB and parquet-cli apply to generated files (#165, #166, #183).
//
// The gate is type-level for a reason: the two tools legitimately differ in
// *scope* — how generated and partial code is enumerated is not identical — while
// the oracle question is "is our ruler accurate?", and a ruler is accurate iff it
// reads the same on every object both can see. Assembly totals are reported
// (informational) but never gated; docs/24-METRICS-ORACLE.md records the split.
//
// Tolerance: Maintainability Index +/- 2 — exactly the policy layer 1 uses for
// cross-machine comparison, because MI involves a cube root whose last digit is
// not bit-reproducible across runtimes. Every other metric is compared exactly.
//
// A disagreement makes the nightly job fail AND (via metrics-oracle.yml) open or
// update a GitHub issue: a failing schedule gets muted, a reviewable issue gets
// acted on.
//
// Exit codes: 0 agree, 1 disagreement or unreadable inputs, 2 usage.
// -----------------------------------------------------------------------------

string? xmlPath = null;
string? baselinePath = null;
string? summaryPath = null;
string? targetFilter = null;
bool append = false;

var argv = Environment.GetCommandLineArgs();
for (int i = 1; i < argv.Length; i++)
{
    switch (argv[i])
    {
        case "--xml":
            xmlPath = Require(argv, ref i, "--xml");
            break;
        case "--baseline":
            baselinePath = Require(argv, ref i, "--baseline");
            break;
        case "--summary":
            summaryPath = Require(argv, ref i, "--summary");
            break;
        case "--append":
            append = true;
            break;
        case "--target":
            targetFilter = Require(argv, ref i, "--target");
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {argv[i]}");
            return 2;
    }
}

if (xmlPath is null || baselinePath is null)
{
    Console.Error.WriteLine(
        "Usage: MetricsOracleCompare.cs --xml <Metrics.exe output> --baseline <metrics/*.metrics.txt> [--summary <file>] [--append]"
    );
    return 2;
}

if (!File.Exists(xmlPath))
{
    Console.Error.WriteLine($"Metrics XML not found: {xmlPath} (did Metrics.exe fail to run?)");
    return 1;
}

BaselineFile baseline = ReadBaseline(baselinePath);
Dictionary<string, OracleRow> oracle = ReadOracleXml(xmlPath, targetFilter);

string assemblyName = Path.GetFileNameWithoutExtension(baselinePath)
    .Replace(".metrics", string.Empty);

var failures = new List<string>();
var infos = new List<string>();
int compared = 0;

foreach (
    (string type, BaselineRow row) in baseline.Types.OrderBy(kv => kv.Key, StringComparer.Ordinal)
)
{
    if (!oracle.TryGetValue(type, out OracleRow? o))
    {
        failures.Add(
            $"TYPE NOT REPORTED by {assemblyName}: '{type}' has no matching <NamedType> in Metrics.exe output."
        );
        continue;
    }

    compared++;
    if (Math.Abs(row.Mi - o.Mi) > 2)
        failures.Add(
            $"MI {assemblyName}:{type} hand-computed {row.Mi} vs oracle {o.Mi} (tolerance ±2)."
        );
    if (row.Cc != o.Cc)
        failures.Add($"CC {assemblyName}:{type} hand-computed {row.Cc} vs oracle {o.Cc}.");
    if (row.Cl != o.Cl)
        failures.Add($"CL {assemblyName}:{type} hand-computed {row.Cl} vs oracle {o.Cl}.");
    if (row.Sloc != o.Sloc)
        failures.Add($"SLOC {assemblyName}:{type} hand-computed {row.Sloc} vs oracle {o.Sloc}.");
}

if (baseline.Assembly is not null && oracle.TryGetValue(assemblyName, out OracleRow? asm))
{
    infos.Add(
        $"assembly {assemblyName}: hand-computed MI={baseline.Assembly.Mi} CC={baseline.Assembly.Cc} "
            + $"CL={baseline.Assembly.Cl} SLOC={baseline.Assembly.Sloc}; oracle MI={asm.Mi} CC={asm.Cc} "
            + $"CL={asm.Cl} SLOC={asm.Sloc} — reported, not gated (enumeration scope differs; docs/24)."
    );
}
else
{
    infos.Add(
        $"assembly-level line for {assemblyName} not located in oracle output; type-level results stand alone."
    );
}

Console.WriteLine(
    $"Metrics oracle {assemblyName}: {compared}/{baseline.Types.Count} types compared "
        + (failures.Count == 0 ? "— oracle agrees." : $"— {failures.Count} disagreement(s).")
);
foreach (string f in failures)
    Console.Error.WriteLine("❌ " + f);
foreach (string s in infos)
    Console.WriteLine("   " + s);

if (summaryPath is not null)
{
    var sb = new StringBuilder();
    if (!append || !File.Exists(summaryPath))
    {
        sb.AppendLine("## Metrics oracle vs checked-in baselines (#254)");
        sb.AppendLine();
    }
    sb.AppendLine(
        $"**{assemblyName}**: {compared}/{baseline.Types.Count} types compared, {failures.Count} disagreement(s)."
    );
    foreach (string f in failures)
        sb.AppendLine($"- ❌ {f}");
    foreach (string s in infos)
        sb.AppendLine($"- ℹ️ {s}");
    File.AppendAllText(summaryPath, sb.ToString());
}

return failures.Count == 0 ? 0 : 1;

static string Require(string[] argv, ref int i, string flag)
{
    if (i + 1 >= argv.Length)
    {
        Console.Error.WriteLine($"{flag} requires a value");
        Environment.Exit(2);
    }
    return argv[++i];
}

static BaselineFile ReadBaseline(string path)
{
    var result = new BaselineFile();
    foreach (string line in File.ReadAllLines(path))
    {
        if (
            !line.StartsWith("T:", StringComparison.Ordinal)
            && !line.StartsWith("A:", StringComparison.Ordinal)
        )
            continue;

        int bar = line.IndexOf(" | ", StringComparison.Ordinal);
        if (bar < 0)
            continue;

        string key = line[..bar];
        bool isAssembly = key.StartsWith("A:", StringComparison.Ordinal);
        string name = key[2..];

        var fields = ParseFields(line[(bar + 3)..]);
        var row = new BaselineRow(
            Get(fields, "MI"),
            Get(fields, "CC"),
            Get(fields, "CL"),
            Get(fields, "SLOC")
        );

        if (isAssembly)
            result.Assembly = row;
        else
            result.Types[Normalize(name)] = row;
    }
    return result;
}

static Dictionary<string, string> ParseFields(string tail)
{
    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (string part in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        int eq = part.IndexOf('=');
        if (eq > 0)
            fields[part[..eq]] = part[(eq + 1)..];
    }
    return fields;
}

static int Get(Dictionary<string, string> fields, string key) =>
    fields.TryGetValue(key, out string? v)
    && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
        ? n
        : -1;

static string Normalize(string full) => full.Trim().Replace('+', '.');

// One Metrics.exe invocation over several projects writes ONE report with one <Target>
// element per project. The two generator projects link the same shared parser sources, so
// their type sets collide by name; the reader therefore scopes to a single <Target> whose
// Name attribute contains --target, and a wrong match there would defeat the oracle.

// Schema, per Metrics.exe source (dotnet/roslyn src/RoslynAnalyzers/Tools/Metrics,
// MetricsOutputWriter.cs): <CodeMetricsReport><Targets><Target Name="proj.csproj">
// <Assembly Name="..."><Metrics><Metric Name="..." Value="..."/> ... <Namespaces>
// <Namespace Name="ns"><Types><NamedType Name="Outer.Inner"><Metrics> ...
// Element names are symbol KINDS ("NamedType", "Namespace", "Assembly"); a type's
// Name attribute carries at most the containing-type qualification, so the full
// dotted key is ancestor Namespace + '.' + Name. Metric names: MaintainabilityIndex,
// CyclomaticComplexity, ClassCoupling, SourceLines, ExecutableLines.
static Dictionary<string, OracleRow> ReadOracleXml(string path, string? targetFilter)
{
    var rows = new Dictionary<string, OracleRow>(StringComparer.Ordinal);
    XDocument doc = XDocument.Load(path);

    List<XElement> targets = string.IsNullOrEmpty(targetFilter)
        ? doc.Descendants("Target").ToList()
        : doc.Descendants("Target")
            .Where(t =>
                (t.Attribute("Name")?.Value ?? string.Empty).Contains(
                    targetFilter,
                    StringComparison.Ordinal
                )
            )
            .ToList();

    if (targets.Count == 0)
    {
        Console.Error.WriteLine(
            $"No <Target> matching '{targetFilter}' in {path}; refusing to compare against an empty oracle."
        );
        Environment.Exit(1);
    }

    foreach (XElement type in targets.SelectMany(t => t.Descendants("NamedType")))
    {
        string? name = type.Attribute("Name")?.Value;
        XElement? metrics = type.Element("Metrics");
        if (name is null || metrics is null)
            continue;

        string nsName =
            type.Ancestors("Namespace").FirstOrDefault()?.Attribute("Name")?.Value ?? string.Empty;
        string full = string.IsNullOrEmpty(nsName) ? name : nsName + "." + name;

        var fields = ReadMetricFields(metrics);
        var row = new OracleRow(
            Num(fields, "MaintainabilityIndex"),
            Num(fields, "CyclomaticComplexity"),
            Num(fields, "ClassCoupling"),
            Num(fields, "SourceLines")
        );

        // Index the full dotted name, and also the namespace-less spelling for types
        // emitted without a parent <Namespace>. Never a bare name: "TargetParser"
        // could silently stand in for "TargetParser" in another namespace, and a
        // wrong match would defeat the purpose of an independent oracle.
        rows[Normalize(full)] = row;
        if (full != name)
            rows[Normalize(name)] = row;
    }

    foreach (XElement assembly in targets.SelectMany(t => t.Descendants("Assembly")))
    {
        string? name = assembly.Attribute("Name")?.Value;
        XElement? metrics = assembly.Element("Metrics");
        if (name is null || metrics is null)
            continue;

        var fields = ReadMetricFields(metrics);
        rows[Normalize(name)] = new OracleRow(
            Num(fields, "MaintainabilityIndex"),
            Num(fields, "CyclomaticComplexity"),
            Num(fields, "ClassCoupling"),
            Num(fields, "SourceLines")
        );
    }

    return rows;
}

static Dictionary<string, string> ReadMetricFields(XElement metrics)
{
    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (XElement m in metrics.Elements("Metric"))
    {
        string? mn = m.Attribute("Name")?.Value;
        string? mv = m.Attribute("Value")?.Value;
        if (mn is not null && mv is not null)
            fields[mn] = mv;
    }
    return fields;
}

static int Num(Dictionary<string, string> fields, string key) =>
    fields.TryGetValue(key, out string? v)
    && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
        ? n
        : -1;

internal sealed record BaselineRow(int Mi, int Cc, int Cl, int Sloc);

internal sealed class BaselineFile
{
    public BaselineRow? Assembly { get; set; }

    public Dictionary<string, BaselineRow> Types { get; } = new(StringComparer.Ordinal);
}

internal sealed record OracleRow(int Mi, int Cc, int Cl, int Sloc);
