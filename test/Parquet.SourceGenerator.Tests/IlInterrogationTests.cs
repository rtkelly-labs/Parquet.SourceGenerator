using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[Trait("Category", "Integration")]
public class IlInterrogationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScriptInterrogateILRunsAndReportsZeroBoxing()
    {
        string repoRoot = FindRepoRoot();
        var psi = CreateProcessStartInfo(repoRoot);

        using var process = Process.Start(psi);
        process.ShouldNotBeNull();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        (process.ExitCode == 0).ShouldBeTrue(
            $"InterrogateIL.cs failed with exit code {process.ExitCode}.\nStdout:\n{stdout}\nStderr:\n{stderr}"
        );
        stdout.ShouldContain("IL interrogation completed successfully!");
        stderr.ShouldNotContain("boxing operation(s) ('box') detected");
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

    private static ProcessStartInfo CreateProcessStartInfo(string repoRoot)
    {
        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");
        string dotnetHost = global::System.IO.File.Exists(homeDotnet) ? homeDotnet : "dotnet";

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

        string scriptArgs = "run scripts/InterrogateIL.cs --check";
        if (global::System.IO.File.Exists(cliReleaseDll))
        {
            scriptArgs = $"run scripts/InterrogateIL.cs --assembly \"{cliReleaseDll}\" --check";
        }
        else if (global::System.IO.File.Exists(cliDebugDll))
        {
            scriptArgs = $"run scripts/InterrogateIL.cs --assembly \"{cliDebugDll}\" --check";
        }

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
