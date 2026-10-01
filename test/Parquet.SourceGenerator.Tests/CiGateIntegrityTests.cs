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
/// code nobody pinned, protects nothing. Each test asserts it examined something, so none of
/// them passes by finding no workflows.
/// </summary>
public sealed class CiGateIntegrityTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // `uses: owner/repo[/path]@ref`, with an optional list dash and trailing comment.
    private static readonly Regex UsesLine = new(
        @"^\s*(?:-\s*)?uses:\s*(?<ref>[^\s#]+)",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        RegexTimeout
    );

    private static readonly Regex FullSha = new(
        @"@[0-9a-f]{40}$",
        RegexOptions.CultureInvariant,
        RegexTimeout
    );

    // A job key: exactly two spaces of indent, a bare identifier, nothing after the colon.
    private static readonly Regex JobKey = new(
        @"^  (?<id>[A-Za-z0-9_-]+):\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
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
            foreach (Match match in UsesLine.Matches(IOFile.ReadAllText(file)))
            {
                string reference = match.Groups["ref"].Value;
                if (reference.StartsWith("./", StringComparison.Ordinal))
                {
                    continue; // a local action or reusable workflow, versioned with this tree
                }

                examined++;
                if (!FullSha.IsMatch(reference))
                {
                    unpinned.Add($"{Path.GetRelativePath(root, file)}: {reference}");
                }
            }
        }

        examined.ShouldBeGreaterThan(10, "expected the workflows to reference many actions");
        unpinned.ShouldBeEmpty(
            "mutable action tags can be moved by their owner; pin a 40-character commit SHA"
        );
    }

    [Fact]
    public void TheRequiredCheckNamesAreTheJobNamesBranchProtectionMatchesOn()
    {
        string root = FindRepositoryRoot();

        // Branch protection matches a check by name string: `build` and `pr-title`.
        string ci = Read(root, ".github", "workflows", "ci.yml");
        JobBlock(ci, "build").ShouldContain("name: build\n");

        string prTitle = Read(root, ".github", "workflows", "pr-title.yml");
        JobBlock(prTitle, "pr-title").ShouldContain("name: pr-title\n");
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
            RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
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

        // The aggregate-gate trap: only an exact "success" passes. A skipped, cancelled or
        // never-reported job must fail the aggregate, not be forgiven by an empty comparison.
        // Mentioning a result in an env var or an echo is not deciding on it: each job's result
        // variable must appear in the failing comparison itself.
        string decision = string.Join(
            "\n",
            aggregate
                .Split('\n')
                .Where(l => l.TrimStart().StartsWith("if [", StringComparison.Ordinal))
        );
        decision.ShouldNotBeEmpty("the build job must decide in an `if [ ... ]` test");
        foreach (string job in gated)
        {
            Match variable = Regex.Match(
                aggregate,
                @"^\s+(?<name>[A-Z_][A-Z0-9_]*):\s*\$\{\{\s*needs\."
                    + Regex.Escape(job)
                    + @"\.result\s*\}\}",
                RegexOptions.Multiline
                    | RegexOptions.ExplicitCapture
                    | RegexOptions.CultureInvariant,
                RegexTimeout
            );
            variable.Success.ShouldBeTrue($"build must bind needs.{job}.result to a variable");
            decision.ShouldContain(
                $"\"${variable.Groups["name"].Value}\" != \"success\"",
                customMessage: $"build must fail unless {job} succeeded"
            );
        }
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
