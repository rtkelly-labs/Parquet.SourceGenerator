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
/// Guards the wiring around the CI gates rather than any one gate (docs/51-CI-GATE-MATRIX.md):
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
    public void EveryProjectInTheSolutionHasALockFileExceptTheNativeAotOnes()
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
        foreach (string project in projects)
        {
            string name = Path.GetFileNameWithoutExtension(project);
            bool expected = !ProjectsWithoutLockFile.Contains(name);
            string lockFile = Path.Combine(
                root,
                Path.GetDirectoryName(project)!,
                "packages.lock.json"
            );
            IOFile.Exists(lockFile).ShouldBe(expected, $"{name}: packages.lock.json");
        }
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
