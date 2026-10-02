using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parquet.SourceGenerator.Tools;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Protects the CI/release test-data verification boundary from drifting back into two copies.
/// </summary>
public sealed class WorkflowConsistencyTests
{
    private const string SharedAction = "./.github/actions/verify-test-data";
    private const string HashStep =
        "- name: Verify Checked-In Dataset Hash Integrity (pre-regeneration)";
    private const string PythonGenerationStep =
        "- name: Generate Python Test Datasets (PyArrow v1 & v2)";
    private const string CSharpGenerationStep = "- name: Generate C# Test Datasets (Parquet.Net)";
    private const string BaselineRelativePath = "benchmarks/baseline.json";
    private const string GateFilterRelativePath = "benchmarks/gate-filter.txt";

    [Fact]
    public void CiAndReleaseDelegateTheTestDataSequenceToOneSharedAction()
    {
        string root = FindRepositoryRoot();
        string action = Read(root, ".github", "actions", "verify-test-data", "action.yml");
        string ci = Read(root, ".github", "workflows", "ci.yml");
        string release = Read(root, ".github", "workflows", "release.yml");

        AssertWorkflowDelegatesOnce(ci);
        AssertWorkflowDelegatesOnce(release);

        foreach (string step in new[] { HashStep, PythonGenerationStep, CSharpGenerationStep })
        {
            Count(action, step).ShouldBe(1, $"Shared action must define '{step}' exactly once.");
            Count(ci, step).ShouldBe(0, $"ci.yml must not duplicate '{step}'.");
            Count(release, step).ShouldBe(0, $"release.yml must not duplicate '{step}'.");
        }

        int hashIndex = action.IndexOf(HashStep, StringComparison.Ordinal);
        int pythonIndex = action.IndexOf(PythonGenerationStep, StringComparison.Ordinal);
        int csharpIndex = action.IndexOf(CSharpGenerationStep, StringComparison.Ordinal);
        hashIndex.ShouldBeLessThan(pythonIndex);
        pythonIndex.ShouldBeLessThan(csharpIndex);

        action.ShouldContain(
            "PARQUET_TEST_DATA_OUTPUT_DIR: ${{ runner.temp }}/parquet-source-generator/data"
        );
        action.ShouldContain(
            "PARQUET_TEST_DATA_CSHARP_OUTPUT_DIR: ${{ runner.temp }}/parquet-source-generator/data_csharp"
        );
    }

    [Fact]
    public void MetricsOracleStagesTheMatchingRoslynBuildHostAndClassifiesProvisioningFailures()
    {
        string root = FindRepositoryRoot();
        string workflow = Read(root, ".github", "workflows", "metrics-oracle.yml");

        workflow.ShouldContain("METRICS_VERSION: 5.6.0");
        workflow.ShouldContain("METRICS_WORKSPACES_VERSION: 4.12.0");
        workflow.ShouldContain(
            "nuget install Microsoft.CodeAnalysis.Workspaces.MSBuild -Version \"$METRICS_WORKSPACES_VERSION\""
        );
        workflow.ShouldContain("contentFiles/any/any/BuildHost-netcore");
        workflow.ShouldContain(
            "test -s \"$build_host_source/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll\""
        );
        workflow.ShouldContain("cp -R \"$build_host_source/.\" \"$build_host_destination/\"");
        workflow.ShouldContain(
            "test -s \"$build_host_destination/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll\""
        );
        workflow.ShouldContain("metrics_outcome=\"${{ steps.run_metrics.outcome }}\"");
        workflow.ShouldContain(
            "if [ \"$metrics_outcome\" = \"success\" ] && [ \"$compare_outcome\" != \"success\" ]; then"
        );
        workflow.ShouldContain("label=metrics-oracle-infrastructure");
        workflow.ShouldContain("Metrics.exe execution failed");
        workflow.ShouldContain(
            "gh issue create --title \"$title\" --body \"$body\" --label \"$label\""
        );

        int installIndex = workflow.IndexOf(
            "nuget install Microsoft.CodeAnalysis.Workspaces.MSBuild",
            StringComparison.Ordinal
        );
        int hostCheckIndex = workflow.IndexOf(
            "test -s \"$build_host_destination/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll\"",
            StringComparison.Ordinal
        );
        int runIndex = workflow.IndexOf(
            "- name: Run Metrics.exe over both generator projects",
            StringComparison.Ordinal
        );
        installIndex.ShouldBeGreaterThanOrEqualTo(0);
        hostCheckIndex.ShouldBeGreaterThan(installIndex);
        runIndex.ShouldBeGreaterThan(hostCheckIndex);
    }

    /// <summary>
    /// The regression gate has to be invoked to be a gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RegressionCheck</c> only runs when the summary generator is passed <c>--baseline</c>.
    /// Until #404 no workflow passed it, so the comparison logic, the allocation tolerance and the
    /// unit tests around them gated nothing: any regression merged while the benchmark job
    /// reported success. Unit tests on the comparison cannot catch that — only the wiring can.
    /// </para>
    /// <para>
    /// The missing-baseline assertions guard the failure in #412 from the other direction: a check
    /// run that finds no reference must stop, not record the run it was supposed to judge as the
    /// new truth.
    /// </para>
    /// </remarks>
    [Fact]
    public void BenchmarksWorkflowRunsTheGeneratorAsARegressionGate()
    {
        string root = FindRepositoryRoot();
        string workflow = Read(root, ".github", "workflows", "benchmarks.yml");

        workflow.ShouldContain($"BENCHMARK_BASELINE: {BaselineRelativePath}");
        workflow.ShouldContain($"BENCHMARK_GATE_FILTER: {GateFilterRelativePath}");
        workflow.ShouldContain("- name: Benchmark Regression Gate");
        Count(workflow, "--baseline \"$BENCHMARK_BASELINE\" --report")
            .ShouldBe(
                1,
                "The gate must invoke the generator with --baseline and capture a report."
            );

        // The gate fails when a baseline benchmark did not run, when a run benchmark is not in the
        // baseline, and never compares wall-clock against a baseline from other hardware.
        workflow.ShouldContain("--fail-on-not-run --fail-on-new --no-time");

        // The runtime is pinned: the benchmark project rolls forward to the newest installed major
        // and allocations differ between runtimes.
        workflow.ShouldContain("DOTNET_ROLL_FORWARD: Minor");

        // A baseline that is absent, or present but empty, must stop the job rather than being
        // bootstrapped from the very run under test.
        workflow.ShouldContain("if [ ! -f \"$BENCHMARK_BASELINE\" ]; then");
        workflow.ShouldContain("(.measurements // []) | length");
        workflow.ShouldContain("contains no measurements");

        // And a check run must not be able to rewrite the reference it is checking against.
        workflow.ShouldContain("The regression check rewrote its own baseline");

        // Recording stays an explicit dispatch, and what it records is an artifact for a person to
        // commit, never a push or a pull request from the workflow.
        Count(workflow, "--update-baseline")
            .ShouldBe(1, "Only the explicit update_baseline dispatch may record a baseline.");
        workflow.ShouldContain("name: benchmark-baseline-candidate");
        workflow.ShouldNotContain("git add \"$BENCHMARK_BASELINE\"");

        int recordIndex = workflow.IndexOf(
            "- name: Record a candidate baseline",
            StringComparison.Ordinal
        );
        int gateIndex = workflow.IndexOf(
            "- name: Benchmark Regression Gate",
            StringComparison.Ordinal
        );
        recordIndex.ShouldBeGreaterThanOrEqualTo(0);
        gateIndex.ShouldBeGreaterThan(recordIndex);
    }

    /// <summary>
    /// A failing Sunday run must reach a person (#564), through a job that holds
    /// <c>issues: write</c> and runs no repository code.
    /// </summary>
    [Fact]
    public void BenchmarksWorkflowNotifiesOnScheduledFailureWithoutWidePermissions()
    {
        string root = FindRepositoryRoot();
        string workflow = Read(root, ".github", "workflows", "benchmarks.yml");

        string workflowHeader = workflow[..workflow.IndexOf("\njobs:\n", StringComparison.Ordinal)];
        workflowHeader.ShouldNotContain("\n  issues: write");

        string notify = workflow[
            workflow.IndexOf("\n  notify-failure:", StringComparison.Ordinal)..
        ];
        notify.ShouldContain("needs: [run-benchmarks, regression-gate]");
        notify.ShouldContain("github.event_name == 'schedule'");
        notify.ShouldContain("\n      issues: write\n");
        notify.ShouldNotContain("actions/checkout");
        notify.ShouldContain("title=\"[CI] scheduled benchmarks failed\"");
        Count(workflow, "\n      issues: write\n")
            .ShouldBe(1, "Only the notification job may hold issues: write.");
    }

    /// <summary>
    /// The gated list, the baseline and the examined-N rule have to agree: every listed benchmark
    /// has a baseline, and every baseline entry is listed. Otherwise the gate either fails on
    /// day one or silently stops covering something.
    /// </summary>
    [Fact]
    public void GatedBenchmarkListAndBaselineDescribeTheSameBenchmarks()
    {
        string root = FindRepositoryRoot();
        string[] globs = Read(root, "benchmarks", "gate-filter.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();

        globs.ShouldNotBeEmpty("An empty gate list is a gate that runs nothing.");
        globs.ShouldAllBe(glob => glob != "*", "The gated set is curated, not the whole suite.");

        IReadOnlyList<BenchmarkMeasurement> baseline = RegressionCheck.ParseBaseline(
            Read(root, "benchmarks", "baseline.json")
        );
        baseline.Count.ShouldBeGreaterThan(
            0,
            $"'{BaselineRelativePath}' records no measurements; the gate would pass vacuously."
        );
        baseline.ShouldAllBe(m => m.AllocatedBytes >= 0);

        // "*Class.Method" -> (Class, Method)
        var listed = globs
            .Select(glob => glob.TrimStart('*').Split('.'))
            .Select(parts => (Type: parts[0], Method: parts[1]))
            .ToHashSet();
        var recorded = baseline.Select(m => (m.Type, m.Method)).ToHashSet();

        listed.Except(recorded).ShouldBeEmpty("Listed in gate-filter.txt but not in the baseline.");
        recorded.Except(listed).ShouldBeEmpty("In the baseline but not listed in gate-filter.txt.");

        // Class + method + parameters: no two baseline entries may share an identity, and the
        // method name alone is not unique across the suite.
        baseline
            .Select(m => m.Key)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(baseline.Count);
    }

    private static void AssertWorkflowDelegatesOnce(string workflow)
    {
        Count(workflow, $"uses: {SharedAction}").ShouldBe(1);
    }

    private static string Read(string root, params string[] segments) =>
        IOFile
            .ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()))
            .Replace("\r\n", "\n");

    private static int Count(string value, string needle)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (
                IOFile.Exists(Path.Combine(directory.FullName, ".github", "workflows", "ci.yml"))
                && IOFile.Exists(
                    Path.Combine(
                        directory.FullName,
                        ".github",
                        "actions",
                        "verify-test-data",
                        "action.yml"
                    )
                )
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root containing the CI workflow and shared verification action."
        );
    }
}
