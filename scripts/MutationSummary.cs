#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

// -----------------------------------------------------------------------------
// MutationSummary.cs
//
// Reader for #261, merger for #668: the nightly sweep runs Stryker once per
// partition (stryker-partitions.json) and this script merges the partition
// reports into the numbers the nightly PR body needs: the score per project,
// and the surviving-mutant inventory, because a score without a list is not
// triageable.
//
// The merge is what makes the number a COMPLETE report, so it refuses to
// produce one silently:
//   * a partition whose report is missing or unreadable fails the run (a
//     timed-out or crashed job must not leave a "complete" score behind);
//   * a partition that owns no mutant fails the run (a glob that matches
//     nothing would otherwise pass while covering nothing);
//   * a mutant tested by two partitions fails the run (overlapping globs would
//     count it twice).
// Gaps between partitions cannot happen: each project has one "rest" partition
// that mutates whatever the others do not claim (see the workflow step).
//
// The score is RECOMPUTED, never taken from the report's own headline, and the
// denominators are explicit: mutants the runner could not meaningfully test
// (compile errors, ignored, pending) are excluded from the denominator, and the
// exclusions are named so a reader can see exactly what the percentage means.
// A mutation score is only as honest as its visible denominator. There is no
// floor: the summary never fails on the score itself (#261).
//
//   dotnet run scripts/MutationSummary.cs -- --partitions stryker-partitions.json
//       --report-path "reports/stryker-report-{name}/mutation-report.json"
//       [--out <markdown file>]
// -----------------------------------------------------------------------------

const string FilteredOut = "Removed by mutate filter";

string? partitionsPath = null;
string? reportPathTemplate = null;
string? outPath = null;

var argv = Environment.GetCommandLineArgs();
for (int i = 1; i < argv.Length; i++)
{
    switch (argv[i])
    {
        case "--partitions":
            partitionsPath = Require(argv, ref i, "--partitions");
            break;
        case "--report-path":
            reportPathTemplate = Require(argv, ref i, "--report-path");
            break;
        case "--out":
            outPath = Require(argv, ref i, "--out");
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {argv[i]}");
            return 2;
    }
}

if (partitionsPath is null || !File.Exists(partitionsPath))
{
    Console.Error.WriteLine("--partitions <stryker-partitions.json> is required and must exist.");
    return 2;
}

if (reportPathTemplate is null || !reportPathTemplate.Contains("{name}", StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "--report-path <template containing {name}> is required, e.g. reports/stryker-report-{name}/mutation-report.json"
    );
    return 2;
}

using JsonDocument plan = JsonDocument.Parse(File.ReadAllText(partitionsPath));
var labels = new Dictionary<string, string>(StringComparer.Ordinal);
if (plan.RootElement.TryGetProperty("projects", out JsonElement projectsEl))
{
    foreach (JsonProperty p in projectsEl.EnumerateObject())
        labels[p.Name] = p.Value.GetString() ?? p.Name;
}

var partitions = new List<(string Name, string Project)>();
foreach (JsonElement p in Enumerate(plan.RootElement, "partitions"))
    partitions.Add((Str(p, "name"), Str(p, "project")));

var problems = new List<string>();
if (partitions.Count == 0)
    problems.Add($"{partitionsPath} declares no partitions.");
foreach (
    string dup in partitions
        .GroupBy(p => p.Name, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
)
    problems.Add($"Partition name '{dup}' is declared more than once.");

// project -> (relative file -> (mutant id -> owner)). Stryker marks a mutant outside a partition's
// globs "Ignored: Removed by mutate filter"; every other status belongs to the partition. Compile
// errors are reported by every partition that saw them, so only tested statuses must be unique, and
// only tested mutants count towards a partition's "examined N".
var owned = new Dictionary<string, Dictionary<string, Dictionary<string, Owned>>>(
    StringComparer.Ordinal
);
var ownedPerPartition = new List<(string Name, int Owned)>();

foreach ((string name, string project) in partitions)
{
    string path = reportPathTemplate.Replace("{name}", name, StringComparison.Ordinal);
    if (!File.Exists(path))
    {
        problems.Add(
            $"Partition '{name}': no report at {path} (job failed, timed out or was cancelled)."
        );
        continue;
    }

    JsonDocument report;
    try
    {
        report = JsonDocument.Parse(File.ReadAllText(path));
    }
    catch (JsonException ex)
    {
        problems.Add($"Partition '{name}': {path} is not valid JSON ({ex.Message}).");
        continue;
    }

    using (report)
    {
        string root = report.RootElement.TryGetProperty("projectRoot", out JsonElement r)
            ? r.GetString() ?? string.Empty
            : string.Empty;
        if (!owned.TryGetValue(project, out var files))
            owned[project] = files = new(StringComparer.Ordinal);

        int count = 0;
        foreach (JsonProperty file in EnumerateObject(report.RootElement, "files"))
        {
            string rel = Relative(file.Name, root);
            foreach (JsonElement mutant in Enumerate(file.Value, "mutants"))
            {
                string status = Str(mutant, "status");
                if (status == "Ignored" && Str(mutant, "statusReason") == FilteredOut)
                    continue; // another partition owns it

                if (IsTested(status))
                    count++;
                string id = Str(mutant, "id");
                if (!files.TryGetValue(rel, out var mutants))
                    files[rel] = mutants = new(StringComparer.Ordinal);

                var candidate = new Owned(name, status);
                if (!mutants.TryGetValue(id, out Owned? existing))
                {
                    mutants[id] = candidate;
                }
                else if (IsTested(existing.Status) && IsTested(status))
                {
                    problems.Add(
                        $"Mutant {id} in {rel} was tested by both '{existing.Partition}' and '{name}': their globs overlap."
                    );
                }
                else if (IsTested(status))
                {
                    mutants[id] = candidate;
                }
            }
        }

        ownedPerPartition.Add((name, count));
        if (count == 0)
        {
            problems.Add(
                $"Partition '{name}': its report tested no mutant, so its globs match nothing. Fix stryker-partitions.json."
            );
        }
    }
}

var sb = new StringBuilder();
foreach (string project in partitions.Select(p => p.Project).Distinct(StringComparer.Ordinal))
{
    string label = labels.TryGetValue(project, out string? l) ? l : project;
    if (!owned.TryGetValue(project, out var files))
    {
        sb.AppendLine($"### {label}");
        sb.AppendLine();
        sb.AppendLine("No partition of this project produced a report.");
        sb.AppendLine();
        continue;
    }

    AppendProject(
        sb,
        label,
        files,
        ownedPerPartition.Count(o => partitions.Any(p => p.Name == o.Name && p.Project == project)),
        partitions.Count(p => p.Project == project)
    );
}

sb.AppendLine("<details><summary>Mutants tested per partition</summary>");
sb.AppendLine();
sb.AppendLine("| partition | mutants |");
sb.AppendLine("|:--|--:|");
foreach ((string name, int count) in ownedPerPartition)
    sb.AppendLine($"| `{name}` | {count} |");
sb.AppendLine();
sb.AppendLine("</details>");
sb.AppendLine();

if (problems.Count > 0)
{
    sb.AppendLine("### Incomplete report");
    sb.AppendLine();
    sb.AppendLine(
        "The numbers above do not cover the whole generator, so they are not a mutation score:"
    );
    sb.AppendLine();
    foreach (string problem in problems)
        sb.AppendLine($"- {problem}");
    sb.AppendLine();
}

Console.Write(sb);
if (outPath is not null)
    File.WriteAllText(outPath, sb.ToString());

foreach (string problem in problems)
    Console.Error.WriteLine($"::error title=Incomplete mutation report::{problem}");

// #261: report-only. A failure here means the report is incomplete, never that the score is low.
return problems.Count == 0 ? 0 : 1;

static void AppendProject(
    StringBuilder sb,
    string label,
    Dictionary<string, Dictionary<string, Owned>> files,
    int reported,
    int declared
)
{
    int total = 0;
    int killed = 0;
    int timeout = 0;
    int noCoverage = 0;
    int compileError = 0;
    int ignored = 0;
    int pending = 0;
    var survivors = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach ((string file, Dictionary<string, Owned> mutants) in files)
    {
        foreach (Owned mutant in mutants.Values)
        {
            total++;
            switch (mutant.Status)
            {
                case "Killed":
                    killed++;
                    break;
                case "Timeout":
                    timeout++;
                    break;
                case "NoCoverage":
                    noCoverage++;
                    break;
                case "CompileError":
                    compileError++;
                    break;
                case "Ignored":
                    ignored++;
                    break;
                case "Pending":
                    pending++;
                    break;
                case "Survived":
                    survivors[file] = survivors.GetValueOrDefault(file) + 1;
                    break;
            }
        }
    }

    int tested = total - compileError - ignored - pending;
    double score = tested == 0 ? 0 : 100.0 * (killed + timeout) / tested;
    int survived = survivors.Values.Sum();

    sb.AppendLine($"### {label}");
    sb.AppendLine();
    sb.AppendLine(
        $"Mutation score **{score.ToString("F1", CultureInfo.InvariantCulture)}%** — "
            + $"{killed + timeout} detected / {tested} tested "
            + $"({survived} survived, {noCoverage} not covered by any test; "
            + $"{total} generated; {compileError} compile-error, {ignored} ignored, {pending} pending excluded). "
            + $"Merged from {reported} of {declared} partition report(s)."
    );
    sb.AppendLine();
    sb.AppendLine("| surviving mutants in | count |");
    sb.AppendLine("|:--|--:|");
    foreach (
        (string file, int count) in survivors
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(12)
    )
    {
        sb.AppendLine($"| `{file}` | {count} |");
    }
    if (survivors.Count > 12)
        sb.AppendLine($"| … {survivors.Count - 12} more files | — |");
    sb.AppendLine();
}

static bool IsTested(string status) => status is not ("CompileError" or "Ignored" or "Pending");

static string Require(string[] argv, ref int i, string flag)
{
    if (i + 1 >= argv.Length)
    {
        Console.Error.WriteLine($"{flag} requires a value");
        Environment.Exit(2);
    }
    return argv[++i];
}

static IEnumerable<JsonElement> Enumerate(JsonElement el, string property) =>
    el.TryGetProperty(property, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array
        ? arr.EnumerateArray()
        : [];

static IEnumerable<JsonProperty> EnumerateObject(JsonElement el, string property) =>
    el.TryGetProperty(property, out JsonElement obj) && obj.ValueKind == JsonValueKind.Object
        ? obj.EnumerateObject()
        : [];

static string Str(JsonElement el, string property) =>
    el.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.String
        ? v.GetString()!
        : "?";

// Report file keys are absolute paths on the runner that produced them; key the merge on the path
// relative to the project root so reports from different runners line up.
static string Relative(string path, string root)
{
    string p = path.Replace('\\', '/');
    string r = root.Replace('\\', '/').TrimEnd('/');
    if (r.Length > 0 && p.StartsWith(r + "/", StringComparison.Ordinal))
        return p[(r.Length + 1)..];
    int idx = p.IndexOf("src/", StringComparison.Ordinal);
    return idx >= 0 ? p[idx..] : p;
}

sealed record Owned(string Partition, string Status);
