using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[Trait("Category", "Integration")]
public class IlInterrogationTests
{
    // IL fixtures below mirror the shape of `ilspycmd -il` output closely enough for the gate's
    // extraction and opcode regexes, so the detector can be driven with known-good and known-bad
    // input without depending on ilspycmd being restorable.
    private const string CleanIl = """
        .class public auto ansi abstract sealed beforefieldinit Sample.CleanParquetExtensions
            extends [System.Runtime]System.Object
        {
            .method public hidebysig static int32 Add(int32 a, int32 b) cil managed
            {
                .maxstack 2

                IL_0000: ldarg.0
                IL_0001: ldarg.1
                IL_0002: add
                IL_0003: ret
            } // end of method CleanParquetExtensions::Add
        } // end of class Sample.CleanParquetExtensions
        """;

    private const string BoxingIl = """
        .class public auto ansi abstract sealed beforefieldinit Sample.BoxingParquetExtensions
            extends [System.Runtime]System.Object
        {
            .method public hidebysig static object Wrap(int32 value) cil managed
            {
                .maxstack 1

                IL_0000: ldarg.0
                IL_0001: box [System.Runtime]System.Int32
                IL_0006: ret
            } // end of method BoxingParquetExtensions::Wrap
        } // end of class Sample.BoxingParquetExtensions
        """;

    // A type declaration with no instructions at all: exactly what a failed or format-changed
    // disassembly degrades into, and what must never be reported as clean.
    private const string InstructionlessIl = """
        .class public auto ansi abstract sealed beforefieldinit Sample.EmptyParquetExtensions
            extends [System.Runtime]System.Object
        {
        } // end of class Sample.EmptyParquetExtensions
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScriptInterrogateILRunsAndReportsZeroBoxing()
    {
        string repoRoot = FindRepoRoot();
        var psi = CreateProcessStartInfo(repoRoot, BuildAssemblyScriptArgs(repoRoot));

        var (exitCode, stdout, stderr) = await RunAsync(psi);

        (exitCode == 0).ShouldBeTrue(
            $"InterrogateIL.cs failed with exit code {exitCode}.\nStdout:\n{stdout}\nStderr:\n{stderr}"
        );
        stdout.ShouldContain("IL interrogation completed successfully!");
        stderr.ShouldNotContain("boxing operation(s) ('box') detected");

        // Positive control: a run that examined nothing must not count as a clean run.
        ExtractExaminedInstructionCount(stdout)
            .ShouldBeGreaterThan(
                0,
                $"The gate reported zero boxing without examining any IL.\nStdout:\n{stdout}"
            );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GateFailsWhenInterrogatedIlContainsBoxing()
    {
        var (exitCode, stdout, stderr) = await RunAgainstIlAsync(
            BoxingIl,
            "*BoxingParquetExtensions*"
        );

        exitCode.ShouldBe(
            1,
            $"The gate accepted IL containing a 'box' opcode.\nStdout:\n{stdout}\nStderr:\n{stderr}"
        );
        stderr.ShouldContain("boxing operation(s) ('box') detected");
        stdout.ShouldNotContain("IL interrogation completed successfully!");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GateFailsWhenNoIlInstructionsWereRecovered()
    {
        var (exitCode, stdout, stderr) = await RunAgainstIlAsync(
            InstructionlessIl,
            "*EmptyParquetExtensions*"
        );

        exitCode.ShouldBe(
            1,
            $"The gate reported success without recovering any IL.\nStdout:\n{stdout}\nStderr:\n{stderr}"
        );
        stderr.ShouldContain("refusing to report zero boxing");
        stdout.ShouldNotContain("IL interrogation completed successfully!");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GateReportsNonZeroInstructionCountForCleanIl()
    {
        var (exitCode, stdout, stderr) = await RunAgainstIlAsync(
            CleanIl,
            "*CleanParquetExtensions*"
        );

        (exitCode == 0).ShouldBeTrue(
            $"The gate rejected boxing-free IL with exit code {exitCode}.\nStdout:\n{stdout}\nStderr:\n{stderr}"
        );
        stdout.ShouldContain("IL interrogation completed successfully!");
        ExtractExaminedInstructionCount(stdout)
            .ShouldBeGreaterThan(
                0,
                $"The gate passed without reporting any examined instructions.\nStdout:\n{stdout}"
            );
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAgainstIlAsync(
        string il,
        string typeFilter
    )
    {
        string repoRoot = FindRepoRoot();
        string workDir = Path.Combine(
            repoRoot,
            "temp",
            "il-gate-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
        );
        Directory.CreateDirectory(workDir);

        try
        {
            string ilFile = Path.Combine(workDir, "fixture.il");
            await global::System.IO.File.WriteAllTextAsync(ilFile, il);

            string arguments =
                $"run scripts/InterrogateIL.cs --il-source \"{ilFile}\" --type \"{typeFilter}\" --out \"{workDir}\" --check";

            return await RunAsync(CreateProcessStartInfo(repoRoot, arguments));
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
                // Leftover artifacts under temp/ are gitignored and harmless.
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo psi
    )
    {
        using var process = Process.Start(psi);
        process.ShouldNotBeNull();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static int ExtractExaminedInstructionCount(string stdout)
    {
        var match = Regex.Match(
            stdout,
            @"Positive control: (?<count>\d+) IL instruction",
            RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2)
        );
        match.Success.ShouldBeTrue(
            $"InterrogateIL.cs did not report a positive control count.\nStdout:\n{stdout}"
        );
        return int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (
                global::System.IO.File.Exists(
                    Path.Combine(dir.FullName, "scripts", "InterrogateIL.cs")
                )
            )
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find repository root containing scripts/InterrogateIL.cs"
        );
    }

    private static string BuildAssemblyScriptArgs(string repoRoot)
    {
        string cliReleaseDll = Path.Combine(
            repoRoot,
            "test",
            "Parquet.SourceGenerator.CLI",
            "bin",
            "Release",
            "net8.0",
            "Parquet.SourceGenerator.CLI.dll"
        );
        string cliDebugDll = Path.Combine(
            repoRoot,
            "test",
            "Parquet.SourceGenerator.CLI",
            "bin",
            "Debug",
            "net8.0",
            "Parquet.SourceGenerator.CLI.dll"
        );

        if (global::System.IO.File.Exists(cliReleaseDll))
        {
            return $"run scripts/InterrogateIL.cs --assembly \"{cliReleaseDll}\" --check";
        }
        if (global::System.IO.File.Exists(cliDebugDll))
        {
            return $"run scripts/InterrogateIL.cs --assembly \"{cliDebugDll}\" --check";
        }
        return "run scripts/InterrogateIL.cs --check";
    }

    private static ProcessStartInfo CreateProcessStartInfo(string repoRoot, string scriptArgs)
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
            Arguments = scriptArgs,
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
}
