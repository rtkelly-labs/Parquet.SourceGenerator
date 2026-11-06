# 26 — Mutation Testing the Behavioural Suite (Layer 4 of #251)

Coverage says a line executed. **Mutation testing says a test would have noticed if the
line were wrong.** This repository gates 85% line / 70% branch coverage and had no measure
of the second thing — and had already done it by hand twice, both times with payoff:

- **#209** flipped `clearArray: true → false` in its own pool-hygiene test and watched it
  fail with "3 of 10 consumed rows are still reachable". That proved the test could detect
  the bug it claimed to guard.
- **#213** verified a double-return detector by deliberately dropping `slot = null`.

Both were one-off, manual, unrepeatable. This page is about the repeatable version — and
about the trap that makes a naive version worthless.

## The trap: golden files make mutation scores meaningless

The golden-file tests compare the **full emitted text**, character for character. So *any*
mutation of the emitter — every operator flip, boundary change and deleted branch — changes
the emitted string and is trivially "killed". A Stryker run that includes them reports a
score approaching 100% while proving nothing about semantic coverage: the golden files
detect **change**, not **wrongness**. Worse than useless, an inflated number would claim
the behavioural suite is stronger than it is.

That is the entire substance of this layer: the exclusion is the design, not a detail.

## What runs, and where it is configured

`stryker-config.json` (repo root) + the nightly `mutation.yml`:

- **Excluded from the mutation run** (`test-case-filter`, VSTest syntax):
  `GoldenCodeGenRegressionTests` (emitted-text match), `GeneratedApiBaselineTests`
  (`.api.txt` signature baselines), `GeneratedCodeMetricsBaselineTests` (`.metrics.txt`),
  and `IlInterrogationTests` (IL-shape assertions — same change-not-wrongness property as
  the text suites). **If you are about to remove one of these filters to raise the score,
  stop: you are inverting the measurement.** The comment in the config file says so where
  the damage would be done.
- **Included**: everything that asserts behaviour — round-trips against Parquet.Net and
  the external oracles, property-based cases (#198), diagnostics (`PARQ*`), and the
  compile-checks (a mutation producing non-compiling emitted code *is* wrongness, per the
  #255 lesson, and those tests stay).
- **Projects mutated**: `Parquet.SourceGenerator` (parser + emitters — where #253 found
  the complexity and where surviving mutants matter most) and `Parquet.SourceGenerator.Attributes`
  (the runtime helpers that ship to consumers).

## Reporting: PR, never a red build

The established CI lesson (#189, and adopted by #254): a scheduled job that merely fails
gets muted; a scheduled job that opens something reviewable gets acted on. Each night the
workflow force-updates the `mutation/headline` branch and refreshes **one PR** —
`chore(mutation): nightly mutation testing baseline` — whose body is the recomputed score
per project with the surviving-mutant inventory grouped by file. The first night's number
**is** the baseline (#254's exact pattern for the oracle). Merging that PR is how the
baseline gets recorded into `docs/MUTATION-HEADLINE.md`; the alternative is closing it.

## What is deliberately NOT gated yet

No threshold, no floor, no break-at. The issue's rule: *"Gate on a floor only once the
number is known — choosing a threshold before measuring is how a gate ends up either
vacuous or permanently red."* `stryker-config.json` has no `thresholds` object for exactly
this reason; adding one is a change that must cite a measured score in review. The score
also never fails the job today — surviving mutants open a conversation through the PR, and
a red schedule would just be noise with a mutation percentage in it.

## Reading a score honestly

`scripts/MutationSummary.cs` recomputes the percentage rather than trusting Stryker's
headline: `tested = total − compile-error − ignored − not-run`, and the denominator
components are printed beside the score. A mutation score with an invisible denominator is
a marketing number; here every part of it is on the page.

Triage priority when survivors appear (issue AC 5): the parser first — `TargetParser` is
the generator's front door and the one every #251 layer has pointed at — then the emitters,
then the Attributes runtime helpers, because those two ships in the consumer's package and
its data correctness rests on it.

## Practical notes

- Nightly on `ubuntu-latest`; the generator sweep is capped at 100 minutes and the
  Attributes sweep at 45, inside a 150-minute job. It is not per-PR: CI is already ~4.5
  minutes of build+test and mutation testing is orders of magnitude slower.
- `dotnet-stryker` is a local tool pinned in `.config/dotnet-tools.json` like every other
  CLI here; restore with the mandated `dotnet tool restore --disable-parallel`.
- Raw `mutation-report.json` files upload as artifacts every run; the PR body is the
  summary, the artifact is the evidence.

Refs: #251 (epic), #261 (this layer), #260 (layer 3), #254 (the oracle, same reporting
shape), #209/#213 (the manual precedents), #189 (the PR-not-red-build lesson).
