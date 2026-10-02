using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Positive controls for <c>scripts/VerifyPackageLayout.cs</c> (#400): the gate compares the exact
/// entry list of each package with a committed manifest, so content nobody listed (a new
/// <c>build/*.targets</c>, <c>buildTransitive/</c> or <c>tools/</c> entry, all executed on the
/// consumer's machine) fails by default.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackageLayoutGateTests : IDisposable
{
    private const string Id = "Acme.Generator";
    private static readonly string[] BaseEntries =
    [
        "Acme.Generator.nuspec",
        "[Content_Types].xml",
        "_rels/.rels",
        "analyzers/dotnet/cs/Acme.Generator.dll",
        "package/services/metadata/core-properties/0123abcd.psmdcp",
    ];

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "parquet-package-gate-" + Guid.NewGuid().ToString("N")
    );

    private string Packages => Path.Combine(_dir, "packages");
    private string Manifests => Path.Combine(_dir, "manifests");

    public PackageLayoutGateTests()
    {
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Manifests);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackageWhoseEntriesMatchTheManifestPassesAsync()
    {
        WritePackage(Id, BaseEntries);
        WriteManifest(Id, ManifestFor(BaseEntries), "@dependency Acme.Attributes");

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(0, result.Describe());
        result.Stdout.ShouldContain("Package layout OK: 1 package(s)");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackageWithAnEntryTheManifestDoesNotListFailsAsync()
    {
        WritePackage(Id, [.. BaseEntries, "build/Acme.Generator.targets"]);
        WriteManifest(Id, ManifestFor(BaseEntries), "@dependency Acme.Attributes");

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("unexpected package entry 'build/Acme.Generator.targets'");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackageMissingAnEntryTheManifestListsFailsAsync()
    {
        WritePackage(Id, BaseEntries);
        WriteManifest(
            Id,
            [.. ManifestFor(BaseEntries), "lib/net8.0/Acme.Generator.dll"],
            "@dependency Acme.Attributes"
        );

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain(
            "expected package entry 'lib/net8.0/Acme.Generator.dll' is missing"
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackageWithoutAManifestFailsAsync()
    {
        WritePackage(Id, BaseEntries);
        WriteManifest("Some.Other", ManifestFor(BaseEntries));

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain($"{Id}: no manifest");
        result.Stderr.ShouldContain("Manifest");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManifestWithoutAPackageFailsAsync()
    {
        WritePackage(Id, BaseEntries);
        WriteManifest(Id, ManifestFor(BaseEntries), "@dependency Acme.Attributes");
        WriteManifest("Dropped.Package", ["a.txt"]);

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("Dropped.Package.txt has no package");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NuspecWithoutTheDeclaredDependencyFailsAsync()
    {
        WritePackage(Id, BaseEntries, dependencyId: null);
        WriteManifest(Id, ManifestFor(BaseEntries), "@dependency Acme.Attributes");

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("does not declare a dependency on Acme.Attributes");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EmptyPackageDirectoryFailsBecauseItExaminedNothingAsync()
    {
        WriteManifest(Id, ManifestFor(BaseEntries));

        ScriptResult result = await RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("examined nothing");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PackageVersionOtherThanTheRequestedOneFailsAsync()
    {
        WritePackage(Id, BaseEntries);
        WriteManifest(Id, ManifestFor(BaseEntries), "@dependency Acme.Attributes");

        ScriptResult result = await RunAsync("--version", "9.9.9");

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("package version is '1.2.3', expected '9.9.9'");
    }

    [Fact]
    public void EveryCommittedManifestListsEntriesAndTheGeneratorsDeclareTheirDependency()
    {
        string manifests = Path.Combine(FindRepoRoot(), ".github", "package-layout");
        string[] files = Directory.GetFiles(manifests, "*.txt");

        files
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe([
                "Parquet.SourceGenerator",
                "Parquet.SourceGenerator.Attributes",
                "Parquet.SourceGenerator.Legacy",
            ]);

        foreach (string file in files)
        {
            string[] lines = IOFile.ReadAllLines(file);
            string name = Path.GetFileNameWithoutExtension(file)!;
            lines
                .Count(l => l.Trim().Length > 0 && !l.StartsWith('#') && !l.StartsWith('@'))
                .ShouldBeGreaterThan(5, $"{name}: manifest lists too few entries");
            lines
                .Any(l => l.Contains("analyzers/dotnet/cs/", StringComparison.Ordinal))
                .ShouldBe(name != "Parquet.SourceGenerator.Attributes", name);
            lines
                .Any(l => l == "@dependency Parquet.SourceGenerator.Attributes")
                .ShouldBe(name != "Parquet.SourceGenerator.Attributes", name);
        }
    }

    private static string[] ManifestFor(string[] entries) =>
        [
            .. entries.Select(e =>
                e.EndsWith(".psmdcp", StringComparison.Ordinal)
                    ? "package/services/metadata/core-properties/*.psmdcp"
                    : e
            ),
        ];

    private void WriteManifest(string id, string[] lines, params string[] directives) =>
        IOFile.WriteAllLines(Path.Combine(Manifests, id + ".txt"), [.. directives, .. lines]);

    private void WritePackage(string id, string[] entries, string? dependencyId = "Acme.Attributes")
    {
        string path = Path.Combine(Packages, $"{id}.1.2.3.nupkg");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string entry in entries)
        {
            ZipArchiveEntry created = archive.CreateEntry(entry);
            using var writer = new StreamWriter(created.Open());
            if (entry.EndsWith(".nuspec", StringComparison.Ordinal))
            {
                string dependency = dependencyId is null
                    ? string.Empty
                    : $"<dependencies><dependency id=\"{dependencyId}\" version=\"1.2.3\" /></dependencies>";
                writer.Write(
                    $"<package><metadata><id>{id}</id><version>1.2.3</version>{dependency}</metadata></package>"
                );
            }
            else
            {
                writer.Write("x");
            }
        }
    }

    private async Task<ScriptResult> RunAsync(params string[] extraArguments)
    {
        string repoRoot = FindRepoRoot();
        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");

        var psi = new ProcessStartInfo
        {
            FileName = IOFile.Exists(homeDotnet) ? homeDotnet : "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            string argument in new[]
            {
                "run",
                "scripts/VerifyPackageLayout.cs",
                "--",
                "--packages",
                Packages,
                "--manifests",
                Manifests,
            }.Concat(extraArguments)
        )
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        psi.Environment["DOTNET_BUILD_SERVER_DISABLE"] = "1";

        using Process? process = Process.Start(psi);
        process.ShouldNotBeNull();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (IOFile.Exists(Path.Combine(dir.FullName, "scripts", "VerifyPackageLayout.cs")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find repository root containing scripts/VerifyPackageLayout.cs"
        );
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Describe() => $"Stdout:\n{Stdout}\nStderr:\n{Stderr}";
    }
}
