using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

// -----------------------------------------------------------------------------
// CheckApiLedger.cs
//
// The CI half of the API change contract (docs/18-API-CHANGE-CONTRACT.md). Two rules, both of
// which need a diff and therefore cannot live in an analyzer:
//
//   1. A pull request that ADDS a line to a governed catalogue must also ADD an entry to
//      docs/api/LEDGER.md. The catalogues are the emitted-API `*.api.txt` baselines,
//      `src/api/seams.txt`, and every `PublicAPI.Unshipped.txt`. Build-time gates PARQAPI001 and
//      PARQAPI002 make sure a member cannot exist outside a catalogue; this makes sure a catalogue
//      line cannot exist without a recorded semver decision.
//
//   2. The escape hatch is rejected here. A ledger entry marked `**Unapproved-by-design:**`
//      suppresses the build errors so a spike can compile, and this workflow only ever runs on
//      `main` or on a pull request targeting it — so finding the marker means the branch is trying
//      to merge an unreviewed surface, and it fails.
//
// Usage:
//   dotnet run scripts/CheckApiLedger.cs                 # base ref from GITHUB_BASE_REF
//   dotnet run scripts/CheckApiLedger.cs -- --base main
// -----------------------------------------------------------------------------

const string LedgerPath = "docs/api/LEDGER.md";
const string UnapprovedMarker = "**Unapproved-by-design:**";

string[] catalogueGlobs =
{
    "*.api.txt",
    "src/api/seams.txt",
    "PublicAPI.Unshipped.txt",
    "**/PublicAPI.Unshipped.txt",
};

Directory.SetCurrentDirectory(FindRepoRoot());

int failures = 0;

// ---------------------------------------------------------------------------
// Rule 2 first: it needs no diff, so it reports even when the base ref is unavailable.
// ---------------------------------------------------------------------------
if (File.Exists(LedgerPath))
{
    var unapproved = new List<string>();
    string currentHeading = string.Empty;
    foreach (string line in File.ReadAllLines(LedgerPath))
    {
        if (line.StartsWith("#", StringComparison.Ordinal))
        {
            currentHeading = line.TrimStart('#', ' ').Trim();
        }
        else if (line.Contains(UnapprovedMarker, StringComparison.Ordinal))
        {
            unapproved.Add(currentHeading.Length > 0 ? currentHeading : line.Trim());
        }
    }

    if (unapproved.Count > 0)
    {
        failures++;
        Console.Error.WriteLine(
            $"::error file={LedgerPath}::{LedgerPath} carries {unapproved.Count} "
                + $"'{UnapprovedMarker}' entr{(unapproved.Count == 1 ? "y" : "ies")}, which cannot merge to main."
        );
        foreach (string heading in unapproved)
        {
            Console.Error.WriteLine($"    - {heading}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "  The marker exists so a spike or experiment branch can compile past PARQAPI001 and"
        );
        Console.Error.WriteLine(
            "  PARQAPI002 without writing a rationale it does not yet have. It is not a merge path."
        );
        Console.Error.WriteLine("  To land the change: either");
        Console.Error.WriteLine(
            "    (a) replace the marker with a real **Semver:** bucket and **Rationale:**, and add"
        );
        Console.Error.WriteLine(
            "        the member to its catalogue (the .api.txt baseline or src/api/seams.txt); or"
        );
        Console.Error.WriteLine(
            "    (b) revert the member and drop the ledger entry, keeping the spike off main."
        );
        Console.Error.WriteLine();
    }
}

// ---------------------------------------------------------------------------
// Rule 1: catalogue additions require ledger additions.
// ---------------------------------------------------------------------------
string? range = ResolveBaseRange(args);
if (range is null)
{
    Console.WriteLine(
        "No base ref available (not a pull request, and no --base given): skipping the "
            + "catalogue-vs-ledger diff check."
    );
    return failures == 0 ? 0 : 1;
}

Console.WriteLine($"Comparing catalogues against {range}");

var addedCatalogueLines = new List<(string File, string Line)>();
var removedCatalogueLines = new List<(string File, string Line)>();
foreach (string pathspec in catalogueGlobs)
{
    string diff = Git($"diff --unified=0 --no-color {range} -- \"{pathspec}\"");
    string currentFile = string.Empty;
    foreach (string line in diff.Split('\n'))
    {
        if (line.StartsWith("+++ b/", StringComparison.Ordinal))
        {
            currentFile = line.Substring("+++ b/".Length).Trim();
        }
        else if (line.StartsWith("--- a/", StringComparison.Ordinal) && currentFile.Length == 0)
        {
            currentFile = line.Substring("--- a/".Length).Trim();
        }
        else if (
            line.StartsWith("+", StringComparison.Ordinal)
            && !line.StartsWith("+++", StringComparison.Ordinal)
        )
        {
            string content = line.Substring(1).Trim();

            // Blank lines, the `#nullable enable` header and catalogue comments are not signatures.
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            addedCatalogueLines.Add((currentFile, content));
        }
        else if (
            line.StartsWith("-", StringComparison.Ordinal)
            && !line.StartsWith("---", StringComparison.Ordinal)
        )
        {
            string content = line.Substring(1).Trim();

            // Blank lines, the `#nullable enable` header and catalogue comments are not signatures.
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            removedCatalogueLines.Add((currentFile, content));
        }
    }
}

// De-duplicate: PublicAPI.Unshipped.txt is matched by two pathspecs.
addedCatalogueLines = addedCatalogueLines
    .Distinct()
    .OrderBy(entry => entry.File, StringComparer.Ordinal)
    .ThenBy(entry => entry.Line, StringComparer.Ordinal)
    .ToList();

removedCatalogueLines = removedCatalogueLines
    .Distinct()
    .OrderBy(entry => entry.File, StringComparer.Ordinal)
    .ThenBy(entry => entry.Line, StringComparer.Ordinal)
    .ToList();

if (addedCatalogueLines.Count == 0 && removedCatalogueLines.Count == 0)
{
    Console.WriteLine("No governed catalogue gained or lost a signature in this change.");
    return failures == 0 ? 0 : 1;
}

int addedLedgerEntries = Git($"diff --unified=0 --no-color {range} -- \"{LedgerPath}\"")
    .Split('\n')
    .Count(line => line.StartsWith("+### ", StringComparison.Ordinal));

Console.WriteLine(
    $"{addedCatalogueLines.Count} signature(s) added, {removedCatalogueLines.Count} signature(s) removed; "
        + $"{addedLedgerEntries} ledger entr{(addedLedgerEntries == 1 ? "y" : "ies")} added."
);

if (addedLedgerEntries == 0)
{
    failures++;
    int totalChanged = addedCatalogueLines.Count + removedCatalogueLines.Count;
    Console.Error.WriteLine(
        $"::error file={LedgerPath}::{totalChanged} signature(s) changed in a "
            + $"governed API catalogue with no new entry in {LedgerPath}."
    );
    foreach ((string file, string line) in addedCatalogueLines.Take(15))
    {
        Console.Error.WriteLine($"    + {file}: {line}");
    }
    foreach ((string file, string line) in removedCatalogueLines.Take(15))
    {
        Console.Error.WriteLine($"    - {file}: {line}");
    }

    if (addedCatalogueLines.Count > 25)
    {
        Console.Error.WriteLine($"    ... and {addedCatalogueLines.Count - 25} more");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine($"  Add one entry per added signature to {LedgerPath}, newest first:");
    Console.Error.WriteLine();
    Console.Error.WriteLine("    ### YYYY-MM-DD — `Member(ParamTypes)`");
    Console.Error.WriteLine("    - **Surface:** emitted | package | seam");
    Console.Error.WriteLine(
        "    - **Semver:** additive-minor | breaking-major | internal | generated-shape"
    );
    Console.Error.WriteLine("    - **Issue:** #NNN");
    Console.Error.WriteLine("    - **Rationale:** why no existing member can express this.");
    Console.Error.WriteLine("    - **Alternatives considered:** what was rejected, and why.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  The full contract is in docs/18-API-CHANGE-CONTRACT.md.");
}
else
{
    Console.WriteLine("Ledger entry present for this change.");
}

return failures == 0 ? 0 : 1;

static string? ResolveBaseRange(string[] args)
{
    string? baseRef = null;
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--base", StringComparison.Ordinal))
        {
            baseRef = args[i + 1];
            break;
        }
    }

    baseRef ??= Environment.GetEnvironmentVariable("GITHUB_BASE_REF");
    if (string.IsNullOrWhiteSpace(baseRef))
    {
        return null;
    }

    string resolved = Resolve(baseRef!);
    if (HasMergeBase(resolved))
    {
        return $"{resolved}...HEAD";
    }

    // A CI checkout is shallow on both sides: fetching the base tip is not enough, the two
    // histories still do not reach a common ancestor. Deepen before giving up — a three-dot range
    // without a merge base is a hard git error, and silently falling back would make the check
    // report additions that came from other pull requests.
    Console.WriteLine($"No merge base with {resolved} yet; deepening the checkout.");
    Run("git", "fetch --no-tags --unshallow origin");
    Run("git", $"fetch --no-tags origin +refs/heads/{baseRef}:refs/remotes/origin/{baseRef}");

    resolved = Resolve(baseRef!);
    if (HasMergeBase(resolved))
    {
        return $"{resolved}...HEAD";
    }

    // Last resort: diff the two trees directly. On a pull request this is accurate anyway, because
    // actions/checkout checks out the merge commit, which already contains the base tip.
    Console.WriteLine(
        $"Still no merge base with {resolved}; comparing the two trees directly instead."
    );
    return $"{resolved} HEAD";

    static bool HasMergeBase(string reference) =>
        Run("git", $"merge-base {reference} HEAD").ExitCode == 0;

    static string Resolve(string reference)
    {
        if (Run("git", $"rev-parse --verify --quiet {reference}^{{commit}}").ExitCode == 0)
        {
            return reference;
        }

        Run(
            "git",
            $"fetch --no-tags --depth=200 origin +refs/heads/{reference}:refs/remotes/origin/{reference}"
        );
        if (Run("git", $"rev-parse --verify --quiet origin/{reference}^{{commit}}").ExitCode == 0)
        {
            return $"origin/{reference}";
        }

        Console.Error.WriteLine(
            $"::error::Cannot resolve base ref '{reference}'; the catalogue-vs-ledger check "
                + "cannot run without it."
        );
        Environment.Exit(1);
        return reference;
    }
}

static string Git(string arguments)
{
    (int exitCode, string stdout, string stderr) = Run("git", arguments);
    if (exitCode != 0)
    {
        Console.Error.WriteLine($"::error::git {arguments} failed: {stderr}");
        Environment.Exit(1);
    }

    return stdout;
}

static (int ExitCode, string StandardOutput, string StandardError) Run(
    string fileName,
    string arguments
)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    foreach (string argument in SplitArguments(arguments))
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)!;
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
}

static IEnumerable<string> SplitArguments(string arguments)
{
    var current = new StringBuilder();
    bool quoted = false;
    foreach (char character in arguments)
    {
        if (character == '"')
        {
            quoted = !quoted;
        }
        else if (character == ' ' && !quoted)
        {
            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        else
        {
            current.Append(character);
        }
    }

    if (current.Length > 0)
    {
        yield return current.ToString();
    }
}

static string FindRepoRoot()
{
    // `git rev-parse --show-toplevel`, not a walk up looking for a `.git` directory: in a git
    // worktree `.git` is a *file*, and a walk would silently climb out of the worktree into the
    // main checkout and diff the wrong branch.
    (int exitCode, string stdout, _) = Run("git", "rev-parse --show-toplevel");
    if (exitCode != 0 || stdout.Trim().Length == 0)
    {
        Console.Error.WriteLine(
            "::error::Could not locate the repository root (not a git checkout?)."
        );
        Environment.Exit(1);
    }

    return stdout.Trim();
}
