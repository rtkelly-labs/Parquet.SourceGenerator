using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Runs the Stryker steps of <c>mutation.yml</c> in bash against a fake <c>dotnet</c>. The nightly
/// failed 20 of 20 times because <c>before=$(ls -1d StrykerOutput/*/ | sort)</c> exits non-zero on
/// a fresh runner (no <c>StrykerOutput/</c> yet) and <c>pipefail</c> carries that into the
/// assignment, so the step died before Stryker started (#575). Reproducing the shell is the only
/// way to know the step now survives a fresh runner; Stryker itself is not run.
/// </summary>
public sealed class MutationWorkflowStepTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "parquet-mutation-step-" + Guid.NewGuid().ToString("N")
    );

    public MutationWorkflowStepTests() => Directory.CreateDirectory(_dir);

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

    [Theory]
    [InlineData("Stryker — the generator (parser + emitters)", "GENERATOR_REPORT")]
    [InlineData("Stryker — shipped runtime helpers (Attributes)", "ATTRIBUTES_REPORT")]
    public async Task TheStrykerStepSurvivesAFreshRunnerWithNoOutputDirectoryAsync(
        string stepName,
        string reportVariable
    )
    {
        BashAvailable().ShouldBeTrue("bash is required to run the workflow step");

        ScriptRun run = await RunStepAsync(stepName, seedPreviousOutput: false, stryker: "creates");

        run.ExitCode.ShouldBe(0, run.Describe());
        run.EnvFile.ShouldContain(
            $"{reportVariable}=StrykerOutput/2026-10-02.04-30-00//reports/mutation-report.json"
        );
    }

    [Theory]
    [InlineData("Stryker — the generator (parser + emitters)", "GENERATOR_REPORT")]
    [InlineData("Stryker — shipped runtime helpers (Attributes)", "ATTRIBUTES_REPORT")]
    public async Task TheStrykerStepReportsOnlyTheDirectoryThisRunCreatedAsync(
        string stepName,
        string reportVariable
    )
    {
        BashAvailable().ShouldBeTrue("bash is required to run the workflow step");

        ScriptRun run = await RunStepAsync(stepName, seedPreviousOutput: true, stryker: "creates");

        run.ExitCode.ShouldBe(0, run.Describe());
        run.EnvFile.ShouldContain($"{reportVariable}=StrykerOutput/2026-10-02.04-30-00//reports");
        run.EnvFile.ShouldNotContain("2026-10-01");
    }

    [Fact]
    public async Task AStrykerRunThatCreatesNoOutputFailsTheStepWithAClearMessageAsync()
    {
        BashAvailable().ShouldBeTrue("bash is required to run the workflow step");

        ScriptRun run = await RunStepAsync(
            "Stryker — the generator (parser + emitters)",
            seedPreviousOutput: false,
            stryker: "creates-nothing"
        );

        run.ExitCode.ShouldNotBe(0, run.Describe());
        run.Stdout.ShouldContain("Stryker finished without creating a new StrykerOutput directory");
        run.EnvFile.ShouldBeEmpty();
    }

    [Fact]
    public void TheStepsAreFoundByNameSoTheTestsCannotPassByExtractingNothing()
    {
        string workflow = ReadWorkflow();
        StepScript("Stryker — the generator (parser + emitters)", workflow)
            .ShouldContain("dotnet tool run dotnet-stryker");
        StepScript("Stryker — shipped runtime helpers (Attributes)", workflow)
            .ShouldContain("dotnet tool run dotnet-stryker");
        Should.Throw<InvalidOperationException>(() => StepScript("No such step", workflow));
    }

    private async Task<ScriptRun> RunStepAsync(
        string stepName,
        bool seedPreviousOutput,
        string stryker
    )
    {
        string script = StepScript(stepName, ReadWorkflow());
        string work = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        // The fake `dotnet`, defined as a function that bash loads from BASH_ENV (so no executable
        // bit is needed): `dotnet tool run dotnet-stryker ...` creates the timestamped output
        // directory the real tool creates, or nothing at all.
        string creates =
            stryker == "creates"
                ? "mkdir -p StrykerOutput/2026-10-02.04-30-00/reports && echo '{}' > StrykerOutput/2026-10-02.04-30-00/reports/mutation-report.json\n"
                : string.Empty;
        string fakeDotnet = Path.Combine(work, "fake-dotnet.sh");
        await IOFile.WriteAllTextAsync(fakeDotnet, "dotnet() {\n" + creates + "return 0\n}\n");

        if (seedPreviousOutput)
        {
            Directory.CreateDirectory(Path.Combine(work, "StrykerOutput", "2026-10-01.04-30-00"));
        }

        string scriptPath = Path.Combine(work, "step.sh");
        await IOFile.WriteAllTextAsync(scriptPath, script);
        string envFile = Path.Combine(work, "github-env");
        await IOFile.WriteAllTextAsync(envFile, string.Empty);

        // GitHub's default `run` shell for bash is `bash --noprofile --norc -eo pipefail {0}`.
        var psi = new ProcessStartInfo("bash")
        {
            WorkingDirectory = work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            string argument in new[] { "--noprofile", "--norc", "-eo", "pipefail", scriptPath }
        )
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["BASH_ENV"] = fakeDotnet;
        psi.Environment["GITHUB_ENV"] = envFile;

        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptRun(
            process.ExitCode,
            await stdout,
            await stderr,
            await IOFile.ReadAllTextAsync(envFile)
        );
    }

    /// <summary>The body of one step's <c>run: |</c> block, read from the real workflow.</summary>
    private static string StepScript(string stepName, string workflow)
    {
        string[] lines = workflow.Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimEnd() == $"      - name: {stepName}");
        if (start < 0)
        {
            throw new InvalidOperationException($"Step '{stepName}' not found in mutation.yml");
        }

        int run = Array.FindIndex(lines, start, l => l.TrimEnd() == "        run: |");
        int next = Array.FindIndex(
            lines,
            start + 1,
            l => l.StartsWith("      - name:", StringComparison.Ordinal)
        );
        if (run < 0 || (next >= 0 && run > next))
        {
            throw new InvalidOperationException($"Step '{stepName}' has no `run: |` block");
        }

        var body = new List<string>();
        for (int i = run + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !line.StartsWith("          ", StringComparison.Ordinal))
            {
                break;
            }

            body.Add(line.Length >= 10 ? line[10..] : string.Empty);
        }

        return string.Join('\n', body) + "\n";
    }

    private static string ReadWorkflow() =>
        IOFile
            .ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "mutation.yml"))
            .Replace("\r\n", "\n");

    private static bool BashAvailable()
    {
        try
        {
            using Process? process = Process.Start(
                new ProcessStartInfo("bash", "--version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                }
            );
            process!.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (
                IOFile.Exists(
                    Path.Combine(directory.FullName, ".github", "workflows", "mutation.yml")
                )
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed record ScriptRun(int ExitCode, string Stdout, string Stderr, string EnvFile)
    {
        public string Describe() => $"Stdout:\n{Stdout}\nStderr:\n{Stderr}\nGITHUB_ENV:\n{EnvFile}";
    }
}
