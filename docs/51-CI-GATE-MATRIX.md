# 51 - CI Gate Matrix

> Audit of every gate this repository relies on or promises: where it runs, whether branch
> protection reaches it, how it can pass without checking anything, and what is known to be open.
> Written against `main` at `e6d16d9` (2026-10-01; #465 and #466 had merged by then and the rows reflect them). Every claim was checked against the workflow
> or script it cites, not against another document. Companion to
> [18](./18-API-CHANGE-CONTRACT.md) (API gates), [17](./17-GENERATED-API-BASELINES.md) (derived
> outputs), [50](./50-GENERATED-CODE-ANALYSIS.md) (emitted-code analyzers) and
> [05](./05-TESTING-STRATEGY-AND-BENCHMARKS.md) (test strategy). The release criteria it audits
> against are [47 section 8](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md#8-release-gate).

A green `build` check means "every gate below marked **via `build`** passed on this commit", and
nothing more. This page exists so that sentence can be read without opening ten workflows.

## 1. What branch protection requires

`main` is governed by one ruleset, *Main Branch Governance Ruleset* (id 20385022, enforcement
active, no bypass actors), and by a classic branch-protection record that agrees with it. Read
with `gh api repos/rtkelly-labs/Parquet.SourceGenerator/rules/branches/main` and
`.../branches/main/protection`:

| Rule | Value |
|:--|:--|
| Required status checks | `build`, `pr-title` (both from GitHub Actions, app id 15368) |
| Strict (branch must be up to date) | yes |
| Pull request required | yes, 0 approving reviews, stale reviews dismissed on push |
| Conversation resolution | required (`required_review_thread_resolution: true`) |
| Linear history, no deletion, no non-fast-forward | yes |
| Merge methods | ruleset lists merge, squash and rebase; the repository settings allow squash only (`allow_merge_commit` and `allow_rebase_merge` are false), so squash is what is reachable |

Nothing else is required. `test`, `derived`, `generated-analysis`, `devskim`, `semgrep-oss`,
CodeRabbit, DeepSource and Socket all report on pull requests and none of them can block a merge
except through `build` (the first three) or an unresolved review thread (CodeRabbit).

### Required check names are a contract

Branch protection matches by name string. `ci.yml:679` (`name: build`) and `pr-title.yml:18`
(`name: pr-title`) are the strings the ruleset holds. `CiGateIntegrityTests` now fails if either
job is renamed, so a rename shows up in the pull request that makes it rather than as a
permanently pending required check afterwards.

### The aggregate-gate trap: `build` does not have it

`build` (`ci.yml:678-694`) is the only way `test`, `derived` and `generated-analysis` reach branch
protection. Read line by line:

- `if: always()` (`ci.yml:680`): the job runs even when a dependency failed, was cancelled or was
  `build`, and GitHub counts a job skipped by its own condition as passing, not failed, so a failing
  dependency would let the required check through. "Expected" means the check never reported at all.
  `build`, and a skipped required check reads as "expected", not "failed".
- `needs: [test, derived, generated-analysis]` (`ci.yml:681`).
- Each result is read from `needs.<job>.result` (`ci.yml:686-688`), the job's own conclusion, not
  from a step output. A job that dies before its last step still reports `failure`, so the
  "empty output is forgiven" shape from
  the estate's `landing-a-queue.md` (shared-utilities, docs/git) cannot occur.
- The decision (`ci.yml:691`) is `!= "success"` for each of the three, joined with `||`. Only an
  exact `success` passes. `failure`, `cancelled`, `skipped` and an empty value all fail it.
- None of the three jobs has a job-level `if:` or `continue-on-error`, so none can be `skipped`.

What the aggregate cannot see: a job added to `ci.yml` and left out of `needs` runs, reports, and
blocks nothing. `CiGateIntegrityTests.TheBuildAggregateRequiresEveryOtherJobInCiToSucceed`
(added with this page) compares `needs` with every job key in the file and requires a
`needs.<job>.result` comparison for each.

What is deliberately outside the aggregate, by `continue-on-error` inside a job that the aggregate
does count: the coverage sticky comment (`ci.yml:229-231`), the merge-base lookup for the derived
diff (`ci.yml:503-506`) and the derived sticky comment (`ci.yml:584-586`). Those are presentation;
the gate steps run before them.

A concurrency cancel is safe: a cancelled `test` makes `build` fail rather than pass. A cancelled
`build` itself is not a success, so it does not satisfy the requirement either.

## 2. The matrix

Column key. **Req**: *direct* means a required check of its own; *via build* means the job is in
the `build` aggregate (section 1); *no* means nothing requires it. **Vacuity** says how the gate
can pass while examining nothing, and whether a positive control (a seeded failure it must catch)
or an "examined N items" assertion exists. Triggers: **PR** is `pull_request` (any base branch,
`ci.yml:10`), **main** is push to `main`.

| Gate | Where it runs | Req | Can it pass vacuously? | Trigger | Known gaps |
|:--|:--|:--|:--|:--|:--|
| `pr-title` conventional title | `pr-title.yml:17-37`, `amannn/action-semantic-pull-request` | direct | No: the action validates the title it is given. | PR (opened, edited, synchronize, reopened) | A PR opened by `GITHUB_TOKEN` (the benchmark headline and mutation headline PRs) starts no workflows, so it never reports and cannot merge: [#474](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/474) has no checks since 2026-09-20. |
| Build, `-warnaserror` (style, CA/MA/RCS analyzers, CA1502/CA1505/CA1506, RS0016, PARQAPI002) | `ci.yml:133-134`; also `release.yml:121-124` | via build | The flag lives only on these two command lines. `Directory.Build.props` sets no `TreatWarningsAsErrors`, so a local build and the `dotnet test` in `generated-analysis` only warn. No positive control proves each analyzer is loaded. | PR, main | A mistyped severity in `.editorconfig` disables a rule silently. Only `CodeMetricsConfig.txt` is validated (below). |
| Unit, property, behavioural tests | `ci.yml:209-216`, filter excludes `DatasetIntegrity` and `ExternalInterop` | via build | No test-count floor. `ExternalInterop` tests `return` (pass) when their input variable is unset (`PyArrowInteropTests.cs:70-75,95-99`); `ci.yml:218-222` sets both, but nothing fails if that step loses its `env:` block. | PR, main | `release.yml:131-132` runs the unfiltered suite with neither variable set, so those two tests pass without examining a file there. |
| Dataset integrity (committed fixture hashes) | `.github/actions/verify-test-data/action.yml`, called at `ci.yml:144-145` | via build | Hash manifest compared to committed bytes; fails on mismatch. No count assertion. | PR, main, release | None known. |
| Golden regression | `GoldenCodeGenRegressionTests.cs:25-42` (`VerifyAndPublish`) | via build | There is no checked-in `.g.cs` to go missing: output is published and diffed against the base (docs 17). Per model it asserts no generator error, syntax-clean source, and a non-empty public API (`CountMembers > 0`, line 38). | PR, main | [#407](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/407) is closed as obsolete (commit `9ca9d14`, #514). [#468](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/468) targeted the removed self-heal branch and was closed as superseded. The diff itself is a review aid, not a gate (see derived-output review). |
| Semantic compile of every golden model ([#413](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/413)) | `scripts/CodeMetrics.cs:660-700` compiles each emitted file with its `GoldenModels/` declaration; errors fail the run (`:220-227`); called from `DerivedOutputs.cs` in the `derived` job (`ci.yml:477-480`) | via build | Zero published models throws (`CodeMetrics.cs:169-176`); a model with no declaration throws (`:188-196`). It iterates over published files, not declared models, so a model dropped from `GoldenCorpus` would be skipped here. `generated-analysis` does compare against the declared set (`GeneratedCodeAnalysis.cs:101-110,164-170`), which closes that. | PR, main | #413 describes tests that parsed three hand-built goldens without binding them. Main now binds all seven (hand-built and driven, both backends). The issue appears satisfiable; close it after confirming. |
| Emitted-code analyzer gate ([#553](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/553), docs 50) | job `generated-analysis`, `ci.yml:626-` | via build | Positive control: `--self-test` seeds a CA2007 violation in each backend and fails unless it is caught (`ci.yml:648-650`, `GeneratedCodeAnalysis.cs:68`). Examined-N: fails when the emitted file count differs from the declared golden models or either backend compiled none (`:101-110,138-142,164-170`). | PR, main | Baseline is empty by design. Rules outside the `src/` `.editorconfig` section are not applied. |
| Coverage gate ([#416](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/416), closed) | `ci.yml:224-227` runs `scripts/CoverageSummary.cs --min-line 85.0 --min-branch 70.0` | via build | Non-vacuous since #466 (`e6d16d9`): fewer than `--min-packages` shipping packages (default 1), zero lines, or zero branches each exit 1 (`CoverageSummary.cs:234-256`); a missing file or unparseable XML also fails. Positive controls: `CoverageSummaryGateTests`. Residual: unparseable `--min-line`/`--min-branch` values still silently keep the defaults ([#458](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/458)), and `ci.yml` does not pass `--min-packages`, so the floor is one package, not the expected two. | PR, main | `coverlet.runsettings` said coverage was "not yet gated"; corrected in this change. |
| Zero-boxing IL gate ([#422](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/422)) | `ci.yml:206-207` runs `scripts/InterrogateIL.cs --check` over the CLI assembly | via build | No, since [#467](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/467). A failed or empty disassembly is fatal, and a type whose IL yields no instructions fails the run ("refusing to report zero boxing"), so the gate prints `N IL instruction(s) examined across M type(s)`. IL is counted per type, including nested and async state-machine types. Fixtures for the parser are in `IlInterrogationTests`. | PR, main | Landed in #467. |
| Native AOT | `ci.yml:261-290`; `release.yml:137-156` | via build | The native binary is executed and throws on a round-trip mismatch, so a zero exit is a real result. IL trim warnings are filtered by "not attributed to `/Parquet.dll `"; a log with no warnings passes, which is the correct empty case. | PR, main, release | `linux-x64` only. Every warning from Parquet.Net is tolerated wholesale. |
| .NET Framework 4.7.2 legacy consumer | `ci.yml:400-409` (and `release.yml:223-231`): `dotnet build --framework net472` | via build | **It is compiled, never executed.** An Ubuntu runner cannot run net472 (no Windows, no Mono step). A runtime failure on .NET Framework, for example a missing method or a binding redirect, passes CI. | PR, main, release | See recommendation R5. Related: [#493](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/493). |
| Package layout and consumption | `ci.yml:296-358`: nupkg layout, then consume v6 and Legacy packages on net8.0 and net9.0 from a local feed | via build | Layout checks name exact entries (`unzip -Z1 \| grep -q`), so absence fails. No negative allowlist: new consumer-executed content in a package is not noticed ([#400](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/400)). The consumers are real programs that must exit 0. | PR, main | Layout is checked again at release (`release.yml:163-192`). |
| Cross-version interoperability (Parquet.Net 6.x and 4.25) | `ci.yml:368-392` | via build | Each hand-off must read back or the program exits non-zero. The matrix file is printed (`:392`), not counted, so a program that wrote no rows would still pass. | PR, main | Row count not asserted. |
| External engines: PyArrow, DuckDB, Apache parquet-cli | `ci.yml:147-155` PyArrow, `:157-176` DuckDB, `:183-197` Apache | via build | Apache conformance prints "N checks passed" without asserting N is positive ([#436](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/436)). `generate_test_data.py` silently regenerates on an argc mismatch ([#445](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/445)). | PR, main | The DuckDB CLI zip is downloaded without a checksum (`ci.yml:157-169`); the Apache tools are hash-verified. |
| API change contract: RS0016 and PARQAPI002 | analyzers under `-warnaserror`, `ci.yml:133-134` | via build | RS0016 is a warning by default and no `.editorconfig` entry raises it, so it blocks only because of `-warnaserror`. No test proves the PARQAPI002 analyzer reports; `ApiChangeContractTests` covers the ledger exemption parser only. | PR, main | A positive control for each analyzer would close this. |
| API change ledger (catalogue vs ledger, escape-hatch marker) | `ci.yml:128-131` runs `scripts/CheckApiLedger.cs` | via build | The catalogue-vs-ledger diff needs a base ref and is skipped when `GITHUB_BASE_REF` is empty (`CheckApiLedger.cs:242-246`), which is every push to main ([#430](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/430)). One heading satisfies any number of additions ([#426](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/426)). | PR (full), main (marker only) | Both open. |
| Emitted consumer API change | `derived` job posts the `.api.txt` diff as a sticky comment (`ci.yml:584-614`) | **no** | It is not a gate. A removed or changed public method on the emitted surface fails nothing; it is shown to the reviewer. docs 18 states this as the design. | PR | The 0.1 contract centres on this surface. [#522](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/522) proposes an approval gate (in flight, docs only). |
| Derived-output production and review diff | job `derived`, `ci.yml:450-614` | via build (production and its gates), no (the comment) | Head production failing fails the job. The base lookup and the comment are `continue-on-error` and best-effort. | PR, main (also publishes `derived-baseline-<sha>`) | In flight: #519, #520, #521 rework this job. |
| Code-metrics ratchets (CA1502, CA1505, CA1506) | thresholds in `CodeMetricsConfig.txt`, enforced by the build (`ci.yml:133-134`); file validated by `CodeMetrics.cs` (`ValidateCodeMetricsConfig`, run in `derived`) | via build | An unrecognised entry in the config silently disables the rules; that is why the script refuses to run on a file it cannot parse. No test proves CA1502 actually fires. | PR, main | None known. |
| Call-graph gates: cycles, fan-out ratchet (28 main, 13 per `CallGraph.cs:68,83`), layering | `scripts/CallGraph.cs:152-207` in `derived` | via build | The `src/` graph has no zero-node assertion; the only `Nodes.Count == 0` check (`:465`) belongs to the generated-code diagram. A parser regression that emitted no edges would pass the fan-out ratchet. | PR, main | Cheap to add: fail when a measured project has no nodes. |
| Duplication | `scripts/Duplication.cs` in `derived` | no | Report only; docs 23 states it does not fail the build. | PR, main | By design. |
| Benchmark regression gate ([#404](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/404), [#412](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/412), [#408](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/408)) | `tools/BenchmarkSummaryGenerator/RegressionCheck.cs` exists; `benchmarks.yml` never calls it | no | **Not wired on main.** `benchmarks.yml` runs on schedule, dispatch and a `/benchmark` PR comment (`:3-36,47-53`), publishes a summary and opens a PR; it fails on nothing. It cannot be a required check (`BRANCH_PROTECTION.md`). | Sunday cron, manual, `/benchmark` comment | In flight: [#469](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/469). 47 section 8 lists #404 and #412 as release blockers. |
| Mutation testing | `mutation.yml` nightly | no | Report-only by design ([#440](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/440)). **Broken in practice:** it failed at "Restore local tools" on each of the last four nightly runs (2026-09-28 to 2026-10-01): `The command "dotnet-stryker" ... is not contained in the package`, the dotnet/sdk #53783 mis-attribution `ci.yml:116` works around. The Stryker steps never ran. | nightly | Retry added in this change; not proven live (running it would also push `mutation/headline` and open a PR). Nobody was told it was red. |
| Metrics oracle (Metrics.exe vs `CodeMetrics.cs`) | `metrics-oracle.yml` nightly on `windows-latest` | no | Compares both tools' output, fails on disagreement, and opens an issue (`:120-159`). Green on the last four runs. | nightly | Cannot run per PR (Windows executable). |
| Property-based fuzz, broad seeds | `fuzz.yml` weekly | no | **Could not pass.** `DefaultSeedsAreDeterministicAndStable` asserts the default seed (`SupportedSchemaPropertyTests.cs:198-203`) and the job sets `PARQUET_FUZZ_SEED`, so that one test failed every run (2026-09-14, 09-21, 09-28; 294 of 295 passed). The red run hid whether any real case failed. | weekly, manual | Filter fixed in this change. The per-push run (default seeds) is unaffected. |
| Python ban, LFS and provenance, formatting, coverage envelope map, package vulnerability audit | `ci.yml:41-67`, `:76-77`, `:119-120`, `:139-140`, `:93-100` | via build | The audit greps the text `has the following vulnerable packages`, so a changed message passes. It does not cover the file-based `scripts/*.cs` apps. | PR, main | None filed. |
| CHANGELOG validation | `ci.yml:79-80` (`ParseChangelog.cs`); `release.yml:43-48` with `--release` | via build | Structure only: `[Unreleased]` first, well-formed version heading. Nothing requires a pull request to add an entry. In release mode it fails when nothing is cut. | PR, main, release | No "changelog touched" gate; whether one is wanted is a policy call. |
| Release workflow guards | `release.yml` | no (manual) | Present: a real release from a non-main ref is refused in `prepare` (`:42-51`) and `publish` is skipped unless `github.ref` is main (`:267`, #465); permissions are `contents: read` at workflow level and widened only on `publish` (`:21-22,269-272`, #370 and #373, both closed); changelog is the version authority (`:60-66`); an existing tag is refused (`:67-77`); `publish` needs `prepare` and `build`. Absent: any check that the commit being released passed `build` on main; `dry_run` still defaults to false (`:12-16`), so an unmodified dispatch from main publishes; the `release` environment has no protection rules or branch policy. | manual dispatch | The release build omits CSharpier, the ledger check, the coverage gate, the IL gate, conformance, DuckDB, PyArrow and `generated-analysis`; it depends on the commit having passed CI on main, and nothing verifies that. |
| Action pins: 40-character SHAs (AGENTS.md rule 8) | every `uses:` under `.github/` | via build (test) | Was unenforced: `devskim.yml` and `semgrep.yml` used `@v4` and `@v1`. Now pinned, and `CiGateIntegrityTests.EveryThirdPartyActionIsPinnedToAFortyCharacterCommitSha` fails on any mutable ref (it asserts it examined more than ten references and read `ci.yml` and `release.yml`). | PR, main | The `returntocorp/semgrep` Docker image (`semgrep.yml:22`) is unpinned; `astral-sh/setup-uv` installs the latest uv. |
| Dependabot | `.github/dependabot.yml`: `nuget` and `github-actions`, weekly, directory `/` | no | Not a gate; an updater. | weekly | Unverified whether it covers `scripts/` file-based `#:package` directives and `.config/dotnet-tools.json`. |
| Security scanners | | | | | |
| - CodeQL | none | no | **Not configured.** `gh api .../code-scanning/default-setup` returns `not-configured`; there is no CodeQL workflow. Earlier descriptions of this repository that list CodeQL are wrong. | | See R4. |
| - Semgrep OSS | `semgrep.yml` | no | Advisory: `semgrep scan` is run without `--error`, SARIF is uploaded `if: always()`. Findings never fail the job. | PR, main | |
| - DevSkim | `devskim.yml` | no | Advisory: SARIF upload, no failing step. | PR, main | |
| - Socket, DeepSource | GitHub apps | no | Report checks on the PR; neither is required. DeepSource analyses C#, Python and secrets. | PR | |
| - CodeRabbit | GitHub app | indirect | Review threads block a merge through conversation resolution until resolved. | PR | |

Workflows registered with GitHub but absent from `main` (`qodana.yml`, `regression.yml`) are
listed under Actions as active; they do nothing here.

## 3. Findings, ranked by risk to the 0.1 release

1. **A release does not verify that the commit passed CI.** #465 closed the any-ref hole and the
   workflow-wide write token, but a dispatch from main with `dry_run` left at its default publishes
   without any check that `build` was green for that commit, and the `release` environment has no
   branch policy. The release build re-runs only some of the gates.
2. **The IL gate no longer passes on empty input** (#422, landed in #467), and neither does the
   coverage gate (#416, #466). Both now fail when they examine nothing, as 47 section 8 requires.
3. **The performance gate is not wired** (#404, #412, #408). 47 section 8 requires it. In flight
   (#469). Until then no check examines a benchmark.
4. **The emitted consumer API is reviewed, not gated.** A breaking change to the surface the 0.1
   contract protects can merge on a green `build`. Design choice, but it should be a decision
   recorded as such. #522 proposes the gate.
5. **Scheduled gates fail unseen.** Mutation was red four nights and fuzz three weeks with no
   notification; only the metrics oracle opens an issue. Workflow causes are fixed here; the
   absence of a notifier is filed.
6. **net472 is never executed.** The Legacy package exists for .NET Framework; CI only compiles it.
7. **Ledger and call-graph vacuity.** #430 and #426 (ledger), and the missing zero-node assertion on
   the `src/` call graph.
8. **ExternalInterop tests pass when their input variable is unset**, and the release workflow runs
   them that way.
9. **Supply chain residue.** DuckDB CLI unverified, Semgrep image and `uv` unpinned.
10. **Advisory scanners and no CodeQL.** No release criterion depends on them.

## 4. Fixed with this page

- `devskim.yml` and `semgrep.yml`: three distinct mutable refs (`actions/checkout@v4`,
  `microsoft/DevSkim-Action@v1`, `github/codeql-action/upload-sarif@v4`) pinned to commit SHAs,
  matching the `checkout` pin the other workflows use.
- `CiGateIntegrityTests` (new): pins are enforced, required check names are pinned to their job
  names, and `build` must aggregate every other job in `ci.yml` with an exact-`success` test.
- `mutation.yml`: the tool-restore retry `ci.yml` already carries.
- `fuzz.yml`: excludes the one test that asserts the unoverridden seed from the run that overrides
  it.
- `docs/17-GENERATED-API-BASELINES.md` (said `build` aggregates `test` and `derived` only) and
  `coverlet.runsettings` (said coverage is not gated) corrected.

Not touched, because in-flight pull requests rewrite them: `ci.yml` (#519, #520), `release.yml`
(#520), `.github/BRANCH_PROTECTION.md` (#521; its `build` bullet still lists only
`test` and `derived`). The `ci.yml:27` comment ("aggregates it with `derived`") is stale for the same reason.

## 5. Recommendations

Branch protection (read-only audit; nothing was changed):

- **R1.** Keep `build` and `pr-title` as the only required checks. Do not add `test`, `derived` or
  `generated-analysis` separately: the aggregate is sound, a second requirement doubles the rename
  hazard, and `CiGateIntegrityTests` now keeps the aggregate honest.
- **R2.** Give the `release` environment a deployment branch policy limited to `main`. Required
  reviewers are not available solo, but a branch policy is, and it backs up #465's guard.
- **R3.** Align the ruleset's `allowed_merge_methods` with the repository (squash only), and keep a
  single source of truth: the classic record still shows `allow_force_pushes: true` and
  `enforce_admins: false`. The ruleset blocks non-fast-forward with no bypass actors, so it wins
  today, but the two records can drift. Both are owned by `shared-utilities`.
- **R4.** Decide whether CodeQL is wanted. If so, enable default setup for `csharp` and `actions`;
  if not, leave the scanner list as it is and stop describing CodeQL as present.

Workflow and test follow-ups (R5 to R8 are filed; R9 and R10 are not). Also filed: [#568](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/568), pull requests opened by `GITHUB_TOKEN` never receive the required checks.

- **R5.** ([#565](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/565)) Add a `windows-latest` job that executes `PackageConsumptionLegacy` on net472, from the
  packages the `test` job built. Add it to the `build` aggregate once it is stable.
- **R6.** ([#564](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/564)) Open an issue when a scheduled workflow fails, as `metrics-oracle.yml` does, for
  `mutation.yml` and `fuzz.yml`.
- **R7.** ([#566](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/566)) Make an `ExternalInterop` test fail, not return, when `CI` is set and its variable is
  empty; filter the category out of `release.yml:131-132` or supply the variables there.
- **R8.** ([#567](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/567)) Verify the DuckDB CLI zip against a pinned SHA-256, pin the Semgrep image by digest, and
  pin the uv version.
- **R9.** Add positive controls where only absence of evidence exists today: a seeded RS0016 and
  PARQAPI002 violation, a seeded CA1502 violation, a nonzero node count on the `src/` call graph, a
  row count on the cross-version matrix, and a test-count floor on the main suite.
- **R10.** After #469 lands, re-read the rows for the benchmark gate and
  golden, and replace "in flight" with the merged behaviour.

## 6. In flight at the time of the audit

Merged since the first pass of this audit: [#465](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/465) (release permissions, main-only publish) and [#466](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/466) (coverage gate on empty report); their rows above describe the merged behaviour.

| Pull request | Rows it changes |
|:--|:--|
| [#469](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/469) benchmark regression wired in | Benchmark regression gate |
| [#519](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/519), [#520](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/520), [#521](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/521) derived report, derived history, protected paths | Derived-output rows, `ci.yml` line numbers |
| [#522](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/522) derived-output approval gate (docs) | Emitted consumer API change |

## 7. What this audit could not establish

- Whether the mutation retry fixes the nightly. The evidence is the error text and the fact that
  `ci.yml` carries the same retry for the same message; a live run was not made because the job
  force-pushes `mutation/headline` and opens a PR.
- Whether Dependabot reads file-based `#:package` directives and the tool manifest.
- Whether `dotnet test` exits non-zero when a filter matches no tests; no floor exists, so a
  discovery regression would depend on that behaviour.
- Socket and DeepSource thresholds; both are configured outside this repository.
