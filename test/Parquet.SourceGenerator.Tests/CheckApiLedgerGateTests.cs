using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Drives <c>scripts/CheckApiLedger.cs</c> against throwaway git repositories. The catalogue-vs-ledger
/// rule needs a diff, and it used to be skipped (exit 0) whenever <c>GITHUB_BASE_REF</c> was empty,
/// which is every push to main (#430).
/// </summary>
[Trait("Category", "Integration")]
public class CheckApiLedgerGateTests
{
    private const string Seams = "src/api/seams.txt";
    private const string Ledger = "docs/api/LEDGER.md";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PushWithoutBaseRefStillRequiresALedgerEntryForANewCatalogueLineAsync()
    {
        using var repo = new TempRepo();
        repo.Write(Seams, "#nullable enable\n");
        repo.Write(Ledger, "# Ledger\n");
        repo.Commit("base");
        repo.Write(Seams, "#nullable enable\nA.B.M() -> void\n");
        repo.Commit("catalogue only");

        ScriptResult result = await repo.RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("no new entry in docs/api/LEDGER.md");
        result.Stdout.ShouldNotContain("skipping");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PushWithoutBaseRefPassesWhenTheCatalogueLineCarriesALedgerEntryAsync()
    {
        using var repo = new TempRepo();
        repo.Write(Seams, "#nullable enable\n");
        repo.Write(Ledger, "# Ledger\n");
        repo.Commit("base");
        repo.Write(Seams, "#nullable enable\nA.B.M() -> void\n");
        repo.Write(Ledger, "# Ledger\n\n### 2026-10-02 - `A.B.M()`\n- **Semver:** internal\n");
        repo.Commit("catalogue and ledger");

        ScriptResult result = await repo.RunAsync();

        result.ExitCode.ShouldBe(0, result.Describe());
        result.Stdout.ShouldContain("Ledger entry present");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PushRangeFromTheEventBeforeShaCoversEveryPushedCommitAsync()
    {
        using var repo = new TempRepo();
        repo.Write(Seams, "#nullable enable\n");
        repo.Write(Ledger, "# Ledger\n");
        string before = repo.Commit("base");
        repo.Write(Seams, "#nullable enable\nA.B.M() -> void\n");
        repo.Commit("catalogue only");
        repo.Write("README.md", "unrelated\n");
        repo.Commit("unrelated tip");

        // The tip commit alone changes no catalogue; only the pushed range shows the addition.
        (await repo.RunAsync()).ExitCode.ShouldBe(0, "first parent range sees nothing");
        ScriptResult result = await repo.RunAsync(("GITHUB_EVENT_BEFORE", before));

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("no new entry in docs/api/LEDGER.md");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NoBaseRefAndNoParentCommitFailsClosedAsync()
    {
        using var repo = new TempRepo();
        repo.Write(Seams, "#nullable enable\nA.B.M() -> void\n");
        repo.Write(Ledger, "# Ledger\n");
        repo.Commit("only commit");

        ScriptResult result = await repo.RunAsync();

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("cannot run");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnavailablePreviousTipFailsClosedInsteadOfCheckingOnlyTheLastCommitAsync()
    {
        using var repo = new TempRepo();
        repo.Write(Seams, "#nullable enable\n");
        repo.Write(Ledger, "# Ledger\n");
        repo.Commit("base");
        repo.Write(Seams, "#nullable enable\nA.B.M() -> void\n");
        repo.Commit("catalogue only");
        repo.Write("README.md", "unrelated\n");
        repo.Commit("unrelated tip");

        // The first parent alone would show no catalogue change and pass: the earlier pushed commit
        // would go unchecked. With a named but unobtainable previous tip there is no safe range.
        ScriptResult result = await repo.RunAsync(("GITHUB_EVENT_BEFORE", new string('a', 40)));

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("cannot run");
        result.Stdout.ShouldContain("refusing to fall back to the first parent");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AllZeroEventBeforeShaFallsBackToTheFirstParentAsync()
    {
        using var repo = new TempRepo();
        repo.Write(Seams, "#nullable enable\n");
        repo.Write(Ledger, "# Ledger\n");
        repo.Commit("base");
        repo.Write(Seams, "#nullable enable\nA.B.M() -> void\n");
        repo.Commit("catalogue only");

        ScriptResult result = await repo.RunAsync(("GITHUB_EVENT_BEFORE", new string('0', 40)));

        result.ExitCode.ShouldBe(1, result.Describe());
    }

    private sealed class TempRepo : IDisposable
    {
        private static readonly string ScriptPath = Path.Combine(
            FindRepoRoot(),
            "scripts",
            "CheckApiLedger.cs"
        );

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            "parquet-ledger-gate-" + Guid.NewGuid().ToString("N")
        );

        public TempRepo()
        {
            Directory.CreateDirectory(_dir);
            Git("init", "-q", "-b", "main");
        }

        public void Write(string relativePath, string content)
        {
            string path = Path.Combine(_dir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            global::System.IO.File.WriteAllText(path, content);
        }

        public string Commit(string message)
        {
            Git("add", "-A");
            Git(
                "-c",
                "user.name=test",
                "-c",
                "user.email=test@example.invalid",
                "-c",
                "commit.gpgsign=false",
                "commit",
                "-q",
                "-m",
                message
            );
            return Git("rev-parse", "HEAD").Trim();
        }

        public async Task<ScriptResult> RunAsync(params (string Name, string Value)[] environment)
        {
            var psi = new ProcessStartInfo
            {
                FileName = DotnetHost(),
                WorkingDirectory = _dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(ScriptPath);

            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
            psi.Environment["DOTNET_BUILD_SERVER_DISABLE"] = "1";
            // The runner's own CI variables must not leak into the script under test.
            psi.Environment.Remove("GITHUB_BASE_REF");
            psi.Environment.Remove("GITHUB_EVENT_BEFORE");
            foreach ((string name, string value) in environment)
            {
                psi.Environment[name] = value;
            }

            using Process? process = Process.Start(psi);
            process.ShouldNotBeNull();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ScriptResult(process.ExitCode, await stdout, await stderr);
        }

        public void Dispose()
        {
            try
            {
                foreach (
                    string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
                )
                {
                    global::System.IO.File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup only.
            }
        }

        private string Git(params string[] arguments)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = _dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)}: {stderr}");
            return stdout;
        }

        private static string DotnetHost()
        {
            string homeDotnetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dotnet"
            );
            string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");
            return global::System.IO.File.Exists(homeDotnet) ? homeDotnet : "dotnet";
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (
                    global::System.IO.File.Exists(
                        Path.Combine(dir.FullName, "scripts", "CheckApiLedger.cs")
                    )
                )
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                "Could not find repository root containing scripts/CheckApiLedger.cs"
            );
        }
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Describe() => $"Stdout:\n{Stdout}\nStderr:\n{Stderr}";
    }
}
