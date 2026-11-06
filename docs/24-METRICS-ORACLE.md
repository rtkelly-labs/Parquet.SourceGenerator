# 24 — The Metrics Oracle (#254): Metrics.exe against CodeMetrics.cs

Layer 1's baselines (`metrics/*.metrics.txt`, [21 — Code Metrics](./21-CODE-METRICS.md)) are
computed by `scripts/CodeMetrics.cs` — a bespoke, cross-platform program. It exists because
`Microsoft.CodeAnalysis.Metrics` ships `Metrics.exe`, a .NET Framework **Windows-only**
executable, which cannot run on this repository's `ubuntu-latest` CI.

"Cannot run on the current runner" is not "cannot be in CI", though. A nightly
`windows-latest` job can run the vendor tool and ask it the one question the bespoke
computation can never answer about itself:

> **Is our ruler accurate?**

If `CodeMetrics.cs` enumerates members differently from Microsoft's tool — a treatment of
accessors, partials or nested types that quietly diverges — then every number in every
baseline is wrong **in the same direction**, and the drift gate holds that wrong baseline
stable forever with perfect confidence. Drift detection has no opinion about correctness;
only an independent oracle does. This is the same reasoning that put PyArrow (#165), DuckDB
(#166) and pinned `parquet-cli` (#183) around the *generated files*; the metrics were the
last self-reported number in the quality stack without a cross-check.

## The mechanism

`.github/workflows/metrics-oracle.yml`, nightly (`30 5 * * *`) plus manual dispatch:

1. `nuget install Microsoft.CodeAnalysis.Metrics` (version pinned in the workflow env) and
   run `Metrics.exe` over both generator projects in one invocation, emitting one XML report.
2. `scripts/MetricsOracleCompare.cs` reads the XML and the matching `metrics/*.metrics.txt`
   baseline and compares them **per type**: MI within ±2 (the layer-1 cross-machine policy —
   the index bottoms out in a cube root), CC / CL / SLOC exact.
3. Any disagreement fails the job **and** opens or updates a `metrics-oracle`-labelled
   GitHub issue with the run link. A silently-red schedule gets muted; an issue gets acted
   on — the reporting lesson of #189, which #261 also adopts.

## What is gated, and what is only reported

**Gated: type-level agreement.** The baseline lists every hand-written type in each
assembly; the oracle must report the same metrics for each. A baseline type the oracle does
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

Refs: #251 (epic), #253 (layer 1), #254 (this oracle), #21 (the tooling split this page
schedules around).
