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
/// Runs the shell steps of <c>mutation.yml</c> in bash against a fake <c>dotnet</c>. The nightly
/// failed 20 of 20 times on a shell fault no one could see without running the step (#575), and
/// since #668 the sweep is split into the partitions of <c>stryker-partitions.json</c>, so the
/// glob plumbing between that file and Stryker is shell too. Stryker itself is not run: the fake
/// records its arguments and writes (or does not write) the report.
/// </summary>
public sealed class MutationWorkflowStepTests : IDisposable
{
    private const string PartitionStep = "Stryker — run this partition";
    private const string PlanStep = "Build the partition matrix";

    private const string Partitions = """
        {
          "partitions": [
            { "name": "parser", "project": "Gen.csproj", "mutate": ["**/Parser/**/*.cs", "**/Big.cs{1..10}"] },
            { "name": "emitter", "project": "Gen.csproj", "mutate": ["**/Emitter/*.cs"] },
            { "name": "gen-rest", "project": "Gen.csproj", "rest": true },
            { "name": "attrs", "project": "Attrs.csproj", "rest": true }
          ]
        }
        """;

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

    [Fact]
    public async Task APartitionHandsExactlyItsOwnGlobsToStrykerAsync()
    {
        ToolsAvailable().ShouldBeTrue("bash and jq are required to run the workflow step");

        ScriptRun run = await RunPartitionAsync("parser", "Gen.csproj", writesReport: true);

        run.ExitCode.ShouldBe(0, run.Describe());
        run.StrykerArgs.ShouldBe([
            "tool",
            "run",
            "dotnet-stryker",
            "--",
            "-p",
            "Gen.csproj",
            "--output",
            Path.Combine(run.WorkDir, "StrykerOutput", "parser"),
            "--log-to-file",
            "--mutate",
            "**/Parser/**/*.cs",
            "--mutate",
            "**/Big.cs{1..10}",
        ]);
    }

    [Fact]
    public async Task TheRestPartitionMutatesEverythingTheProjectsOtherPartitionsLeaveAsync()
    {
        ToolsAvailable().ShouldBeTrue("bash and jq are required to run the workflow step");

        ScriptRun run = await RunPartitionAsync("gen-rest", "Gen.csproj", writesReport: true);

        run.ExitCode.ShouldBe(0, run.Describe());
        MutateGlobs(run)
            .ShouldBe(["**/*.cs", "!**/Parser/**/*.cs", "!**/Big.cs{1..10}", "!**/Emitter/*.cs"]);
    }

    [Fact]
    public async Task ARestPartitionIgnoresOtherProjectsGlobsAsync()
    {
        ToolsAvailable().ShouldBeTrue("bash and jq are required to run the workflow step");

        ScriptRun run = await RunPartitionAsync("attrs", "Attrs.csproj", writesReport: true);

        run.ExitCode.ShouldBe(0, run.Describe());
        MutateGlobs(run).ShouldBe(["**/*.cs"]);
    }

    [Fact]
    public async Task AStrykerRunThatWritesNoReportFailsTheStepWithAClearMessageAsync()
    {
        ToolsAvailable().ShouldBeTrue("bash and jq are required to run the workflow step");

        ScriptRun run = await RunPartitionAsync("emitter", "Gen.csproj", writesReport: false);

        run.ExitCode.ShouldNotBe(0, run.Describe());
        run.Stdout.ShouldContain(
            "Stryker finished without writing StrykerOutput/emitter/reports/mutation-report.json"
        );
    }

    [Fact]
    public async Task AnUndeclaredPartitionFailsBeforeStrykerRunsAsync()
    {
        ToolsAvailable().ShouldBeTrue("bash and jq are required to run the workflow step");

        ScriptRun run = await RunPartitionAsync("no-such", "Gen.csproj", writesReport: true);

        run.ExitCode.ShouldNotBe(0, run.Describe());
        run.Stdout.ShouldContain("Partition 'no-such' has no --mutate globs");
        run.StrykerArgs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ThePlanTurnsTheRealPartitionFileIntoTheMatrixAsync()
    {
        ToolsAvailable().ShouldBeTrue("bash and jq are required to run the workflow step");
        string real = IOFile.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "stryker-partitions.json")
        );

        ScriptRun run = await RunStepAsync(PlanStep, real, new Dictionary<string, string>());

        run.ExitCode.ShouldBe(0, run.Describe());
        run.Output.ShouldStartWith("matrix={\"include\":[{\"name\":\"");
        run.Output.ShouldContain("\"name\":\"generator-rest\"");
        run.Output.ShouldContain("\"name\":\"attributes\"");
    }

    [Theory]
    [InlineData(true)] // two partitions named alike: the summary could not tell their reports apart
    [InlineData(false)] // no `rest` partition: a file no glob claims would never be mutated
    public async Task ThePlanRejectsAPartitionFileThatCouldLeaveGapsOrDoubleCountAsync(
        bool duplicateName
    )
    {
        ToolsAvailable().ShouldBeTrue("bash and jq are required to run the workflow step");
        string secondName = duplicateName ? "parser" : "emitter";
        string last = duplicateName
            ? """{ "name": "gen-rest", "project": "Gen.csproj", "rest": true }"""
            : """{ "name": "gen-rest", "project": "Gen.csproj", "mutate": ["**/X.cs"] }""";
        string file = $$"""
            { "partitions": [
              { "name": "parser", "project": "Gen.csproj", "mutate": ["**/A.cs"] },
              { "name": "{{secondName}}", "project": "Gen.csproj", "mutate": ["**/B.cs"] },
              {{last}}
            ] }
            """;

        ScriptRun run = await RunStepAsync(PlanStep, file, new Dictionary<string, string>());

        run.ExitCode.ShouldNotBe(0, run.Describe());
        run.Stdout.ShouldContain("invalid partition list");
        run.Output.ShouldBeEmpty();
    }

    [Fact]
    public void TheStepsAreFoundByNameSoTheTestsCannotPassByExtractingNothing()
    {
        string workflow = ReadWorkflow();
        StepScript(PartitionStep, workflow).ShouldContain("dotnet tool run dotnet-stryker");
        StepScript(PlanStep, workflow).ShouldContain("stryker-partitions.json");
        Should.Throw<InvalidOperationException>(() => StepScript("No such step", workflow));
    }

    private static List<string> MutateGlobs(ScriptRun run) =>
        run.StrykerArgs.Where((_, i) => i > 0 && run.StrykerArgs[i - 1] == "--mutate").ToList();

    private Task<ScriptRun> RunPartitionAsync(string partition, string project, bool writesReport)
    {
        return RunStepAsync(
            PartitionStep,
            Partitions,
            new Dictionary<string, string> { ["PARTITION"] = partition, ["PROJECT"] = project },
            writesReport
        );
    }

    private async Task<ScriptRun> RunStepAsync(
        string stepName,
        string partitions,
        Dictionary<string, string> environment,
        bool writesReport = true
    )
    {
        string script = StepScript(stepName, ReadWorkflow());
        string work = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        await IOFile.WriteAllTextAsync(Path.Combine(work, "stryker-partitions.json"), partitions);

        // The fake `dotnet`, defined as a function that bash loads from BASH_ENV (so no executable
        // bit is needed): it records its arguments one per line and, like Stryker, writes the
        // report under the --output directory, or writes nothing at all.
        string argsFile = Path.Combine(work, "stryker-args");
        string writes = writesReport
            ? "    if [ \"$prev\" = \"--output\" ]; then mkdir -p \"$a/reports\" && echo '{}' > \"$a/reports/mutation-report.json\"; fi\n"
            : string.Empty;
        string fakeDotnet = Path.Combine(work, "fake-dotnet.sh");
        await IOFile.WriteAllTextAsync(
            fakeDotnet,
            "dotnet() {\n"
                + "  local prev=\"\"\n"
                + "  for a in \"$@\"; do\n"
                + $"    printf '%s\\n' \"$a\" >> '{argsFile}'\n"
                + writes
                + "    prev=\"$a\"\n"
                + "  done\n"
                + "  return 0\n"
                + "}\n"
        );

        string scriptPath = Path.Combine(work, "step.sh");
        await IOFile.WriteAllTextAsync(scriptPath, script);
        string outputFile = Path.Combine(work, "github-output");
        await IOFile.WriteAllTextAsync(outputFile, string.Empty);

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
        psi.Environment["GITHUB_OUTPUT"] = outputFile;
        foreach ((string key, string value) in environment)
        {
            psi.Environment[key] = value;
        }

        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptRun(
            process.ExitCode,
            await stdout,
            await stderr,
            await IOFile.ReadAllTextAsync(outputFile),
            IOFile.Exists(argsFile) ? (await IOFile.ReadAllLinesAsync(argsFile)).ToList() : [],
            work
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

    private static bool ToolsAvailable() => Runs("bash") && Runs("jq");

    private static bool Runs(string tool)
    {
        try
        {
            using Process? process = Process.Start(
                new ProcessStartInfo(tool, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            );
            process!.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
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

    private sealed record ScriptRun(
        int ExitCode,
        string Stdout,
        string Stderr,
        string Output,
        List<string> StrykerArgs,
        string WorkDir
    )
    {
        public string Describe() =>
            $"Stdout:\n{Stdout}\nStderr:\n{Stderr}\nGITHUB_OUTPUT:\n{Output}\nStryker args:\n{string.Join('\n', StrykerArgs)}";
    }
}
