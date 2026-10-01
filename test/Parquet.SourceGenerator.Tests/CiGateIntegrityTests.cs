using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
