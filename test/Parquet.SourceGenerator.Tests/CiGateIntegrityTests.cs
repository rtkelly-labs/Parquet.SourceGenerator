using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Guards the wiring around the CI gates rather than any one gate (docs/internals/ci-gate-matrix.md):
/// the required check names, the aggregate that funnels the jobs into them, and the pin policy
/// for third-party actions. A gate that is not reachable from a required check, or that runs
/// code nobody pinned, protects nothing. Each repository test asserts it examined something, and
/// each rule is also run against fixtures it must reject, so none of them passes by finding no
/// workflows or by matching text it should not.
/// </summary>
public sealed class CiGateIntegrityTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private const RegexOptions Options =
        RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant;

    // A `uses` key in block form (`uses: x`, `- uses: x`), with a quoted key (`- "uses": x`), or in
    // a flow mapping (`- { uses: x }`). Comment-only lines are removed before matching.
    private static readonly Regex UsesKey = new(
        @"(?:^|[\s{,])[""']?uses[""']?\s*:\s*[""']?(?<ref>[^\s#""',}\]]+)",
        Options,
        RegexTimeout
    );

    private static readonly Regex CommentLine = new(@"^\s*#.*$", Options, RegexTimeout);

    private static readonly Regex FullSha = new(
        @"@[0-9a-f]{40}$",
        RegexOptions.CultureInvariant,
        RegexTimeout
    );

    // A job key: exactly two spaces of indent, a bare identifier, nothing after the colon.
    private static readonly Regex JobKey = new(
        @"^  (?<id>[A-Za-z0-9_-]+):\s*$",
        Options,
        RegexTimeout
    );

    // A job's own `name:` sits at four spaces; a step's is `- name:` at six, or `name:` at eight.
    private static readonly Regex JobNameKey = new(
        @"^    name:\s*(?<name>\S.*?)\s*$",
        Options,
        RegexTimeout
    );

    [Fact]
    public void EveryThirdPartyActionIsPinnedToAFortyCharacterCommitSha()
    {
        string root = FindRepositoryRoot();
        string[] files = WorkflowAndActionFiles(root);

        // Positive control: the files this test exists to police are among those it read.
        files.ShouldContain(f => f.EndsWith("ci.yml", StringComparison.Ordinal));
        files.ShouldContain(f => f.EndsWith("release.yml", StringComparison.Ordinal));

        var unpinned = new List<string>();
        int examined = 0;
        foreach (string file in files)
        {
            (int count, string[] mutable) = ScanUses(IOFile.ReadAllText(file));
            examined += count;
            unpinned.AddRange(mutable.Select(r => $"{Path.GetRelativePath(root, file)}: {r}"));
        }

        examined.ShouldBeGreaterThan(10, "expected the workflows to reference many actions");
        unpinned.ShouldBeEmpty(
            "mutable action tags can be moved by their owner; pin a 40-character commit SHA"
        );
    }

    [Theory]
    [InlineData("uses: actions/checkout@v4")]
    [InlineData("- uses: actions/checkout@main")]
    [InlineData("- \"uses\": actions/checkout@main")]
    [InlineData("- { uses: actions/checkout@main }")]
    [InlineData("- {name: x, uses: 'actions/checkout@v4'}")]
    [InlineData("uses: actions/checkout@3d3c42e5")]
    [InlineData("uses: actions/checkout")]
    public void ThePinScannerRejectsMutableReferencesInEveryYamlSpelling(string line)
    {
        (int examined, string[] mutable) = ScanUses(line);

        examined.ShouldBe(1);
        mutable.Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7")]
    [InlineData("- { uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 }")]
    [InlineData("- uses: ./.github/actions/verify-test-data")]
    [InlineData("# uses: actions/checkout@v4 (a comment)")]
    public void ThePinScannerAcceptsPinnedAndLocalReferences(string line)
    {
        (_, string[] mutable) = ScanUses(line);

        mutable.ShouldBeEmpty();
    }

    [Fact]
    public void TheRequiredSuiteEvidenceRunsInsideTheRequiredTestJob()
    {
        string ci = Read(FindRepositoryRoot(), ".github", "workflows", "ci.yml");
        string test = JobBlock(ci, "test");

        test.ShouldContain("ci-core.trx");
        test.ShouldContain("ci-external.trx");
        test.ShouldContain("Verify Required Test Execution");
        test.ShouldContain("dotnet run scripts/VerifyRequiredTestExecution.cs");
    }

    [Fact]
    public void TheRequiredCheckNamesAreTheJobNamesBranchProtectionMatchesOn()
    {
        string root = FindRepositoryRoot();

        // Branch protection matches a check by name string: `build` and `pr-title`.
        string ci = Read(root, ".github", "workflows", "ci.yml");
        JobName(JobBlock(ci, "build")).ShouldBe("build");

        string prTitle = Read(root, ".github", "workflows", "pr-title.yml");
        JobName(JobBlock(prTitle, "pr-title")).ShouldBe("pr-title");
    }

    [Fact]
    public void TheJobNameIsReadFromTheJobAndNotFromAStepThatKeepsTheOldName()
    {
        const string renamed = """
              build:
                name: aggregate
                runs-on: ubuntu-latest
                steps:
                  - name: build
                    run: echo hi
            """;

        JobName(renamed).ShouldBe("aggregate");
    }

    [Fact]
    public void TheBuildAggregateRequiresEveryOtherJobInCiToSucceed()
    {
        string root = FindRepositoryRoot();
        string ci = Read(root, ".github", "workflows", "ci.yml");

        string[] jobs = JobKey.Matches(JobsSection(ci)).Select(m => m.Groups["id"].Value).ToArray();
        jobs.ShouldContain("build");
        string[] gated = jobs.Where(j => j != "build").ToArray();
        gated.Length.ShouldBeGreaterThan(1, "ci.yml should have gated jobs besides the aggregate");

        string aggregate = JobBlock(ci, "build");

        // `if: always()` so a failed or cancelled dependency reports failure instead of the
        // aggregate being skipped; a skipped required check is not a failed one in every UI.
        aggregate.ShouldContain("if: always()");

        Match needs = Regex.Match(
            aggregate,
            @"^    needs:\s*\[(?<list>[^\]]*)\]",
            Options,
            RegexTimeout
        );
        needs.Success.ShouldBeTrue("the build job must declare `needs: [...]`");
        string[] declared = needs
            .Groups["list"]
            .Value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .ToArray();
        declared
            .OrderBy(j => j, StringComparer.Ordinal)
            .ShouldBe(gated.OrderBy(j => j, StringComparer.Ordinal));

        AggregateProblems(aggregate, gated).ShouldBeEmpty();
    }

    [Fact]
    public void TheReleaseWorkflowDefaultsToADryRunAndKeepsItsMainOnlyGuards()
    {
        string release = Read(FindRepositoryRoot(), ".github", "workflows", "release.yml");

        // A real publish needs an explicit `dry_run: false`.
        Regex
            .IsMatch(
                release,
                @"^      dry_run:\n(?:        .*\n)*?        default: true\s*$",
                Options,
                RegexTimeout
            )
            .ShouldBeTrue("the dry_run input must default to true");

        // Both brakes from #465: the hard failure in prepare and the structural one on publish.
        string prepare = JobBlock(release, "prepare");
        prepare.ShouldContain("if: ${{ !inputs.dry_run }}");
        prepare.ShouldContain("\"$GITHUB_REF\" != \"refs/heads/main\"");
        prepare.ShouldContain("exit 1");

        string publish = JobBlock(release, "publish");
        publish.ShouldContain("!inputs.dry_run && github.ref == 'refs/heads/main'");
    }

    [Fact]
    public void ThePublishJobRequiresTheCommitsBuildCheckToHaveSucceeded()
    {
        string release = Read(FindRepositoryRoot(), ".github", "workflows", "release.yml");

        string publish = JobBlock(release, "publish");
        Match needs = Regex.Match(
            publish,
            @"^    needs:\s*\[(?<list>[^\]]*)\]",
            Options,
            RegexTimeout
        );
        needs.Success.ShouldBeTrue("publish must declare `needs: [...]`");
        needs
            .Groups["list"]
            .Value.Split(',', StringSplitOptions.TrimEntries)
            .ShouldContain("verify-ci");

        string verify = JobBlock(release, "verify-ci");
        verify.ShouldContain("check_name=build");
        verify.ShouldContain("commits/$GITHUB_SHA/check-runs");
        verify.ShouldContain("!= \"success\"");
        verify.ShouldContain("exit 1");
        verify.ShouldNotContain("exit 0");
        verify.ShouldNotContain("continue-on-error");

        // Least privilege: `checks: read` is held by this job alone, and the workflow default
        // stays read-only on contents.
        Regex.Count(release, @"^\s+checks:\s*read\s*$", Options, RegexTimeout).ShouldBe(1);
        verify.ShouldContain("checks: read");
        Regex
            .IsMatch(
                verify,
                @"^\s+(contents|id-token|actions|packages|pull-requests):\s*write",
                Options,
                RegexTimeout
            )
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData("src/Parquet.SourceGenerator/Parquet.SourceGenerator.csproj")]
    [InlineData("src/Parquet.SourceGenerator.Legacy/Parquet.SourceGenerator.Legacy.csproj")]
    public void TheShippingGeneratorsRunTheGeneratorAuthorAnalyzerRules(string project)
    {
        // #471: Microsoft.CodeAnalysis.Analyzers 3.3.3 carried none of RS1035/RS1036/RS1038/RS1041,
        // and the property that switches them on was set on an internal tool instead of the
        // assemblies that load into every consumer's compiler. Both halves are asserted, plus the
        // inverse: no override may pin the analyzer package back to an old version.
        string csproj = Read(FindRepositoryRoot(), project.Split('/'));

        csproj.ShouldContain("<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>");

        Match reference = Regex.Match(
            csproj,
            @"<PackageReference\s+Include=""Microsoft\.CodeAnalysis\.Analyzers""[^>]*/>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            RegexTimeout
        );
        reference.Success.ShouldBeTrue(
            $"{project} must reference Microsoft.CodeAnalysis.Analyzers"
        );
        reference.Value.ShouldNotContain("VersionOverride");

        // The Roslyn floor is a different pin and must stay where it is (#432).
        csproj.ShouldContain("Include=\"Microsoft.CodeAnalysis.CSharp\"");
        csproj.ShouldContain("VersionOverride=\"4.0.1\"");

        // A suppression may name RS2008 and RS1032 and nothing broader.
        foreach (Match noWarn in Regex.Matches(csproj, @"<NoWarn>([^<]*)</NoWarn>"))
        {
            noWarn.Groups[1].Value.ShouldNotMatch(@"RS1(?!032)\d{3}");
            noWarn.Groups[1].Value.ShouldNotContain("RS1035");
        }
    }

    [Fact]
    public void TheCentralAnalyzersVersionIsPastTheRulesGeneratorAuthorsNeed()
    {
        string props = Read(FindRepositoryRoot(), "Directory.Packages.props");
        Match version = Regex.Match(
            props,
            @"Include=""Microsoft\.CodeAnalysis\.Analyzers""\s+Version=""(\d+\.\d+)[^""]*""",
            RegexOptions.CultureInvariant,
            RegexTimeout
        );

        version.Success.ShouldBeTrue();
        // The 3.3.x line (the old override and the old central pin) predates these rules (#471).
        Version.Parse(version.Groups[1].Value).ShouldBeGreaterThanOrEqualTo(new Version(3, 11));
    }

    // Native AOT projects restore a runtime-specific target (osx-arm64 on one machine, linux-x64 in
    // CI), so their lock file would never be valid on a second platform (#394).
    private static readonly string[] ProjectsWithoutLockFile =
    [
        "Parquet.SourceGenerator.AotTest",
        "Parquet.SourceGenerator.SampleAot",
    ];

    [Fact]
    public void EveryProjectInTheSolutionHasALockFileOrIsPaketManagedExceptTheNativeAotOnes()
    {
        string root = FindRepositoryRoot();
        string[] projects = Regex
            .Matches(
                Read(root, "Parquet.SourceGenerator.slnx"),
                @"Path=""(?<p>[^""]+\.csproj)""",
                Options,
                RegexTimeout
            )
            .Select(m => m.Groups["p"].Value)
            .ToArray();

        projects.Length.ShouldBeGreaterThan(8, "the solution scan examined too few projects");
        var problems = new List<string>();
        var locked = new List<string>();
        var paketManaged = new List<string>();
        foreach (string project in projects)
        {
            string name = Path.GetFileNameWithoutExtension(project);
            string directory = Path.Combine(root, Path.GetDirectoryName(project)!);
            bool hasLockFile = IOFile.Exists(Path.Combine(directory, "packages.lock.json"));
            bool hasPaketReferences = IOFile.Exists(Path.Combine(directory, "paket.references"));
            if (hasLockFile)
            {
                locked.Add(name);
            }
            else if (hasPaketReferences)
            {
                paketManaged.Add(name);
            }

            problems.AddRange(
                LockProblems(
                    name,
                    ProjectsWithoutLockFile.Contains(name),
                    hasLockFile,
                    hasPaketReferences,
                    IOFile.ReadAllText(Path.Combine(root, project))
                )
            );
        }

        problems.ShouldBeEmpty();
        // Both kinds were examined: the NuGet-locked majority and the Paket pilot (ADR 0004).
        locked.Count.ShouldBeGreaterThan(5, "the solution scan found too few locked projects");
        paketManaged.ShouldContain("Parquet.SourceGenerator.Benchmarks");
        IOFile.Exists(Path.Combine(root, "paket.lock")).ShouldBeTrue("paket.lock is committed");
    }

    private const string PaketImport =
        """<Import Project="..\..\.paket\Paket.Restore.targets" />""";

    [Fact]
    public void TheLockRuleAcceptsALockedProjectAPaketManagedProjectAndTheNativeAotOnes()
    {
        LockProblems("Locked", false, true, false, "<Project />").ShouldBeEmpty();
        LockProblems("Paket", false, false, true, PaketImport).ShouldBeEmpty();
        LockProblems("Aot", true, false, false, "<Project />").ShouldBeEmpty();
    }

    [Theory]
    // Neither lock: the restore would resolve whatever the feed holds today.
    [InlineData(false, false, false, "<Project />")]
    // A deleted paket.references: the project is no longer locked by anything.
    [InlineData(false, false, false, PaketImport)]
    // Both: two sources of truth for one project.
    [InlineData(false, true, true, PaketImport)]
    // paket.references that nothing reads: Paket.Restore.targets is not imported.
    [InlineData(false, false, true, "<Project />")]
    // An import in a comment is not an import.
    [InlineData(false, false, true, "<!-- " + PaketImport + " -->")]
    // A Native AOT project must not gain a lock it cannot keep on a second platform.
    [InlineData(true, true, false, "<Project />")]
    public void TheLockRuleRejectsAProjectThatNothingLocks(
        bool nativeAot,
        bool hasLockFile,
        bool hasPaketReferences,
        string csproj
    ) =>
        LockProblems("Fixture", nativeAot, hasLockFile, hasPaketReferences, csproj)
            .ShouldNotBeEmpty();

    // A project is locked by its packages.lock.json (NuGet) or by paket.lock through its
    // paket.references (ADR 0004), never by both and never by neither. A Paket-managed project
    // must import Paket.Restore.targets, or its paket.references is inert.
    private static List<string> LockProblems(
        string name,
        bool nativeAot,
        bool hasLockFile,
        bool hasPaketReferences,
        string csproj
    )
    {
        var problems = new List<string>();
        if (nativeAot)
        {
            if (hasLockFile || hasPaketReferences)
            {
                problems.Add($"{name}: a Native AOT project carries no lock (#394)");
            }

            return problems;
        }

        if (hasLockFile == hasPaketReferences)
        {
            problems.Add(
                hasLockFile
                    ? $"{name}: has both packages.lock.json and paket.references"
                    : $"{name}: has neither packages.lock.json nor paket.references"
            );
        }

        string code = Regex.Replace(
            csproj,
            "<!--.*?-->",
            string.Empty,
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            RegexTimeout
        );
        if (
            hasPaketReferences
            && !Regex.IsMatch(
                code,
                @"<Import\s+Project=""[^""]*\.paket[\\/]Paket\.Restore\.targets""",
                RegexOptions.CultureInvariant,
                RegexTimeout
            )
        )
        {
            problems.Add($"{name}: paket.references but no Paket.Restore.targets import");
        }

        return problems;
    }

    [Fact]
    public void EveryRestoreOfALockedProjectInTheWorkflowsRunsInLockedMode()
    {
        string root = FindRepositoryRoot();
        Regex restore = new(
            @"^\s*run:\s+dotnet restore (?<target>\S+)(?<rest>[^\n]*)$",
            Options,
            RegexTimeout
        );
        int examined = 0;
        foreach (
            string file in EnumerateYaml(
                Path.Combine(root, ".github", "workflows"),
                "*",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            foreach (Match m in restore.Matches(IOFile.ReadAllText(file).Replace("\r\n", "\n")))
            {
                examined++;
                m.Groups["rest"]
                    .Value.ShouldContain(
                        "--locked-mode",
                        customMessage: $"{Path.GetFileName(file)}: dotnet restore {m.Groups["target"].Value}"
                    );
            }
        }

        examined.ShouldBeGreaterThanOrEqualTo(8, "the workflow scan examined too few restores");
    }

    // `run: dotnet restore ...`, or the same command on a line of a `run: |` block. The strict
    // Paket step itself must be a single-line `run:`, so a block-style one is reported, not missed.
    private static readonly Regex RestoreStep = new(
        @"^\s*(?:(?:-\s+)?run:\s+)?dotnet restore\b",
        Options,
        RegexTimeout
    );

    private static readonly Regex StrictPaketRestoreStep = new(
        @"^\s*(?:-\s+)?run:\s+dotnet tool run paket restore --fail-on-checks\s*$",
        Options,
        RegexTimeout
    );

    // `run: dotnet tool restore ...`, or the same command on a line of a `run: |` block.
    private static readonly Regex ToolRestoreLine = new(
        @"^\s*(?:(?:-\s+)?run:\s+)?dotnet tool restore\b",
        Options,
        RegexTimeout
    );

    [Fact]
    public void EveryJobThatRestoresTheSolutionFirstRunsTheStrictPaketRestore()
    {
        // ADR 0004: plain `paket restore` exits 0 when paket.dependencies and paket.lock disagree,
        // and the solution restore calls Paket for the Paket-managed projects without the check.
        // So every job that restores runs `dotnet tool run paket restore --fail-on-checks` first, after the
        // local tools (Paket is one) are restored.
        string root = FindRepositoryRoot();
        string[] files = EnumerateYaml(
                Path.Combine(root, ".github", "workflows"),
                "*",
                SearchOption.TopDirectoryOnly
            )
            .ToArray();
        files.ShouldContain(f => f.EndsWith("ci.yml", StringComparison.Ordinal));

        var problems = new List<string>();
        int examined = 0;
        foreach (string file in files)
        {
            (int restores, List<string> found) = PaketRestoreProblems(
                IOFile.ReadAllText(file).Replace("\r\n", "\n")
            );
            examined += restores;
            problems.AddRange(found.Select(p => $"{Path.GetFileName(file)}: {p}"));
        }

        // ci.yml (test, derived), benchmarks.yml (two jobs), fuzz, metrics-oracle, mutation, release.
        examined.ShouldBeGreaterThanOrEqualTo(8, "the workflow scan examined too few restores");
        problems.ShouldBeEmpty();
    }

    private const string GoodPaketJob = """
        on: push
        jobs:
          test:
            runs-on: ubuntu-latest
            steps:
              - name: Restore local tools
                run: |
                  # retry for dotnet/sdk #53783
                  dotnet tool restore --disable-parallel || dotnet tool restore --disable-parallel
              - name: Restore Paket dependencies (strict)
                run: dotnet tool run paket restore --fail-on-checks
              - name: Restore solution dependencies
                run: dotnet restore Parquet.SourceGenerator.slnx --locked-mode
        """;

    [Fact]
    public void ThePaketRestoreRuleAcceptsTheShapeTheWorkflowsUse()
    {
        (int examined, List<string> problems) = PaketRestoreProblems(GoodPaketJob);

        examined.ShouldBe(1);
        problems.ShouldBeEmpty();
    }

    [Theory]
    // The step removed.
    [InlineData(
        "      - name: Restore Paket dependencies (strict)\n        run: dotnet tool run paket restore --fail-on-checks\n",
        ""
    )]
    // Without the switch, drift between paket.dependencies and paket.lock exits 0.
    [InlineData(
        "run: dotnet tool run paket restore --fail-on-checks",
        "run: dotnet tool run paket restore"
    )]
    // The `dotnet paket` shorthand: AGENTS.md rule 10 requires `dotnet tool run <command>`.
    [InlineData(
        "run: dotnet tool run paket restore --fail-on-checks",
        "run: dotnet paket restore --fail-on-checks"
    )]
    // Commented out.
    [InlineData(
        "        run: dotnet tool run paket restore --fail-on-checks",
        "        # run: dotnet tool run paket restore --fail-on-checks"
    )]
    // Echoed rather than run.
    [InlineData(
        "run: dotnet tool run paket restore --fail-on-checks",
        "run: echo dotnet tool run paket restore --fail-on-checks"
    )]
    // A block-style restore with the strict step removed.
    [InlineData(
        "      - name: Restore Paket dependencies (strict)\n        run: dotnet tool run paket restore --fail-on-checks\n      - name: Restore solution dependencies\n        run: dotnet restore Parquet.SourceGenerator.slnx --locked-mode",
        "      - name: Restore solution dependencies\n        run: |\n          set -euo pipefail\n          dotnet restore Parquet.SourceGenerator.slnx --locked-mode"
    )]
    // After the restore it was meant to guard (the two commands swapped).
    [InlineData(
        "run: dotnet tool run paket restore --fail-on-checks\n      - name: Restore solution dependencies\n        run: dotnet restore Parquet.SourceGenerator.slnx --locked-mode",
        "run: dotnet restore Parquet.SourceGenerator.slnx --locked-mode\n      - name: Restore solution dependencies\n        run: dotnet tool run paket restore --fail-on-checks"
    )]
    // Before the local tools exist, so `dotnet tool run paket` is not there to run.
    [InlineData(
        "          dotnet tool restore --disable-parallel || dotnet tool restore --disable-parallel",
        "          echo no tools"
    )]
    public void ThePaketRestoreRuleRejectsAJobWithoutAStrictPaketRestoreFirst(
        string original,
        string broken
    )
    {
        string mutated = GoodPaketJob.Replace(original, broken, StringComparison.Ordinal);
        mutated.ShouldNotBe(GoodPaketJob);

        (int examined, List<string> problems) = PaketRestoreProblems(mutated);

        examined.ShouldBe(1);
        problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void ThePaketRestoreRuleRejectsAStrictRestoreThatOnlyAnotherJobRuns()
    {
        string split = GoodPaketJob.Replace(
            "      - name: Restore solution dependencies\n",
            "  other:\n    runs-on: ubuntu-latest\n    steps:\n      - name: Restore solution dependencies\n",
            StringComparison.Ordinal
        );
        split.ShouldNotBe(GoodPaketJob);

        (int examined, List<string> problems) = PaketRestoreProblems(split);

        examined.ShouldBe(1);
        problems.ShouldNotBeEmpty();
    }

    // Per job: every `dotnet restore` step must come after a `dotnet tool run paket restore
    // --fail-on-checks` step, which must come after `dotnet tool restore`. Comment lines do not
    // count. Returns how many restores were examined and what is wrong.
    private static (int Examined, List<string> Problems) PaketRestoreProblems(string workflow)
    {
        var problems = new List<string>();
        int examined = 0;
        string jobs = JobsSection(workflow);
        MatchCollection keys = JobKey.Matches(jobs);
        for (int i = 0; i < keys.Count; i++)
        {
            int end = i + 1 < keys.Count ? keys[i + 1].Index : jobs.Length;
            string job = CommentLine.Replace(
                jobs.Substring(keys[i].Index, end - keys[i].Index),
                string.Empty
            );
            string id = keys[i].Groups["id"].Value;
            Match paket = StrictPaketRestoreStep.Match(job);
            Match tools = ToolRestoreLine.Match(job);
            foreach (Match restore in RestoreStep.Matches(job))
            {
                examined++;
                if (!paket.Success || paket.Index > restore.Index)
                {
                    problems.Add(
                        $"job {id}: `dotnet restore` without a `dotnet tool run paket restore --fail-on-checks` step before it"
                    );
                }
                else if (!tools.Success || tools.Index > paket.Index)
                {
                    problems.Add(
                        $"job {id}: `dotnet tool run paket restore` runs before `dotnet tool restore`"
                    );
                }
            }
        }

        return (examined, problems);
    }

    [Fact]
    public void NuGetSourcesAreMappedAndThePackageConsumersPinOurIdsToTheLocalFeed()
    {
        string root = FindRepositoryRoot();
        // Parsed, not matched as text: a mapping inside an XML comment is not applied by NuGet.
        MappingsOf(Path.Combine(root, "NuGet.config"))
            .ShouldContain(("nuget.org", "*"), "the root config must map every id to nuget.org");

        foreach (string consumer in new[] { "PackageConsumption", "PackageConsumptionLegacy" })
        {
            var mappings = MappingsOf(Path.Combine(root, "test", consumer, "nuget.config"));
            mappings.ShouldContain(
                ("local-artifacts", "Parquet.SourceGenerator*"),
                $"{consumer}: Parquet.SourceGenerator* must map to local-artifacts"
            );
            mappings.ShouldContain(
                ("nuget.org", "*"),
                $"{consumer}: the rest must come from nuget.org"
            );
        }
    }

    private static List<(string Source, string Pattern)> MappingsOf(string path) =>
        XDocument
            .Load(path)
            .Descendants("packageSourceMapping")
            .Descendants("packageSource")
            .SelectMany(source =>
                source
                    .Elements("package")
                    .Select(p =>
                        ((string)source.Attribute("key")!, (string)p.Attribute("pattern")!)
                    )
            )
            .ToList();

    [Fact]
    public void TheReadmeExamplesAreCompiledInTheTestJobAfterTheSolutionBuild()
    {
        // #596: the README gate is a step of the `test` job, so it reaches `build` through the
        // aggregate; it must come after the build that produces the generator it references.
        string ci = Read(FindRepositoryRoot(), ".github", "workflows", "ci.yml");
        string testJob = JobBlock(ci, "test");

        // Match the command as a step's `run:` value, so a comment or an `echo` of it does not count.
        Match build = Regex.Match(
            testJob,
            @"^\s+run:\s+dotnet build Parquet\.SourceGenerator\.slnx\b",
            Options,
            RegexTimeout
        );
        Match readme = Regex.Match(
            testJob,
            @"^\s+run:\s+dotnet run scripts/CheckReadme\.cs\s*$",
            Options,
            RegexTimeout
        );

        build.Success.ShouldBeTrue("the solution build step");
        readme.Success.ShouldBeTrue("an executable README-check step");
        readme.Index.ShouldBeGreaterThan(build.Index);
        IOFile
            .Exists(
                Path.Combine(
                    FindRepositoryRoot(),
                    "test",
                    "Parquet.SourceGenerator.ReadmeSnippets",
                    "Parquet.SourceGenerator.ReadmeSnippets.csproj"
                )
            )
            .ShouldBeTrue();
    }

    private static void AssertNoStandaloneNet472Workflow(string root) =>
        IOFile
            .Exists(Path.Combine(root, ".github", "workflows", "net472-consumer.yml"))
            .ShouldBeFalse(
                "the standalone workflow was moved into ci.yml; keeping both runs the job twice"
            );

    [Fact]
    public void TheNet472ConsumerIsExecutedOnWindowsNotJustCompiled()
    {
        string root = FindRepositoryRoot();
        // The job is in ci.yml and in `build`'s needs (#565): the aggregate test above proves the
        // latter for every job, this one pins what the job does and that nothing can skip it.
        string ci = Read(root, ".github", "workflows", "ci.yml");
        string workflow = JobBlock(ci, "net472-consumer");
        AssertNoStandaloneNet472Workflow(root);

        workflow.ShouldContain("runs-on: windows-latest");
        Regex
            .IsMatch(
                JobBlock(ci, "build"),
                @"needs:\s*\[[^\]]*\bnet472-consumer\b",
                Options,
                RegexTimeout
            )
            .ShouldBeTrue("build must need net472-consumer");
        // No `if:` at job level and no path filter on the workflow triggers, so it cannot be skipped.
        Regex
            .IsMatch(workflow, @"^    if:", Options, RegexTimeout)
            .ShouldBeFalse("net472-consumer must not be conditional");
        ci.Substring(0, ci.IndexOf("\njobs:\n", StringComparison.Ordinal))
            .ShouldNotContain("paths:");

        // `dotnet build --framework net472` is what ci.yml already does; the gate is the run.
        Regex
            .IsMatch(
                workflow,
                @"dotnet run --project \S*PackageConsumptionLegacy\.csproj\s*\\\n\s*--framework net472",
                Options,
                RegexTimeout
            )
            .ShouldBeTrue("the workflow must `dotnet run` the consumer on net472");
        workflow.ShouldNotContain("continue-on-error");

        // The consumer must keep the scenario that proves it does not deadlock a blocked caller.
        string program = Read(root, "test", "PackageConsumptionLegacy", "Program.cs");
        program.ShouldContain("SynchronizationContextScenario.Run()");
    }

    [Fact]
    public void TheProtectedPathsCheckRunsTheBaseBranchAndNeverChecksOutThePullRequest()
    {
        string root = FindRepositoryRoot();
        string workflow = Read(root, ".github", "workflows", "protected-paths.yml");

        JobName(JobBlock(workflow, "protected-paths")).ShouldBe("protected-paths");

        // pull_request_target hands the job a token and the base branch's copy of this file,
        // which is what stops a fork editing its way past the check. That is only safe while no
        // step checks out or runs the pull request's own code.
        workflow.ShouldContain("pull_request_target:");
        string code = CommentLine.Replace(workflow, string.Empty);
        code.ShouldNotContain("github.event.pull_request.head.sha");
        code.ShouldNotContain("github.event.pull_request.head.ref");
        code.ShouldNotContain("github.head_ref");
        code.ShouldNotContain("refs/pull/");
        Regex.IsMatch(code, @"^\s+ref:", Options, RegexTimeout).ShouldBeFalse();

        // It must fail closed: an unreadable or truncated file listing is a failure, not a pass.
        workflow.ShouldContain("refusing to pass unchecked");
        workflow.ShouldContain("changed_files");

        // The list it reads must exist on the base branch and name something.
        IOFile.Exists(Path.Combine(root, ".github", "protected-paths.txt")).ShouldBeTrue();
        string list = Read(root, ".github", "protected-paths.txt");
        list.Split('\n')
            .Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .ShouldBeGreaterThan(0);
    }

    private const string GoodAggregate = """
          build:
            name: build
            if: always()
            needs: [test, derived]
            steps:
              - name: Require
                env:
                  TEST_RESULT: ${{ needs.test.result }}
                  DERIVED_RESULT: ${{ needs.derived.result }}
                run: |
                  echo "test: $TEST_RESULT"
                  if [ "$TEST_RESULT" != "success" ] || [ "$DERIVED_RESULT" != "success" ]; then
                    echo "::error::no"
                    exit 1
                  fi
        """;

    [Fact]
    public void TheAggregateRuleAcceptsTheShapeCiUses() =>
        AggregateProblems(GoodAggregate, ["test", "derived"]).ShouldBeEmpty();

    [Theory]
    [InlineData(
        "[ \"$TEST_RESULT\" != \"success\" ] || [ \"$DERIVED_RESULT\" != \"success\" ]",
        "[ \"$TEST_RESULT\" != \"success\" -a \"$DERIVED_RESULT\" != \"success\" ]"
    )]
    [InlineData(
        "[ \"$TEST_RESULT\" != \"success\" ] || [ \"$DERIVED_RESULT\" != \"success\" ]",
        "[ \"$TEST_RESULT\" != \"success\" ] && [ \"$DERIVED_RESULT\" != \"success\" ]"
    )]
    [InlineData(
        "[ \"$TEST_RESULT\" != \"success\" ] || [ \"$DERIVED_RESULT\" != \"success\" ]",
        "[ \"$TEST_RESULT\" != \"success\" ]"
    )]
    [InlineData(
        "[ \"$TEST_RESULT\" != \"success\" ] || [ \"$DERIVED_RESULT\" != \"success\" ]",
        "[ \"$TEST_RESULT\" = \"failure\" ] || [ \"$DERIVED_RESULT\" != \"success\" ]"
    )]
    [InlineData("exit 1", "exit 0")]
    public void TheAggregateRuleRejectsPredicatesThatDoNotFailOnAnyJob(
        string original,
        string broken
    )
    {
        string mutated = GoodAggregate.Replace(original, broken, StringComparison.Ordinal);
        mutated.ShouldNotBe(GoodAggregate);

        AggregateProblems(mutated, ["test", "derived"]).ShouldNotBeEmpty();
    }

    // Scans a workflow or action file for `uses` references. Local references (`./...`) are
    // versioned with this tree and are not policed; everything else must end in a 40-hex SHA.
    private static (int Examined, string[] Mutable) ScanUses(string yaml)
    {
        string text = CommentLine.Replace(yaml.Replace("\r\n", "\n"), string.Empty);
        string[] references = UsesKey
            .Matches(text)
            .Select(m => m.Groups["ref"].Value)
            .Where(r => !r.StartsWith("./", StringComparison.Ordinal))
            .ToArray();
        return (references.Length, references.Where(r => !FullSha.IsMatch(r)).ToArray());
    }

    private static string JobName(string jobBlock)
    {
        Match name = JobNameKey.Match(jobBlock);
        name.Success.ShouldBeTrue("the job declares no `name:` of its own");
        return name.Groups["name"].Value.Trim('"', '\'');
    }

    // The aggregate-gate trap, as rules: every gated job's result is bound to a variable, every
    // variable appears in the failing test as `"$VAR" != "success"`, the clauses are joined only
    // by `||`, and the failing branch exits non-zero. Returns what is wrong; empty means sound.
    private static List<string> AggregateProblems(string aggregate, IEnumerable<string> gated)
    {
        var problems = new List<string>();
        string[] lines = aggregate.Split('\n');
        int ifLine = Array.FindIndex(
            lines,
            l => l.TrimStart().StartsWith("if [", StringComparison.Ordinal)
        );
        if (ifLine < 0)
        {
            problems.Add("the build job must decide in an `if [ ... ]; then` test");
            return problems;
        }

        string decision = lines[ifLine].Trim();
        foreach (string job in gated)
        {
            Match variable = Regex.Match(
                aggregate,
                @"^\s+(?<name>[A-Z_][A-Z0-9_]*):\s*\$\{\{\s*needs\."
                    + Regex.Escape(job)
                    + @"\.result\s*\}\}",
                Options,
                RegexTimeout
            );
            if (!variable.Success)
            {
                problems.Add($"build must bind needs.{job}.result to a variable");
                continue;
            }

            string clause = $"[ \"${variable.Groups["name"].Value}\" != \"success\" ]";
            if (!decision.Contains(clause, StringComparison.Ordinal))
            {
                problems.Add($"build must fail unless {job} succeeded: missing {clause}");
            }

            decision = decision.Replace(clause, string.Empty, StringComparison.Ordinal);
        }

        // What remains once every required clause is removed must be `if` + `||` + `; then`.
        string residue = Regex.Replace(decision, @"[\s|]", string.Empty, Options, RegexTimeout);
        if (!string.Equals(residue, "if;then", StringComparison.Ordinal))
        {
            problems.Add($"the decision joins clauses other than with `||`: '{decision}'");
        }

        int fi = Array.FindIndex(
            lines,
            ifLine + 1,
            l => string.Equals(l.Trim(), "fi", StringComparison.Ordinal)
        );
        string failureBranch =
            fi < 0 ? string.Empty : string.Join("\n", lines.Skip(ifLine + 1).Take(fi - ifLine - 1));
        if (!failureBranch.Contains("exit 1", StringComparison.Ordinal))
        {
            problems.Add("the failing branch must `exit 1`");
        }

        if (failureBranch.Contains("exit 0", StringComparison.Ordinal))
        {
            problems.Add("the failing branch must not `exit 0`");
        }

        return problems;
    }

    private static string[] WorkflowAndActionFiles(string root)
    {
        string workflows = Path.Combine(root, ".github", "workflows");
        string actions = Path.Combine(root, ".github", "actions");
        IEnumerable<string> workflowFiles = EnumerateYaml(
            workflows,
            "*",
            SearchOption.TopDirectoryOnly
        );
        IEnumerable<string> actionFiles = Directory.Exists(actions)
            ? EnumerateYaml(actions, "action", SearchOption.AllDirectories)
            : [];
        return workflowFiles.Concat(actionFiles).OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    // GitHub reads both extensions; scanning only one would let the other bypass the policy.
    private static IEnumerable<string> EnumerateYaml(
        string directory,
        string stem,
        SearchOption option
    ) =>
        Directory
            .EnumerateFiles(directory, stem + ".yml", option)
            .Concat(Directory.EnumerateFiles(directory, stem + ".yaml", option));

    private static string JobsSection(string workflow)
    {
        int start = workflow.IndexOf("\njobs:\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "workflow has no jobs: section");
        return workflow.Substring(start + "\njobs:\n".Length);
    }

    private static string JobBlock(string workflow, string jobId)
    {
        string jobs = JobsSection(workflow);
        MatchCollection keys = JobKey.Matches(jobs);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!string.Equals(keys[i].Groups["id"].Value, jobId, StringComparison.Ordinal))
            {
                continue;
            }

            int end = i + 1 < keys.Count ? keys[i + 1].Index : jobs.Length;
            return jobs.Substring(keys[i].Index, end - keys[i].Index);
        }

        throw new InvalidOperationException($"Job '{jobId}' not found.");
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
            if (
                IOFile.Exists(Path.Combine(directory.FullName, ".github", "workflows", "ci.yml"))
                && IOFile.Exists(
                    Path.Combine(directory.FullName, ".github", "workflows", "pr-title.yml")
                )
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root containing the CI workflows."
        );
    }
}
