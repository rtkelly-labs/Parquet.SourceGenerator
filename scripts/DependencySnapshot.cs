#pragma warning disable CA1305, CA1852, MA0002, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

// -----------------------------------------------------------------------------
// DependencySnapshot.cs
//
// Turns a component-detection scan manifest into a GitHub dependency submission snapshot
// (https://docs.github.com/rest/dependency-graph/dependency-submission). dependency-submission.yml
// runs a pinned component-detection release itself instead of the submission action, which
// always downloads the newest release (ADR 0004, docs/architecture/adrs/0004-paket-pilot.md).
//
// One snapshot manifest per scanned project, named by its path relative to the repository. A
// package is `direct` when the project references it explicitly, otherwise `indirect`, and lists
// the packages it depends on. A manifest that resolves no packages (global.json) is left out.
// Fails closed: a scan that resolves no packages at all, or a --require manifest that resolves
// none, exits non-zero, so an empty or partial snapshot never replaces the graph.
//
//   dotnet run scripts/DependencySnapshot.cs -- --manifest <scan.json> --out <snapshot.json>
//       --sha <commit> --ref <refs/heads/...> --job-id <id> --correlator <name>
//       --detector-version <version> [--require <relative path>]...
// -----------------------------------------------------------------------------

var options = new Dictionary<string, string>(StringComparer.Ordinal);
var required = new List<string>();
for (int i = 0; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--require")
    {
        required.Add(args[i + 1]);
    }
    else
    {
        options[args[i]] = args[i + 1];
    }
}

string[] needed =
[
    "--manifest",
    "--out",
    "--sha",
    "--ref",
    "--job-id",
    "--correlator",
    "--detector-version",
];
string[] missing = needed.Where(n => !options.ContainsKey(n)).ToArray();
if (missing.Length > 0 || args.Length % 2 != 0)
{
    Console.Error.WriteLine($"Missing or malformed arguments: {string.Join(", ", missing)}");
    return 2;
}

using JsonDocument scan = JsonDocument.Parse(File.ReadAllText(options["--manifest"]));
JsonElement scanRoot = scan.RootElement;
string resultCode = scanRoot.GetProperty("resultCode").GetString() ?? "";
if (resultCode != "Success")
{
    Console.Error.WriteLine($"::error::component-detection reported '{resultCode}', not Success");
    return 1;
}

string sourceDirectory = scanRoot.GetProperty("sourceDirectory").GetString() ?? "";

// Component id -> package URL and development flag.
var components = new Dictionary<string, (string Purl, bool Development)>(StringComparer.Ordinal);
foreach (JsonElement found in scanRoot.GetProperty("componentsFound").EnumerateArray())
{
    JsonElement component = found.GetProperty("component");
    if (
        !component.TryGetProperty("packageUrl", out JsonElement purl)
        || purl.ValueKind != JsonValueKind.Object
    )
    {
        continue;
    }

    string type = purl.GetProperty("Type").GetString() ?? "";
    string name = purl.GetProperty("Name").GetString() ?? "";
    string version = purl.GetProperty("Version").GetString() ?? "";
    if (type.Length == 0 || name.Length == 0 || version.Length == 0)
    {
        continue;
    }

    bool development =
        found.TryGetProperty("isDevelopmentDependency", out JsonElement dev)
        && dev.ValueKind == JsonValueKind.True;
    components[component.GetProperty("id").GetString()!] = (
        $"pkg:{type}/{name}@{version}",
        development
    );
}

var manifests = new JsonObject();
var problems = new List<string>();
foreach (JsonProperty graph in scanRoot.GetProperty("dependencyGraphs").EnumerateObject())
{
    string path = Path.GetRelativePath(sourceDirectory, graph.Name).Replace('\\', '/');
    var direct = new HashSet<string>(StringComparer.Ordinal);
    if (
        graph.Value.TryGetProperty("explicitlyReferencedComponentIds", out JsonElement explicitIds)
        && explicitIds.ValueKind == JsonValueKind.Array
    )
    {
        direct.UnionWith(explicitIds.EnumerateArray().Select(e => e.GetString()!));
    }

    var resolved = new JsonObject();
    foreach (JsonProperty node in graph.Value.GetProperty("graph").EnumerateObject())
    {
        if (!components.TryGetValue(node.Name, out var package))
        {
            continue;
        }

        var dependencies = new JsonArray();
        if (node.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in node.Value.EnumerateArray())
            {
                if (components.TryGetValue(child.GetString()!, out var dependency))
                {
                    dependencies.Add((JsonNode)JsonValue.Create(dependency.Purl));
                }
            }
        }

        resolved[package.Purl] = new JsonObject
        {
            ["package_url"] = package.Purl,
            ["relationship"] = direct.Contains(node.Name) ? "direct" : "indirect",
            ["scope"] = package.Development ? "development" : "runtime",
            ["dependencies"] = dependencies,
        };
    }

    // global.json (the SDK) and a project with no packages resolve nothing; leave them out.
    if (resolved.Count == 0)
    {
        continue;
    }

    manifests[path] = new JsonObject
    {
        ["name"] = path,
        ["file"] = new JsonObject { ["source_location"] = path },
        ["resolved"] = resolved,
    };
}

if (manifests.Count == 0)
{
    problems.Add("the scan resolved no packages in any project");
}

foreach (string path in required)
{
    if (!manifests.ContainsKey(path))
    {
        problems.Add($"{path}: required, but the scan resolved no packages for it");
    }
}

if (problems.Count > 0)
{
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"::error::{problem}");
    }

    return 1;
}

var snapshot = new JsonObject
{
    ["version"] = 0,
    ["sha"] = options["--sha"],
    ["ref"] = options["--ref"],
    ["job"] = new JsonObject
    {
        ["correlator"] = options["--correlator"],
        ["id"] = options["--job-id"],
    },
    ["detector"] = new JsonObject
    {
        ["name"] = "component-detection",
        ["version"] = options["--detector-version"],
        ["url"] = "https://github.com/microsoft/component-detection",
    },
    ["scanned"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    ["manifests"] = manifests,
};

File.WriteAllText(options["--out"], snapshot.ToJsonString());
Console.WriteLine(
    $"{manifests.Count} manifests, {manifests.Sum(m => ((JsonObject)m.Value!["resolved"]!).Count)} resolved packages."
);
return 0;
