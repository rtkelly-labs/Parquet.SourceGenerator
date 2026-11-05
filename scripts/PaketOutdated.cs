#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

#:package Paket.Core@11.0.1

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.FSharp.Core;

// -----------------------------------------------------------------------------
// PaketOutdated.cs
//
// The weekly Paket outdated report (ADR 0004, docs/architecture/adrs/0004-paket-pilot.md), read
// through Paket.Core's API rather than by parsing `paket outdated` output:
//
//   * Inside the ranges: every update `dotnet tool run paket update` would take (Paket's strict
//     FindOutdated). This drives the tracking issue.
//   * Outside the ranges: packages declared in paket.dependencies, the transitive floors among
//     them, whose newest version is outside their range. Undeclared (transitive) packages are left
//     out: they follow their parents, and taking one is never a decision on its own. A package
//     whose newest version is inside its range is listed only in the first section.
//
//   dotnet run scripts/PaketOutdated.cs -- --root <repo> --out <report.md> --verdict <file>
//       [--results <results.json>]
//
// --verdict receives `true` when anything inside the ranges is outdated, else `false`. Any failure
// (Paket throwing, a missing paket.dependencies) exits non-zero and writes no verdict, so the
// workflow cannot read a failed run as "nothing outdated". --results replaces the Paket queries
// with a JSON file of the same data, for the gate tests.
//
// Paket.Core's version above must match the paket tool in .config/dotnet-tools.json
// (PaketOutdatedScriptTests checks it): both read the same paket.lock format.
// -----------------------------------------------------------------------------

string root = ".";
string? outPath = null;
string? verdictPath = null;
string? resultsPath = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root":
            root = args[++i];
            break;
        case "--out":
            outPath = args[++i];
            break;
        case "--verdict":
            verdictPath = args[++i];
            break;
        case "--results":
            resultsPath = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (outPath is null || verdictPath is null)
{
    Console.Error.WriteLine(
        "Usage: PaketOutdated.cs --root <repo> --out <report.md> --verdict <file> [--results <json>]"
    );
    return 2;
}

Results results = resultsPath is null ? Query(root) : Load(resultsPath);

var declared = results.Declared.Select(d => Key(d.Group, d.Name)).ToHashSet();
var inRange = results.InRange.ToDictionary(u => Key(u.Group, u.Name));
var outside = results
    .AnyVersion.Where(u => declared.Contains(Key(u.Group, u.Name)))
    .Where(u =>
        !inRange.TryGetValue(Key(u.Group, u.Name), out Update? taken) || taken.Latest != u.Latest
    )
    .ToList();

var report = new StringBuilder();
report.AppendLine("## Inside the ranges in paket.dependencies");
report.AppendLine();
if (results.InRange.Count == 0)
{
    report.AppendLine("Nothing to take.");
}
else
{
    report.AppendLine(
        "Take these with `dotnet tool run paket update <package>` (or `dotnet tool run paket update` for all), then commit paket.lock."
    );
    report.AppendLine();
    AppendTable(report, results.InRange);
}

report.AppendLine();
report.AppendLine("## Outside the ranges (information only)");
report.AppendLine();
report.AppendLine(
    "Declared packages only, the transitive floors included. Taking one means widening its range in paket.dependencies on purpose."
);
report.AppendLine();
if (outside.Count == 0)
{
    report.AppendLine("None.");
}
else
{
    AppendTable(report, outside);
}

foreach (string path in new[] { outPath, verdictPath })
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
}

File.WriteAllText(outPath, report.ToString());
File.WriteAllText(verdictPath, results.InRange.Count > 0 ? "true" : "false");
Console.Write(report);
return 0;

static string Key(string group, string name) =>
    $"{group.ToUpperInvariant()}/{name.ToUpperInvariant()}";

static void AppendTable(StringBuilder report, IEnumerable<Update> updates)
{
    report.AppendLine("| Group | Package | Locked | Newest |");
    report.AppendLine("|:--|:--|:--|:--|");
    foreach (Update u in updates.OrderBy(u => u.Group).ThenBy(u => u.Name))
    {
        report.AppendLine($"| {u.Group} | {u.Name} | {u.Locked} | {u.Latest} |");
    }
}

static Results Query(string root)
{
    string dependencies = Path.Combine(Path.GetFullPath(root), "paket.dependencies");
    if (!File.Exists(dependencies))
    {
        throw new FileNotFoundException("No paket.dependencies to report on.", dependencies);
    }

    Paket.Dependencies paket = Paket.Dependencies.Locate(dependencies);
    var declared = paket
        .GetDirectDependencies()
        .Select(d => new Declared(d.Item1, d.Item2))
        .ToList();
    return new Results(declared, Outdated(paket, strict: true), Outdated(paket, strict: false));
}

static List<Update> Outdated(Paket.Dependencies paket, bool strict) =>
    paket
        .FindOutdated(strict, force: false, includePrereleases: false, FSharpOption<string>.None)
        .Select(o => new Update(
            o.Item1,
            o.Item2,
            paket.GetInstalledVersion(FSharpOption<string>.Some(o.Item1), o.Item2)?.Value ?? "?",
            o.Item3.ToString()
        ))
        .ToList();

static Results Load(string path)
{
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
    JsonElement json = doc.RootElement;
    return new Results(
        json.GetProperty("declared")
            .EnumerateArray()
            .Select(d => new Declared(Text(d, "group"), Text(d, "name")))
            .ToList(),
        Updates(json.GetProperty("inRange")),
        Updates(json.GetProperty("anyVersion"))
    );

    static List<Update> Updates(JsonElement list) =>
        list.EnumerateArray()
            .Select(u => new Update(
                Text(u, "group"),
                Text(u, "name"),
                Text(u, "locked"),
                Text(u, "latest")
            ))
            .ToList();

    static string Text(JsonElement e, string name) =>
        e.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"'{name}' is null in {e}");
}

record Declared(string Group, string Name);

record Update(string Group, string Name, string Locked, string Latest);

record Results(List<Declared> Declared, List<Update> InRange, List<Update> AnyVersion);
