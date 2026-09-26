#:sdk Microsoft.NET.Sdk.Razor
#:package Microsoft.AspNetCore.Components.Web@10.0.12
#:package Microsoft.Extensions.Logging.Abstractions@10.0.12
#:property EnableDefaultItems=true
#:property EnableDefaultCompileItems=true
#pragma warning disable CA1852

// -----------------------------------------------------------------------------
// DerivedReport.cs — reports over the trees scripts/DerivedOutputs.cs writes.
//
// A file-based app spread over this folder: the other .cs files and the .razor components beside
// it are compiled in (EnableDefaultItems), and the HTML pages are Razor components rendered with
// the official HtmlRenderer from Microsoft.AspNetCore.Components.Web — no web host, no ASP.NET
// shared framework, no project file. report.css and report.js are inlined into every page.
//
// Two views, two commands:
//
//   state  One tree as it is: the current numbers (API surface, generated and hand-written code
//          metrics, duplication, call graph) and every file in full. Main's weekly snapshot and
//          every release publish this; a pull request publishes it for its head.
//
//   diff   A pull request against its merge base: the sticky comment (a deterministic summary of
//          the whole PR — files by area, API catalogues, tests, CI and tooling touched, then what
//          drifted in the derived outputs), the full unified patch, and the full diff as HTML.
//
// Usage:
//   dotnet run scripts/DerivedReport/DerivedReport.cs -- state --tree <dir> --label <text>
//       [--html <file>]
//   dotnet run scripts/DerivedReport/DerivedReport.cs -- diff --base <dir> --head <dir>
//       [--comment <file>] [--patch <file>] [--html <file>]
//       [--repo <dir> --base-commit <sha> --head-commit <sha>] [--base-label <text>]
//       [--base-source <text>] [--diff-url <url>] [--state-url <url>] [--artifact-url <url>]
//       [--head-status <outcome>] [--base-status <outcome>]
//
// With a head or base that did not succeed there is nothing to diff: --comment still gets a body
// that says so (and the PR's own changes, when --repo is given); --patch and --html are skipped.
// Exit codes: 0 rendered, 2 usage.
// -----------------------------------------------------------------------------

using System.Text;
using Parquet.SourceGenerator.Tools.DerivedReport;

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: see the header of scripts/DerivedReport/DerivedReport.cs.");
    return 2;
}

var utf8 = new UTF8Encoding(false);

if (options.Command == "state")
{
    string tree = options.Required("--tree");
    var state = new State(tree).Build(options.Get("--label") ?? Path.GetFileName(tree));
    if (options.Get("--html") is { } statePath)
    {
        File.WriteAllText(
            statePath,
            await Html.RenderAsync<StatePage>(
                new Dictionary<string, object?> { ["Report"] = state }
            ),
            utf8
        );
    }

    Console.WriteLine($"State of {state.Label}: {state.Sections.Sum(s => s.Files.Count)} files.");
    return 0;
}

string baseDir = Path.GetFullPath(options.Required("--base"));
string headDir = Path.GetFullPath(options.Required("--head"));
string baseLabel = options.Get("--base-label") ?? "base";
string headStatus = options.Get("--head-status") ?? "success";
string baseStatus = options.Get("--base-status") ?? "success";
var links = new Links(
    options.Get("--diff-url"),
    options.Get("--state-url"),
    options.Get("--artifact-url")
);

// The PR's own changes come from git, not the trees, so they are there even when a tree is not.
IReadOnlyList<Table> summary =
    options.Get("--repo") is { } repo
    && options.Get("--base-commit") is { } baseCommit
    && options.Get("--head-commit") is { } headCommit
        ? PrSummary.Build(repo, baseCommit, headCommit)
        : [];

string driftStatus =
    headStatus != "success"
        ? $"**Not produced.** Producing this PR's derived outputs ended with `{headStatus}`: a gate in "
            + "`scripts/DerivedOutputs.cs` failed (golden models compile, emitted code compiles, "
            + "`CodeMetricsConfig.txt`, call-graph rules), or the run was stopped. The job log says which."
    : baseStatus != "success"
        ? $"**No baseline.** This PR's derived outputs passed their gates, but the merge base's could not "
            + $"be fetched or regenerated (`{baseStatus}`), so there is nothing to compare them with."
    : "";

if (driftStatus.Length > 0)
{
    var empty = new DiffReport(baseLabel, options.Get("--base-source"), summary, [], []);
    if (options.Get("--comment") is { } failedComment)
    {
        File.WriteAllText(failedComment, Markdown.Comment(empty, driftStatus, links), utf8);
    }

    Console.WriteLine($"Head {headStatus}, base {baseStatus}: no diff.");
    return 0;
}

var changes = TreeDiff.Changes(baseDir, headDir);
var report = new DiffReport(
    baseLabel,
    options.Get("--base-source"),
    summary,
    new Drift(baseDir, headDir, changes).Build(),
    TreeDiff.Group(changes)
);

if (options.Get("--patch") is { } patchPath)
{
    File.WriteAllText(patchPath, string.Concat(changes.Select(c => c.Patch)), utf8);
}

if (options.Get("--html") is { } htmlPath)
{
    File.WriteAllText(
        htmlPath,
        await Html.RenderAsync<DiffPage>(new Dictionary<string, object?> { ["Report"] = report }),
        utf8
    );
}

if (options.Get("--comment") is { } commentPath)
{
    File.WriteAllText(commentPath, Markdown.Comment(report, "", links), utf8);
}

Console.WriteLine($"{changes.Count} derived file(s) differ from {baseLabel}.");
return 0;

/// <summary>A command followed by "--name value" pairs.</summary>
internal sealed class Options(string command, Dictionary<string, string> values)
{
    private static readonly HashSet<string> Known =
    [
        "--tree",
        "--label",
        "--html",
        "--base",
        "--head",
        "--comment",
        "--patch",
        "--repo",
        "--base-commit",
        "--head-commit",
        "--base-label",
        "--base-source",
        "--diff-url",
        "--state-url",
        "--artifact-url",
        "--head-status",
        "--base-status",
    ];

    public string Command => command;

    public static Options? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("state" or "diff") || args.Length % 2 == 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (!Known.Contains(args[i]))
            {
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return null;
            }

            values[args[i]] = args[i + 1];
        }

        return new Options(args[0], values);
    }

    public string? Get(string name) =>
        values.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;

    public string Required(string name) =>
        Get(name) ?? throw new ArgumentException($"{command} needs {name}.");
}
