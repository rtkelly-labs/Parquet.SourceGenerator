# 24 — The Metrics Oracle (#254): Metrics.exe against CodeMetrics.cs

Layer 1's numbers ([21 — Code Metrics](./21-CODE-METRICS.md)) are computed by `scripts/CodeMetrics.cs` — a bespoke, cross-platform program. It exists because
`Microsoft.CodeAnalysis.Metrics` ships `Metrics.exe`, a .NET Framework **Windows-only**
executable, which cannot run on this repository's `ubuntu-latest` CI.

"Cannot run on the current runner" is not "cannot be in CI", though. A nightly
`windows-latest` job can run the vendor tool and ask it the one question the bespoke
computation can never answer about itself:

> **Is our ruler accurate?**

If `CodeMetrics.cs` enumerates members differently from Microsoft's tool — a treatment of
accessors, partials or nested types that quietly diverges — then every number it reports is
wrong **in the same direction**, and nothing downstream has an opinion about correctness;
only an independent oracle does. This is the same reasoning that put PyArrow (#165), DuckDB
(#166) and pinned `parquet-cli` (#183) around the *generated files*; the metrics were the
last self-reported number in the quality stack without a cross-check.

## The mechanism

`.github/workflows/metrics-oracle.yml`, nightly (`30 5 * * *`) plus manual dispatch:

1. `scripts/CodeMetrics.cs -- --src-only --out $RUNNER_TEMP/metrics` measures the checkout's
   `src/` (layer 1 only — the generated-code layer needs the golden models the test suite
   publishes, and the oracle does not compare it). Nothing is read from a checked-in file: both
   tools measure the same commit, in the same job.
2. `nuget install Microsoft.CodeAnalysis.Metrics` (version pinned in the workflow env), stage
   the matching Roslyn MSBuild build host, and run `Metrics.exe` over both generator projects in
   one invocation, emitting one XML report.
3. `scripts/MetricsOracleCompare.cs` reads the XML and the matching report from step 1 and
   compares them **per type**: MI within ±2 (the index bottoms out in a cube root), CC / CL /
   SLOC exact.
4. Any disagreement fails the job **and** opens or updates a `metrics-oracle`-labelled
   GitHub issue with the run link. A silently-red schedule gets muted; an issue gets acted
   on — the reporting lesson of #189, which #261 also adopts.

## What is gated, and what is only reported

**Gated: type-level agreement.** The report lists every hand-written type in each
assembly; the oracle must report the same metrics for each. A reported type the oracle does
not name is itself a failure (it means the two tools disagree about what exists).

**Reported, never gated: assembly totals.** The two tools disagree about scope before they
ever disagree about arithmetic — how source generators, partials and generated files enter
an assembly-level rollup is precisely the kind of enumeration detail the oracle cannot
standardize away. A tolerance there would paper over real drift; excluding it focuses the
gate on the claim that matters: *for every object both tools can see, they read the same*.

## The schema, so the next reader does not guess

`Metrics.exe` writes element names as **symbol kinds**: `<Assembly>`, `<Namespace>`,
`<NamedType>`, with `<Metrics><Metric Name="MaintainabilityIndex" Value="62"/></Metrics>`
children; a type's `Name` attribute carries at most the containing-type qualification
(`TargetParser.MemberSink`), so the full dotted key is ancestor `<Namespace>` + `.` +
`Name`. `MetricsOracleCompare.cs` reconstructs keys exactly that way and indexes both the
qualified and unqualified spellings — never a bare last segment, because a same-named type
in another namespace silently standing in for its twin would defeat the entire point.

## Known first-run risk, stated plainly

This comparison has been exercised against a synthetic report built from the real
`Metrics.exe` schema (agree → green; corrupted and missing types → red), but not against
`Metrics.exe` itself — no Windows run has happened yet. If the vendor tool disagrees on the
first night, the issue it opens *is* the deliverable: per #254's acceptance criteria, the
discrepancy is investigated and documented **before the job is marked green**, and a
tolerance is never chosen to make an existing disagreement pass.

## Provisioning the vendor tool

The `Microsoft.CodeAnalysis.Metrics` 5.6.0 package contains `Metrics.exe` and its Roslyn
assemblies, but it does not contain the `BuildHost-netcore` payload required by its embedded
`Microsoft.CodeAnalysis.Workspaces.MSBuild` assembly. That assembly resolves the host relative to
its application directory, so merely installing the Metrics package leaves the executable
unusable with the recurring error:

```
The build host could not be found at '.../Metrics/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll'
```

The workflow therefore installs `Microsoft.CodeAnalysis.Workspaces.MSBuild` **4.12.0** — the
version matching the Workspaces assembly embedded in Metrics 5.6.0 — and copies its complete
`contentFiles/any/any/BuildHost-netcore` directory next to `Metrics.exe`. The host directory is
copied in full because its runtime configuration, dependency manifest and companion assemblies
are part of the launch contract. The workflow checks those files before invoking the vendor tool,
so a future package change fails at provisioning with an actionable error.

Failures before the comparison — including `CodeMetrics.cs` itself failing, which is why it runs
first — are reported with the separate `metrics-oracle-infrastructure` label. Only a successful
Metrics.exe run followed by a failed comparison is reported as a genuine `metrics-oracle`
disagreement.

Refs: #251 (epic), #253 (layer 1), #254 (this oracle), #21 (the tooling split this page
schedules around).
