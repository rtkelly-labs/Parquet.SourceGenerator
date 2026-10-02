using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Proves the build gates report (#615). <c>RS0016</c>, <c>PARQAPI002</c> and <c>CA1502</c> block a
/// merge only through analyzers loading and <c>-warnaserror</c> being on a command line; an analyzer
/// that silently stopped loading would pass every build. A fixture project that is wrong on purpose
/// must fail with all three, and the shipping projects must keep the wiring the fixture mirrors.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AnalyzerGateTests
{
    private const string FixtureProject =
        "test/Parquet.SourceGenerator.GateFixture/Parquet.SourceGenerator.GateFixture.csproj";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASeededViolationOfEachBuildGateFailsTheBuildAsync()
    {
        string root = FindRepositoryRoot();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            string argument in new[]
            {
                "build",
                FixtureProject,
                "-warnaserror",
                "--no-incremental",
                "-nologo",
                "-v:q",
            }
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
        string output = await stdout + await stderr;

        process.ExitCode.ShouldNotBe(0, output);
        foreach (string rule in new[] { "RS0016", "PARQAPI002", "CA1502" })
        {
            output.ShouldContain(
                $"error {rule}",
                customMessage: $"{rule} was not reported:\n{output}"
            );
        }
    }

    [Theory]
    [InlineData("ci.yml", "Build solution (Release)")]
    [InlineData("release.yml", "Build Solution (Release)")]
    public void TheSolutionBuildStepsPromoteWarningsToErrors(string workflow, string step)
    {
        string text = Read(FindRepositoryRoot(), ".github", "workflows", workflow);
        int start = text.IndexOf($"- name: {step}", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{workflow} has no step '{step}'");
        string body = text.Substring(start, Math.Min(500, text.Length - start));
        body.ShouldContain("dotnet build Parquet.SourceGenerator.slnx");
        body.ShouldContain("-warnaserror");
    }

    [Fact]
    public void TheShippingProjectsKeepTheAnalyzerWiringTheFixtureMirrors()
    {
        string root = FindRepositoryRoot();

        Read(
                root,
                "src",
                "Parquet.SourceGenerator.Attributes",
                "Parquet.SourceGenerator.Attributes.csproj"
            )
            .ShouldContain("Microsoft.CodeAnalysis.PublicApiAnalyzers");

        foreach (
            string project in new[] { "Parquet.SourceGenerator", "Parquet.SourceGenerator.Legacy" }
        )
        {
            string csproj = Read(root, "src", project, project + ".csproj");
            Regex
                .IsMatch(
                    csproj,
                    @"ApiGates\.csproj""\s+OutputItemType=""Analyzer""",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2)
                )
                .ShouldBeTrue($"{project} must reference ApiGates as an analyzer");
            csproj.ShouldContain("seams.txt");
            csproj.ShouldContain("CodeMetricsConfig.txt");
        }

        string editorconfig = Read(root, ".editorconfig");
        foreach (string rule in new[] { "CA1502", "CA1505", "CA1506" })
        {
            editorconfig.ShouldContain($"dotnet_diagnostic.{rule}.severity = warning");
        }
    }

    private static string Read(string root, params string[] segments) =>
        IOFile
            .ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()))
            .Replace("\r\n", "\n");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (IOFile.Exists(Path.Combine(directory.FullName, FixtureProject)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
