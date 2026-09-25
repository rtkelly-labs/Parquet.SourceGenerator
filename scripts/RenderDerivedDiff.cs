#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// -----------------------------------------------------------------------------
// RenderDerivedDiff.cs
//
// Diffs two trees written by scripts/DerivedOutputs.cs — the pull request's merge base and its
// head — and renders the result three ways: the Markdown body of the sticky PR comment, the full
// unified patch for the `derived-outputs` artifact, and a self-contained HTML page of the full diff
// (no scripts or styles loaded from anywhere) that CI uploads unzipped, so the comment can link to
// one downloadable file.
//
// The comment is ordered so the root change reads first: the emitted public API (a signature
// added, removed or changed), then the emitted code, then the numbers that follow from it
// (generated-code metrics, src/ metrics, duplication, call graph). The API section is expanded;
// everything else is collapsed. GitHub caps a comment at 65,536 characters, so per-file and total
// budgets apply; anything past them is named with its line counts and linked to the HTML page,
// which has no budget: every changed file, in the same section order.
//
// Two passes, because the page's download URL exists only once it is uploaded: first --patch and
// --html, then --comment with --html-url. Each pass writes only the outputs it is given.
//
// The comment always describes the latest run, so it is also rendered when there is nothing to
// diff: --head-status other than `success` (the head failed its gates) and --base-status other
// than `success` (the merge base's outputs could not be fetched or regenerated) each produce a
// body that says so, rather than leaving an earlier run's diff standing as if it were current.
//
// Usage:
//   dotnet run scripts/RenderDerivedDiff.cs -- --base <dir> --head <dir>
//       [--comment <file>] [--patch <file>] [--html <file>] [--html-url <url>]
//       [--base-label <text>] [--base-source <text>] [--artifact-url <url>]
//       [--head-status <outcome>] [--base-status <outcome>]
// At least one of --comment, --patch and --html. With a head or base that did not succeed there is
// no diff: --comment still gets a body saying so, --patch and --html are not written.
// Exit codes: 0 rendered (whether or not anything changed), 2 usage.
// -----------------------------------------------------------------------------

const string Marker = "<!-- derived-review-diff -->";
const int CommentBudget = 60_000;
const int FileBudget = 12_000;
const string OtherSection = "Other";

// Inline so the page opens offline, from a download, with nothing else beside it.
const string HtmlStyle = """
    :root { color-scheme: light dark; --bg: #ffffff; --fg: #1f2328; --muted: #59636e;
      --line: #d1d9e0; --add: #dafbe1; --add-fg: #116329; --del: #ffebe9; --del-fg: #a40e26;
      --hunk: #ddf4ff; --head: #f6f8fa; }
    @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --fg: #e6edf3; --muted: #9198a1;
      --line: #3d444d; --add: #12261e; --add-fg: #3fb950; --del: #25171c; --del-fg: #f85149;
      --hunk: #121d2f; --head: #151b23; } }
    * { box-sizing: border-box; }
    body { margin: 0 auto; max-width: 1200px; padding: 16px; background: var(--bg); color: var(--fg);
      font: 14px/1.5 -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
    code, table.diff { font: 12px/1.45 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
    h1 { font-size: 20px; } h2 { font-size: 16px; margin-top: 32px; }
    .meta { color: var(--muted); }
    .a { color: var(--add-fg); } .d { color: var(--del-fg); } .st { color: var(--muted); }
    table.summary { border-collapse: collapse; margin-bottom: 12px; }
    table.summary th, table.summary td { border: 1px solid var(--line); padding: 4px 10px; }
    table.summary td:not(:first-child) { text-align: right; }
    ul.toc { columns: 2 360px; padding-left: 18px; }
    details { border: 1px solid var(--line); border-radius: 6px; margin: 8px 0; overflow: hidden; }
    summary { background: var(--head); padding: 6px 10px; cursor: pointer; }
    table.diff { border-collapse: collapse; display: block; overflow-x: auto; }
    table.diff td { padding: 0 8px; white-space: pre; vertical-align: top; }
    table.diff td:nth-child(-n+2) { color: var(--muted); text-align: right; user-select: none;
      min-width: 48px; border-right: 1px solid var(--line); }
    table.diff td:last-child { width: 100%; }
    tr.a td:last-child { background: var(--add); } tr.d td:last-child { background: var(--del); }
    tr.h td { background: var(--hunk); color: var(--muted); } tr.m td { color: var(--muted); }
    button { font: inherit; padding: 2px 10px; }

    """;
const string HtmlScript = """
    for (const b of document.querySelectorAll("button[data-open]")) {
      b.addEventListener("click", () => {
        for (const d of document.querySelectorAll("details")) d.open = b.dataset.open === "1";
      });
    }
    const reveal = () => {
      const t = location.hash && document.getElementById(location.hash.slice(1));
      if (t && t.tagName === "DETAILS") t.open = true;
    };
    addEventListener("hashchange", reveal);
    reveal();

    """;

string? baseDir = null;
string? headDir = null;
string? commentPath = null;
string? patchPath = null;
string? htmlPath = null;
string? htmlUrl = null;
string baseLabel = "base";
string? baseSource = null;
string? artifactUrl = null;
string headStatus = "success";
string baseStatus = "success";

string[] argv = Environment.GetCommandLineArgs();
for (int i = 1; i < argv.Length; i++)
{
    string Next() =>
        i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{argv[i]} needs a value");
    switch (argv[i])
    {
        case "--base":
            baseDir = Path.GetFullPath(Next());
            break;
        case "--head":
            headDir = Path.GetFullPath(Next());
            break;
        case "--comment":
            commentPath = Next();
            break;
        case "--patch":
            patchPath = Next();
            break;
        case "--html":
            htmlPath = Next();
            break;
        case "--html-url":
            htmlUrl = Next();
            break;
        case "--base-label":
            baseLabel = Next();
            break;
        case "--base-source":
            baseSource = Next();
            break;
        case "--artifact-url":
            artifactUrl = Next();
            break;
        case "--head-status":
            headStatus = Next();
            break;
        case "--base-status":
            baseStatus = Next();
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {argv[i]}");
            return 2;
    }
}

if (
    baseDir is null
    || headDir is null
    || (commentPath is null && patchPath is null && htmlPath is null)
)
{
    Console.Error.WriteLine(
        "Usage: RenderDerivedDiff.cs --base <dir> --head <dir> [--comment <file>] [--patch <file>] [--html <file>] [--html-url <url>] [--base-label <text>] [--base-source <text>] [--artifact-url <url>] [--head-status <outcome>] [--base-status <outcome>]"
    );
    return 2;
}

string artifactLine = artifactUrl is null
    ? "the `derived-outputs` artifact of this run"
    : $"the [`derived-outputs` artifact]({artifactUrl})";

if (headStatus != "success")
{
    if (commentPath is null)
    {
        Console.WriteLine($"Head status {headStatus}; no diff to write.");
        return 0;
    }

    WriteComment(
        "## Derived output: not produced\n\n"
            + $"Producing the derived outputs for this PR ended with `{headStatus}`, so there is no "
            + "diff against the merge base. A gate in `scripts/DerivedOutputs.cs` failed (golden "
            + "models compile, emitted code compiles, `CodeMetricsConfig.txt`, call-graph rules), or "
            + "the run was stopped. The job log says which.\n"
    );
    Console.WriteLine($"Head status {headStatus}; wrote a no-diff comment to {commentPath}.");
    return 0;
}

if (baseStatus != "success")
{
    if (commentPath is null)
    {
        Console.WriteLine($"Base status {baseStatus}; no diff to write.");
        return 0;
    }

    WriteComment(
        $"## Derived output: no baseline for `{baseLabel}`\n\n"
            + "This PR's derived outputs were produced and passed their gates, but the merge base's "
            + $"could not be fetched or regenerated (`{baseStatus}`), so there is nothing to diff "
            + "against.\n"
    );
    Console.WriteLine($"Base status {baseStatus}; wrote a no-diff comment to {commentPath}.");
    return 0;
}

var sections = new (string Title, bool Expanded, Func<string, bool> Owns)[]
{
    (
        "Emitted public API",
        true,
        p =>
            p.StartsWith("golden/", StringComparison.Ordinal)
            && (
                p.EndsWith(".api.txt", StringComparison.Ordinal)
                || p.EndsWith(".api.shape.txt", StringComparison.Ordinal)
            )
    ),
    (
        "Emitted code",
        false,
        p =>
            p.StartsWith("golden/", StringComparison.Ordinal)
            && p.EndsWith(".g.cs", StringComparison.Ordinal)
    ),
    (
        "Generated-code metrics",
        false,
        p => p.StartsWith("metrics/generated/", StringComparison.Ordinal)
    ),
    (
        "Duplication",
        false,
        p => string.Equals(p, "metrics/duplication.txt", StringComparison.Ordinal)
    ),
    (
        "Hand-written code metrics (src/)",
        false,
        p => p.StartsWith("metrics/", StringComparison.Ordinal)
    ),
    ("Call graph", false, p => p.StartsWith("callgraph/", StringComparison.Ordinal)),
    // Last, and matches everything: a new kind of output shows up in the comment instead of
    // failing the render before a section is written for it.
    (OtherSection, false, _ => true),
};

// Step-summary fragments are views of the files diffed here, not outputs in their own right.
static bool Diffed(string relative) =>
    relative.Contains('/', StringComparison.Ordinal)
    && !relative.EndsWith("-summary.md", StringComparison.Ordinal);

List<string> paths = Files(baseDir)
    .Concat(Files(headDir))
    .Where(Diffed)
    .Distinct(StringComparer.Ordinal)
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToList();

var changes = new List<FileChange>();
foreach (string relative in paths)
{
    string before = Path.Combine(baseDir, relative);
    string after = Path.Combine(headDir, relative);
    bool inBase = File.Exists(before);
    bool inHead = File.Exists(after);
    if (
        inBase
        && inHead
        && File.ReadAllBytes(before).AsSpan().SequenceEqual(File.ReadAllBytes(after))
    )
    {
        continue;
    }

    string patch = GitDiff(inBase ? before : "/dev/null", inHead ? after : "/dev/null", relative);
    // Count hunk lines only. Skipping every line that starts with "---" or "+++" instead would
    // also skip a removed line whose text starts with "--", or an added one starting with "++".
    int added = 0;
    int removed = 0;
    bool inHunks = false;
    foreach (string line in patch.Split('\n'))
    {
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            inHunks = true;
            continue;
        }

        if (!inHunks)
        {
            continue;
        }

        if (line.StartsWith('+'))
        {
            added++;
        }
        else if (line.StartsWith('-'))
        {
            removed++;
        }
    }

    string status =
        !inBase ? "added"
        : !inHead ? "removed"
        : "modified";
    changes.Add(new FileChange(relative, status, added, removed, patch));
}

if (patchPath is not null)
{
    File.WriteAllText(
        patchPath,
        string.Concat(changes.Select(c => c.Patch)),
        new UTF8Encoding(false)
    );
}

if (htmlPath is not null)
{
    File.WriteAllText(htmlPath, RenderHtml(), new UTF8Encoding(false));
}

if (commentPath is null)
{
    Console.WriteLine($"{changes.Count} derived file(s) differ from {baseLabel}.");
    return 0;
}

string fullDiffLink = htmlUrl is null ? "the artifact" : $"the [full diff]({htmlUrl})";

var body = new StringBuilder();
body.Append("## Derived output: this PR vs `").Append(baseLabel).Append("`\n\n");
body.Append(
    "Emitted code, its public API, code metrics and the call graph are generated from the code on "
        + "every run and are not checked in. This is how they differ from the merge base.\n\n"
);
if (htmlUrl is not null && changes.Count > 0)
{
    body.Append(
        $"**[Full diff (HTML)]({htmlUrl})**: every changed file, nothing left out. It downloads as "
            + "one file; open it in a browser.\n\n"
    );
}

if (changes.Count == 0)
{
    body.Append("**No derived output changed.**\n");
}
else
{
    body.Append("| | Files | + | − |\n|:--|--:|--:|--:|\n");
    foreach (var section in sections)
    {
        var owned = changes.Where(c => SectionOf(c.Path) == section.Title).ToList();
        if (owned.Count == 0)
        {
            continue;
        }

        body.Append(
            $"| {section.Title} | {owned.Count} | {owned.Sum(c => c.Added)} | {owned.Sum(c => c.Removed)} |\n"
        );
    }

    body.Append('\n');
    var omitted = new List<FileChange>();
    foreach (var section in sections)
    {
        var owned = changes.Where(c => SectionOf(c.Path) == section.Title).ToList();
        if (owned.Count == 0)
        {
            continue;
        }

        body.Append("### ").Append(section.Title).Append("\n\n");
        foreach (FileChange change in owned)
        {
            string label = $"`{change.Path}` — {change.Status}, +{change.Added} −{change.Removed}";
            string diffBody = StripHeader(change.Patch);
            string block =
                (section.Expanded ? "<details open>" : "<details>")
                + $"<summary>{label}</summary>\n\n````diff\n{diffBody.TrimEnd('\n')}\n````\n\n</details>\n\n";
            // Two different reasons, named separately: a file whose own diff is over its budget,
            // and a small one left out only because earlier sections used up the comment.
            string? reason =
                diffBody.Length > FileBudget ? "too large for a comment"
                : body.Length + block.Length > CommentBudget ? "comment limit reached"
                : null;
            if (reason is not null)
            {
                omitted.Add(change);
                body.Append("- ")
                    .Append(label)
                    .Append(" — ")
                    .Append(reason)
                    .Append("; in ")
                    .Append(fullDiffLink)
                    .Append("\n\n");
                continue;
            }

            body.Append(block);
        }
    }

    if (omitted.Count > 0)
    {
        body.Append(
            $"> {omitted.Count} file diff(s) were left out to fit GitHub's comment limit; "
                + $"{fullDiffLink} has all of them.\n\n"
        );
    }
}

WriteComment(body.ToString());
Console.WriteLine($"{changes.Count} derived file(s) differ from {baseLabel}; wrote {commentPath}.");
return 0;

string SectionOf(string relative) => sections.First(s => s.Owns(relative)).Title;

// The full diff as one self-contained page: a summary table and contents linking to every file,
// then each section with every file's diff in full — line numbers, additions and removals marked.
// Nothing is fetched when it opens, and it carries no timestamp, so the same two trees always
// produce the same bytes. The API section starts expanded, like the comment.
string RenderHtml()
{
    var h = new StringBuilder();
    h.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
        .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
        .Append("<title>Derived output diff vs ")
        .Append(Html(baseLabel))
        .Append("</title>\n<style>\n")
        .Append(HtmlStyle)
        .Append("</style>\n</head>\n<body>\n<header>\n<h1>Derived output: this PR vs <code>")
        .Append(Html(baseLabel))
        .Append("</code></h1>\n");
    if (baseSource is not null)
    {
        // The source is written as Markdown for the comment; its `code` spans become <code> here.
        h.Append("<p class=\"meta\">Merge-base outputs: ")
            .Append(Regex.Replace(Html(baseSource), "`([^`]*)`", "<code>$1</code>"))
            .Append(".</p>\n");
    }

    if (changes.Count == 0)
    {
        h.Append(
            "<p><strong>No derived output changed.</strong></p>\n</header>\n</body>\n</html>\n"
        );
        return h.ToString();
    }

    h.Append("<p class=\"controls\"><button type=\"button\" data-open=\"1\">Expand all</button> ")
        .Append("<button type=\"button\" data-open=\"0\">Collapse all</button></p>\n</header>\n")
        .Append("<nav>\n<table class=\"summary\">\n<thead><tr><th></th><th>Files</th><th>+</th>")
        .Append("<th>−</th></tr></thead>\n<tbody>\n");
    var owned = sections
        .Select(s => (Section: s, Files: changes.Where(c => SectionOf(c.Path) == s.Title).ToList()))
        .Where(s => s.Files.Count > 0)
        .ToList();
    foreach (var (section, files) in owned)
    {
        h.Append("<tr><td><a href=\"#")
            .Append(Slug("s-" + section.Title))
            .Append("\">")
            .Append(Html(section.Title))
            .Append("</a></td><td>")
            .Append(files.Count)
            .Append("</td><td class=\"a\">+")
            .Append(files.Sum(c => c.Added))
            .Append("</td><td class=\"d\">−")
            .Append(files.Sum(c => c.Removed))
            .Append("</td></tr>\n");
    }

    h.Append("</tbody>\n</table>\n<ul class=\"toc\">\n");
    foreach (FileChange change in owned.SelectMany(s => s.Files))
    {
        h.Append("<li><a href=\"#")
            .Append(Slug("f-" + change.Path))
            .Append("\"><code>")
            .Append(Html(change.Path))
            .Append("</code></a> <span class=\"a\">+")
            .Append(change.Added)
            .Append("</span> <span class=\"d\">−")
            .Append(change.Removed)
            .Append("</span></li>\n");
    }

    h.Append("</ul>\n</nav>\n<main>\n");
    foreach (var (section, files) in owned)
    {
        h.Append("<h2 id=\"")
            .Append(Slug("s-" + section.Title))
            .Append("\">")
            .Append(Html(section.Title))
            .Append("</h2>\n");
        foreach (FileChange change in files)
        {
            h.Append(section.Expanded ? "<details open id=\"" : "<details id=\"")
                .Append(Slug("f-" + change.Path))
                .Append("\"><summary><code>")
                .Append(Html(change.Path))
                .Append("</code> <span class=\"st\">")
                .Append(change.Status)
                .Append("</span> <span class=\"a\">+")
                .Append(change.Added)
                .Append("</span> <span class=\"d\">−")
                .Append(change.Removed)
                .Append("</span></summary>\n<table class=\"diff\">\n");
            AppendDiffRows(h, StripHeader(change.Patch));
            h.Append("</table>\n</details>\n");
        }
    }

    h.Append("</main>\n<script>\n").Append(HtmlScript).Append("</script>\n</body>\n</html>\n");
    return h.ToString();
}

static void AppendDiffRows(StringBuilder h, string hunks)
{
    int oldLine = 0;
    int newLine = 0;
    foreach (string line in hunks.Split('\n'))
    {
        Match header = Regex.Match(line, @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
        if (header.Success)
        {
            oldLine = int.Parse(header.Groups[1].Value);
            newLine = int.Parse(header.Groups[2].Value);
            h.Append("<tr class=\"h\"><td></td><td></td><td>")
                .Append(Html(line))
                .Append("</td></tr>\n");
            continue;
        }

        if (line.Length == 0)
        {
            continue;
        }

        (string css, string left, string right) = line[0] switch
        {
            '+' => ("a", "", (newLine++).ToString()),
            '-' => ("d", (oldLine++).ToString(), ""),
            '\\' => ("m", "", ""),
            _ => ("c", (oldLine++).ToString(), (newLine++).ToString()),
        };
        h.Append("<tr class=\"")
            .Append(css)
            .Append("\"><td>")
            .Append(left)
            .Append("</td><td>")
            .Append(right)
            .Append("</td><td>")
            .Append(Html(line))
            .Append("</td></tr>\n");
    }
}

static string Html(string text) => WebUtility.HtmlEncode(text);

static string Slug(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-");

// Every body starts with the marker the sticky-comment step finds it by, and ends by naming where
// the full trees are and where the baseline came from, so the body depends only on the two trees
// and those two facts.
void WriteComment(string content)
{
    string footer =
        $"\nThis run's outputs (head, base and the full patch, as far as they were produced) are in {artifactLine}.\n"
        + (baseSource is null ? "" : $"\n<sub>Merge-base outputs: {baseSource}.</sub>\n");
    File.WriteAllText(commentPath!, Marker + "\n" + content + footer, new UTF8Encoding(false));
}

static IEnumerable<string> Files(string root) =>
    Directory.Exists(root)
        ? Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
        : Enumerable.Empty<string>();

static string GitDiff(string before, string after, string relative)
{
    var start = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (
        string arg in new[]
        {
            "diff",
            "--no-index",
            "--no-color",
            "--unified=3",
            "--src-prefix=base/",
            "--dst-prefix=head/",
            before,
            after,
        }
    )
    {
        start.ArgumentList.Add(arg);
    }

    using Process process =
        Process.Start(start) ?? throw new InvalidOperationException("git did not start");
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();

    // --no-index exits 1 when the files differ; anything else is a failure.
    if (process.ExitCode > 1)
    {
        throw new InvalidOperationException($"git diff failed for {relative}: {error}");
    }

    // Name the file by its path inside the tree, not by the temp directory it was read from. Only
    // the header, before the first hunk: inside a hunk, a removed "-- x" line reads "--- x" and an
    // added "++ x" line reads "+++ x", and rewriting those would corrupt the diff.
    var lines = output.Split('\n').ToList();
    for (int i = 0; i < lines.Count && !lines[i].StartsWith("@@", StringComparison.Ordinal); i++)
    {
        if (lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
        {
            lines[i] = $"diff --git base/{relative} head/{relative}";
        }
        else if (lines[i].StartsWith("--- ", StringComparison.Ordinal))
        {
            lines[i] = before == "/dev/null" ? "--- /dev/null" : $"--- base/{relative}";
        }
        else if (lines[i].StartsWith("+++ ", StringComparison.Ordinal))
        {
            lines[i] = after == "/dev/null" ? "+++ /dev/null" : $"+++ head/{relative}";
        }
    }

    return string.Join('\n', lines);
}

// The comment shows hunks only; the file is already named in the <summary>.
static string StripHeader(string patch)
{
    int hunk = patch.IndexOf("\n@@", StringComparison.Ordinal);
    return hunk < 0 ? patch : patch[(hunk + 1)..];
}

internal sealed record FileChange(string Path, string Status, int Added, int Removed, string Patch);
