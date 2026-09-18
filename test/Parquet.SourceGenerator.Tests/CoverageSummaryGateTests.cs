using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Guards the coverage gate in <c>scripts/CoverageSummary.cs</c> against reports that
/// measure nothing. A zero denominator used to default the rate to 100%, so an empty,
/// filtered-out or condition-coverage-less report passed the gate.
/// </summary>
[Trait("Category", "Integration")]
public class CoverageSummaryGateTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReportWithNoLineElementsFailsTheGate()
    {
        const string report = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" branch-rate="0" version="1.9" timestamp="0">
              <packages>
                <package name="Parquet.SourceGenerator" line-rate="0" branch-rate="0" complexity="0">
                  <classes />
                </package>
              </packages>
            </coverage>
            """;

        var result = await RunCoverageSummaryAsync(report);

        result.ExitCode.ShouldNotBe(
            0,
            $"An empty coverage report must fail the gate.\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}"
        );
        result.Stderr.ShouldContain("No lines found in coverage report");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReportWhereEveryPackageIsFilteredOutFailsTheGate()
    {
        const string report = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="1" branch-rate="1" version="1.9" timestamp="0">
              <packages>
                <package name="Parquet.SourceGenerator.Tests" line-rate="1" branch-rate="1" complexity="0">
                  <classes>
                    <class name="Parquet.SourceGenerator.Tests.SomeTests" filename="SomeTests.cs" line-rate="1" branch-rate="1" complexity="0">
                      <lines>
                        <line number="1" hits="1" branch="false" />
                        <line number="2" hits="1" branch="true" condition-coverage="100% (2/2)" />
                      </lines>
                    </class>
                  </classes>
                </package>
                <package name="BenchmarkSummaryGenerator" line-rate="1" branch-rate="1" complexity="0">
                  <classes>
                    <class name="BenchmarkSummaryGenerator.Program" filename="Program.cs" line-rate="1" branch-rate="1" complexity="0">
                      <lines>
                        <line number="1" hits="1" branch="false" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var result = await RunCoverageSummaryAsync(report);

        result.ExitCode.ShouldNotBe(
            0,
            $"A report whose packages are all filtered out must fail the gate.\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}"
        );
        result.Stderr.ShouldContain("shipping package");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReportWithoutConditionCoverageAttributesFailsTheGate()
    {
        const string report = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="1" branch-rate="0" version="1.9" timestamp="0">
              <packages>
                <package name="Parquet.SourceGenerator" line-rate="1" branch-rate="0" complexity="0">
                  <classes>
                    <class name="Parquet.SourceGenerator.Writer" filename="Writer.cs" line-rate="1" branch-rate="0" complexity="0">
                      <lines>
                        <line number="1" hits="1" branch="false" />
                        <line number="2" hits="1" branch="false" />
                        <line number="3" hits="1" branch="true" />
                        <line number="4" hits="1" branch="true" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var result = await RunCoverageSummaryAsync(report);

        result.ExitCode.ShouldNotBe(
            0,
            $"A report carrying no condition-coverage data must fail the gate.\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}"
        );
        result.Stderr.ShouldContain("No branches found in coverage report");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WellMeasuredReportAboveThresholdsPassesTheGate()
    {
        const string report = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="1" branch-rate="1" version="1.9" timestamp="0">
              <packages>
                <package name="Parquet.SourceGenerator" line-rate="1" branch-rate="1" complexity="0">
                  <classes>
                    <class name="Parquet.SourceGenerator.Writer" filename="Writer.cs" line-rate="1" branch-rate="1" complexity="0">
                      <lines>
                        <line number="1" hits="1" branch="false" />
                        <line number="2" hits="1" branch="false" />
                        <line number="3" hits="1" branch="true" condition-coverage="100% (2/2)" />
                        <line number="4" hits="1" branch="true" condition-coverage="100% (2/2)" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var result = await RunCoverageSummaryAsync(report);

        result.ExitCode.ShouldBe(
            0,
            $"A fully covered report must pass the gate.\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}"
        );
        result.Stdout.ShouldContain("Code coverage gate passed successfully");
    }

    private static async Task<ScriptResult> RunCoverageSummaryAsync(string coberturaXml)
    {
        string repoRoot = FindRepoRoot();
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "parquet-coverage-gate-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempDir);

        try
        {
            string reportPath = Path.Combine(tempDir, "coverage.cobertura.xml");
            await global::System.IO.File.WriteAllTextAsync(reportPath, coberturaXml);

            var psi = CreateProcessStartInfo(repoRoot, reportPath);

            using var process = Process.Start(psi);
            process.ShouldNotBeNull();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup only.
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (
                global::System.IO.File.Exists(
                    Path.Combine(dir.FullName, "scripts", "CoverageSummary.cs")
                )
            )
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find repository root containing scripts/CoverageSummary.cs"
        );
    }

    private static ProcessStartInfo CreateProcessStartInfo(string repoRoot, string reportPath)
    {
        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");
        string dotnetHost = global::System.IO.File.Exists(homeDotnet) ? homeDotnet : "dotnet";

        var psi = new ProcessStartInfo
        {
            FileName = dotnetHost,
            Arguments =
                $"run scripts/CoverageSummary.cs -- --input \"{reportPath}\" --min-line 85.0 --min-branch 70.0",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        psi.Environment["DOTNET_BUILD_SERVER_DISABLE"] = "1";

        if (Directory.Exists(homeDotnetDir))
        {
            psi.Environment["DOTNET_ROOT"] = homeDotnetDir;
            psi.Environment["PATH"] =
                Path.Combine(homeDotnetDir, "tools")
                + Path.PathSeparator
                + homeDotnetDir
                + Path.PathSeparator
                + Environment.GetEnvironmentVariable("PATH");
        }

        return psi;
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);
}
