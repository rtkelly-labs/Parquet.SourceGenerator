#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

// -----------------------------------------------------------------------------
// RenderDerivedDiff.cs
//
// Diffs two trees written by scripts/DerivedOutputs.cs — the pull request's merge base and its
// head — and renders the result as the Markdown body of the sticky PR comment, plus the full
// unified patch for the `derived-outputs` artifact.
//
// The comment is ordered so the root change reads first: the emitted public API (a signature
// added, removed or changed), then the emitted code, then the numbers that follow from it
// (generated-code metrics, src/ metrics, duplication, call graph). The API section is expanded;
// everything else is collapsed. GitHub caps a comment at 65,536 characters, so per-file and total
// budgets apply; anything past them is named with its line counts and left to the artifact.
//
// Usage:
//   dotnet run scripts/RenderDerivedDiff.cs -- --base <dir> --head <dir> --comment <file>
//       [--patch <file>] [--base-label <text>] [--artifact-url <url>]
// Exit codes: 0 rendered (whether or not anything changed), 2 usage.
// -----------------------------------------------------------------------------

const string Marker = "<!-- derived-review-diff -->";
const int CommentBudget = 60_000;
const int FileBudget = 12_000;

string? baseDir = null;
string? headDir = null;
string? commentPath = null;
string? patchPath = null;
string baseLabel = "base";
string? artifactUrl = null;

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
        case "--base-label":
            baseLabel = Next();
            break;
        case "--artifact-url":
            artifactUrl = Next();
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {argv[i]}");
            return 2;
    }
}

if (baseDir is null || headDir is null || commentPath is null)
{
    Console.Error.WriteLine(
        "Usage: RenderDerivedDiff.cs --base <dir> --head <dir> --comment <file> [--patch <file>] [--base-label <text>] [--artifact-url <url>]"
    );
    return 2;
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
    int added = 0;
    int removed = 0;
    foreach (string line in patch.Split('\n'))
    {
        if (
            line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
        )
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

var body = new StringBuilder();
body.Append(Marker).Append('\n');
body.Append("## Derived output: this PR vs `").Append(baseLabel).Append("`\n\n");
body.Append(
    "Emitted code, its public API, code metrics and the call graph are generated from the code on "
        + "every run and are not checked in. This is how they differ from the merge base.\n\n"
);

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
            if (diffBody.Length > FileBudget || body.Length + block.Length > CommentBudget)
            {
                omitted.Add(change);
                body.Append("- ")
                    .Append(label)
                    .Append(" — too large for a comment; see the artifact\n\n");
                continue;
            }

            body.Append(block);
        }
    }

    if (omitted.Count > 0)
    {
        body.Append(
            $"> {omitted.Count} file diff(s) were left out to fit GitHub's comment limit.\n\n"
        );
    }
}

body.Append(
    artifactUrl is null
        ? "\nBoth trees and the full patch are in the `derived-outputs` artifact of this run.\n"
        : $"\nBoth trees and the full patch are in the [`derived-outputs` artifact]({artifactUrl}).\n"
);

File.WriteAllText(commentPath, body.ToString(), new UTF8Encoding(false));
Console.WriteLine($"{changes.Count} derived file(s) differ from {baseLabel}; wrote {commentPath}.");
return 0;

string SectionOf(string relative) => sections.First(s => s.Owns(relative)).Title;

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

    // Name the file by its path inside the tree, not by the temp directory it was read from.
    var lines = output.Split('\n').ToList();
    for (int i = 0; i < lines.Count; i++)
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
