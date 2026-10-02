using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

// -----------------------------------------------------------------------------
// VerifyPackageLayout.cs
//
// Compares the exact entry list of every packed .nupkg with a committed manifest (#400). The
// earlier gate named the entries it expected and one prefix it did not want, so a new
// `build/*.targets`, `buildTransitive/` folder or `tools/` script (all executed by the consumer's
// MSBuild or shell) passed it. Here anything not in the manifest fails by default.
//
// One manifest per package id, `<id>.txt`, in --manifests (default .github/package-layout):
//   - one entry per line, forward slashes, exactly as `unzip -Z1` lists it;
//   - `@dependency <id>` requires the nuspec to declare that package dependency;
//   - blank lines and `#` comments are ignored.
// Entry names carry no version in these packages. The one generated name, the
// `package/services/metadata/core-properties/<guid>.psmdcp` part, is matched as `*.psmdcp`.
//
// The gate fails when it would examine nothing: no package in --packages, a package without a
// manifest, a manifest without a package, or an empty manifest.
//
// Usage:
//   dotnet run scripts/VerifyPackageLayout.cs -- --packages artifacts [--manifests <dir>]
//                                                [--version <expected package version>]
// -----------------------------------------------------------------------------

string packagesDir = Option(args, "--packages") ?? "artifacts";
string manifestsDir = Option(args, "--manifests") ?? ".github/package-layout";
string? expectedVersion = Option(args, "--version");

int failures = 0;

void Fail(string message)
{
    failures++;
    Console.Error.WriteLine($"::error::{message}");
}

if (!Directory.Exists(packagesDir))
{
    Console.Error.WriteLine($"::error::Packages directory '{packagesDir}' does not exist.");
    return 1;
}

if (!Directory.Exists(manifestsDir))
{
    Console.Error.WriteLine($"::error::Manifest directory '{manifestsDir}' does not exist.");
    return 1;
}

string[] packages = Directory
    .GetFiles(packagesDir, "*.nupkg")
    .Where(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.Ordinal)
    .ToArray();
string[] manifests = Directory
    .GetFiles(manifestsDir, "*.txt")
    .OrderBy(path => path, StringComparer.Ordinal)
    .ToArray();

if (packages.Length == 0)
{
    Console.Error.WriteLine(
        $"::error::No .nupkg in '{packagesDir}': the layout gate examined nothing."
    );
    return 1;
}

if (manifests.Length == 0)
{
    Console.Error.WriteLine($"::error::No manifest in '{manifestsDir}'.");
    return 1;
}

var seenIds = new HashSet<string>(StringComparer.Ordinal);
foreach (string package in packages)
{
    using ZipArchive archive = ZipFile.OpenRead(package);
    string[] entries = archive
        .Entries.Select(entry => Normalise(entry.FullName))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    ZipArchiveEntry? nuspecEntry = archive.Entries.FirstOrDefault(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)
        && !entry.FullName.Contains('/')
    );
    if (nuspecEntry is null)
    {
        Fail($"{Path.GetFileName(package)} has no root .nuspec.");
        continue;
    }

    string nuspec;
    using (var reader = new StreamReader(nuspecEntry.Open()))
    {
        nuspec = reader.ReadToEnd();
    }

    string? id = Regex.Match(nuspec, @"<id>\s*([^<\s]+)\s*</id>").Groups[1].Value;
    string version = Regex.Match(nuspec, @"<version>\s*([^<\s]+)\s*</version>").Groups[1].Value;
    if (string.IsNullOrEmpty(id))
    {
        Fail($"{Path.GetFileName(package)}: nuspec declares no id.");
        continue;
    }

    seenIds.Add(id);
    Console.WriteLine($"::group::{id} {version}: {entries.Length} entries");
    foreach (string entry in entries)
    {
        Console.WriteLine(entry);
    }
    Console.WriteLine("::endgroup::");

    if (expectedVersion is not null && version != expectedVersion)
    {
        Fail($"{id}: package version is '{version}', expected '{expectedVersion}'.");
    }

    string manifestPath = Path.Combine(manifestsDir, id + ".txt");
    if (!File.Exists(manifestPath))
    {
        Fail(
            $"{id}: no manifest at {manifestPath}. A new package must add its expected entry list "
                + "deliberately."
        );
        continue;
    }

    var expected = new List<string>();
    var dependencies = new List<string>();
    foreach (string raw in File.ReadAllLines(manifestPath))
    {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            continue;
        }

        if (line.StartsWith("@dependency ", StringComparison.Ordinal))
        {
            dependencies.Add(line.Substring("@dependency ".Length).Trim());
        }
        else
        {
            expected.Add(line);
        }
    }

    if (expected.Count == 0)
    {
        Fail($"{manifestPath} lists no entries; an empty manifest would accept an empty package.");
        continue;
    }

    string[] unexpected = entries.Except(expected, StringComparer.Ordinal).ToArray();
    string[] missing = expected.Except(entries, StringComparer.Ordinal).ToArray();
    foreach (string entry in unexpected)
    {
        Fail(
            $"{id}: unexpected package entry '{entry}'. Packed content is executed or consumed by "
                + $"every consumer; if it is intended, add it to {manifestPath} in the same pull request."
        );
    }

    foreach (string entry in missing)
    {
        Fail($"{id}: expected package entry '{entry}' is missing.");
    }

    foreach (string dependency in dependencies)
    {
        if (!Regex.IsMatch(nuspec, $"<dependency[^>]*\\bid=\"{Regex.Escape(dependency)}\""))
        {
            Fail($"{id}: nuspec does not declare a dependency on {dependency}.");
        }
    }
}

foreach (string manifest in manifests)
{
    string manifestId = Path.GetFileNameWithoutExtension(manifest);
    if (!seenIds.Contains(manifestId))
    {
        Fail(
            $"Manifest {manifest} has no package in '{packagesDir}'; a package was dropped or "
                + "renamed without updating its manifest."
        );
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"Package layout FAILED: {failures} problem(s).");
    return 1;
}

Console.WriteLine($"Package layout OK: {seenIds.Count} package(s) match their manifests exactly.");
return 0;

static string Normalise(string entry) =>
    Regex.Replace(
        entry.Replace('\\', '/'),
        @"^package/services/metadata/core-properties/[^/]+\.psmdcp$",
        "package/services/metadata/core-properties/*.psmdcp"
    );

static string? Option(string[] arguments, string option)
{
    int index = Array.IndexOf(arguments, option);
    return index < 0 || index + 1 >= arguments.Length ? null : arguments[index + 1];
}
