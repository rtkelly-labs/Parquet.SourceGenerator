#:package CliWrap@3.7.1

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;

// -----------------------------------------------------------------------------
// BuildLakeParquet.cs
//
// Builds the Lake.Parquet package (the third generator pattern, lake/README.md) from the parquet-dotnet fork. The
// fork stays upstream Parquet.Net plus its review units; the Lake identity is applied here, the same way every
// time:
//
//   1. a fresh checkout of exactly the pinned fork commit (lake/fork.json), or --ref <branch|commit>, or the HEAD
//      of a local --source checkout, in --work (default temp/lake/parquet-dotnet);
//   2. the upstream base the fork records in docs/lake/upstream-base ("<commit> <Parquet.Net version>");
//   3. the overlay (lake/overlay): Lake.Parquet.targets imported as the last line of Parquet.csproj, created_by
//      renamed in ThriftFooter.cs, Globals.cs stamped, the package README beside the project. Every edit must
//      match exactly once: a fork change that moves the code fails here instead of changing the package;
//   4. with --test, the fork's test suite on the renamed build (net10.0);
//   5. the package, into --output (default artifacts/lake), version from the first heading of
//      lake/release-notes.md unless --version is given;
//   6. checks: one Lake.Parquet.<version>.nupkg whose nuspec id and version match, holding
//      lib/net10.0/Lake.Parquet.dll with that assembly name. Prints the provenance and writes it to
//      <output>/lake-build.json.
//
// Usage:
//   dotnet run scripts/BuildLakeParquet.cs -- [--ref <branch|commit> | --source <dir>] [--test]
//                                             [--version <v>] [--work <dir>] [--output <dir>]
// -----------------------------------------------------------------------------

string repoRoot = Directory.GetCurrentDirectory();
string lakeDir = Path.Combine(repoRoot, "lake");
if (!File.Exists(Path.Combine(lakeDir, "fork.json")))
{
    Console.Error.WriteLine("::error::Run from the repository root (lake/fork.json not found).");
    return 1;
}

string? source = Option(args, "--source");
string? refOverride = Option(args, "--ref");
bool runTests = args.Contains("--test");
string work = Path.GetFullPath(
    Option(args, "--work") ?? Path.Combine("temp", "lake", "parquet-dotnet")
);
string output = Path.GetFullPath(Option(args, "--output") ?? Path.Combine("artifacts", "lake"));
string version =
    Option(args, "--version") ?? ReleaseNotesVersion(Path.Combine(lakeDir, "release-notes.md"));

using JsonDocument forkJson = JsonDocument.Parse(
    File.ReadAllText(Path.Combine(lakeDir, "fork.json"))
);
string repository = forkJson.RootElement.GetProperty("repository").GetString()!;
string pinned = forkJson.RootElement.GetProperty("commit").GetString()!;

// 1. a fresh checkout of one commit
if (Directory.Exists(work))
{
    Directory.Delete(work, recursive: true);
}
Directory.CreateDirectory(work);

// The checkout sits under this repository (temp/ by default), where MSBuild would pick up this repository's
// Directory.Build.props and central package versions. These files beside it stop that search: the fork builds
// with its own settings only, as it does in its own repository.
string isolation = Path.GetDirectoryName(work)!;
File.WriteAllText(Path.Combine(isolation, "Directory.Build.props"), "<Project />\n");
File.WriteAllText(Path.Combine(isolation, "Directory.Build.targets"), "<Project />\n");
File.WriteAllText(
    Path.Combine(isolation, "Directory.Packages.props"),
    "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n"
);
string from = source is not null ? Path.GetFullPath(source) : repository;
string wanted = source is not null ? (refOverride ?? "HEAD") : (refOverride ?? pinned);
if (source is not null)
{
    // a local checkout: its committed state at the ref, never its working tree
    wanted = (await Git(from, "rev-parse", wanted + "^{commit}")).Trim();
}
await Git(work, "init", "--quiet");
await Git(work, "remote", "add", "origin", from);
await Git(work, "fetch", "--quiet", "--depth", "1", "origin", wanted);
await Git(work, "checkout", "--quiet", "--detach", "FETCH_HEAD");
string forkCommit = (await Git(work, "rev-parse", "HEAD")).Trim();
Console.WriteLine(
    $"fork: {from} @ {forkCommit}{(forkCommit == pinned ? " (pinned)" : " (NOT the pinned commit)")}"
);

// 2. the upstream base
string baseFile = Path.Combine(work, "docs", "lake", "upstream-base");
if (!File.Exists(baseFile))
{
    return Fail(
        $"{forkCommit} has no docs/lake/upstream-base: not a commit of the fork this build expects."
    );
}
string[] baseParts = File.ReadAllText(baseFile)
    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
if (baseParts.Length != 2 || !Regex.IsMatch(baseParts[0], "^[0-9a-f]{40}$"))
{
    return Fail(
        $"docs/lake/upstream-base should be '<40-hex commit> <version>', is '{File.ReadAllText(baseFile).Trim()}'."
    );
}
(string upstreamCommit, string upstreamVersion) = (baseParts[0], baseParts[1]);
Console.WriteLine($"upstream base: Parquet.Net {upstreamVersion} @ {upstreamCommit}");

// 3. the overlay
string project = Path.Combine(work, "src", "Parquet");
File.Copy(
    Path.Combine(lakeDir, "overlay", "Lake.Parquet.targets"),
    Path.Combine(project, "Lake.Parquet.targets")
);
Directory.CreateDirectory(Path.Combine(project, "lake"));
File.Copy(
    Path.Combine(lakeDir, "overlay", "README.md"),
    Path.Combine(project, "lake", "README.md")
);

if (
    !Edit(
        Path.Combine(project, "Parquet.csproj"),
        @"<PackageId>Parquet\.Net</PackageId>",
        m => m.Value,
        "the upstream package id"
    )
    || !Edit(
        Path.Combine(project, "Parquet.csproj"),
        @"</Project>\s*$",
        _ => "    <Import Project=\"Lake.Parquet.targets\" />\n</Project>\n",
        "the project's closing tag"
    )
    || !Edit(
        Path.Combine(project, "File", "ThriftFooter.cs"),
        @"""Parquet\.Net version ",
        _ => "\"Lake.Parquet version ",
        "created_by",
        expected: 2
    )
    || !Edit(
        Path.Combine(project, "Globals.cs"),
        @"\$\{VERSION\}",
        _ => version,
        "the version placeholder"
    )
    || !Edit(
        Path.Combine(project, "Globals.cs"),
        @"\$\{GITHUB_SHA\}",
        _ => forkCommit,
        "the commit placeholder"
    )
)
{
    return 1;
}

string[] properties =
[
    $"-p:Version={version}",
    $"-p:FileVersion={version.Split('-')[0]}.0",
    $"-p:AssemblyVersion={version.Split('.')[0]}.0.0.0",
    $"-p:LakeForkCommit={forkCommit}",
    $"-p:LakeUpstreamCommit={upstreamCommit}",
    $"-p:LakeUpstreamVersion={upstreamVersion}",
];

// 4. the fork's tests on the renamed build
if (runTests)
{
    // Lake.Parquet is net10.0 only; the fork's test projects also target net8.0, so they build for net10.0 alone
    await Run(
        "dotnet",
        [
            "test",
            Path.Combine(work, "src", "Parquet.Test"),
            "-c",
            "Release",
            "-p:TargetFrameworks=net10.0",
            .. properties,
        ],
        work
    );
}

// 5. the package (upstream's project packs on a Release build)
await Run(
    "dotnet",
    ["build", Path.Combine(project, "Parquet.csproj"), "-c", "Release", .. properties],
    work
);
Directory.CreateDirectory(output);
string[] built = Directory.GetFiles(
    Path.Combine(project, "bin", "Release"),
    $"Lake.Parquet.{version}.*nupkg"
);
foreach (string file in built)
{
    File.Copy(file, Path.Combine(output, Path.GetFileName(file)), overwrite: true);
}

// 6. checks
string nupkg = Path.Combine(output, $"Lake.Parquet.{version}.nupkg");
if (!File.Exists(nupkg))
{
    return Fail(
        $"{Path.GetFileName(nupkg)} was not produced (found: {string.Join(", ", built.Select(Path.GetFileName))})."
    );
}
using (ZipArchive zip = ZipFile.OpenRead(nupkg))
{
    XDocument nuspec = XDocument.Load(
        zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open()
    );
    XNamespace ns = nuspec.Root!.Name.Namespace;
    string? id = nuspec.Root.Element(ns + "metadata")?.Element(ns + "id")?.Value;
    string? packed = nuspec.Root.Element(ns + "metadata")?.Element(ns + "version")?.Value;
    if (id != "Lake.Parquet" || packed != version)
    {
        return Fail($"nuspec says {id} {packed}, expected Lake.Parquet {version}.");
    }
    ZipArchiveEntry? dll = zip.GetEntry("lib/net10.0/Lake.Parquet.dll");
    if (dll is null)
    {
        return Fail("the package has no lib/net10.0/Lake.Parquet.dll.");
    }
    string extracted = Path.Combine(work, "check-Lake.Parquet.dll");
    dll.ExtractToFile(extracted, overwrite: true);
    string? assemblyName = AssemblyName.GetAssemblyName(extracted).Name;
    if (assemblyName != "Lake.Parquet")
    {
        return Fail($"lib/net10.0/Lake.Parquet.dll is assembly '{assemblyName}'.");
    }
}

// JsonObject, not an anonymous type: file-based apps run without reflection-based serialization
string provenance = new JsonObject
{
    ["package"] = "Lake.Parquet",
    ["version"] = version,
    ["forkRepository"] = from,
    ["forkCommit"] = forkCommit,
    ["pinned"] = forkCommit == pinned,
    ["upstreamCommit"] = upstreamCommit,
    ["upstreamVersion"] = upstreamVersion,
}.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "lake-build.json"), provenance + "\n");
Console.WriteLine(provenance);
return 0;

static string? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string ReleaseNotesVersion(string path)
{
    string first = File.ReadLines(path).First(l => l.StartsWith("# ", StringComparison.Ordinal));
    return first[2..].Trim();
}

static int Fail(string message)
{
    Console.Error.WriteLine($"::error::{message}");
    return 1;
}

// One regex edit of a file of the checkout, which must match exactly `expected` times.
static bool Edit(
    string path,
    string pattern,
    Func<Match, string> replacement,
    string what,
    int expected = 1
)
{
    string text = File.ReadAllText(path);
    int count = Regex.Matches(text, pattern).Count;
    if (count != expected)
    {
        Console.Error.WriteLine(
            $"::error::{Path.GetFileName(path)}: expected {expected} match(es) for {what}, found {count}. The fork changed this code; update the overlay in scripts/BuildLakeParquet.cs."
        );
        return false;
    }
    File.WriteAllText(path, Regex.Replace(text, pattern, m => replacement(m)));
    return true;
}

static async Task<string> Git(string directory, params string[] arguments)
{
    BufferedCommandResult result = await Cli.Wrap("git")
        .WithArguments(arguments)
        .WithWorkingDirectory(directory)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"git {string.Join(' ', arguments)} failed in {directory}:\n{result.StandardError}"
        );
    }
    return result.StandardOutput;
}

static async Task Run(string program, string[] arguments, string directory)
{
    Console.WriteLine($"> {program} {string.Join(' ', arguments)}");
    CommandResult result = await Cli.Wrap(program)
        .WithArguments(arguments)
        .WithWorkingDirectory(directory)
        .WithStandardOutputPipe(PipeTarget.ToStream(Console.OpenStandardOutput()))
        .WithStandardErrorPipe(PipeTarget.ToStream(Console.OpenStandardError()))
        .WithValidation(CommandResultValidation.None)
        .ExecuteAsync();
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException($"{program} exited with {result.ExitCode}");
    }
}
