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
  skipped, so the required check always reports. Without it a failed `needs` job would skip
  `build`, and GitHub counts a job skipped by its own condition as passing, not failed, so a failing
  dependency would let the required check through. "Expected" means the check never reported at all.
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
diff (`ci.yml:526`), the two HTML report renders and uploads (`Render Reports`, `Upload Diff (HTML)`, `Upload State (HTML)`, `Render Review Comment`) and the derived sticky comment (`ci.yml:~650`). Those are presentation;
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
| `pr-title` conventional title | `pr-title.yml:17-37`, `amannn/action-semantic-pull-request` | direct | No: the action validates the title it is given. | PR (opened, edited, synchronize, reopened) | A PR opened by `GITHUB_TOKEN` starts no workflows, so it never reports and cannot merge: [#474](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/474) has no checks since 2026-09-20. The two workflows that open PRs (`benchmarks.yml` headline table, `mutation.yml` headline) now use `WORKFLOW_PR_TOKEN` and open nothing, with a notice, while it is absent (#568, setup in R11); #474 itself still needs a manual close or a push by a person. |
| Build, `-warnaserror` (style, CA/MA/RCS analyzers, CA1502/CA1505/CA1506, RS0016, PARQAPI002) | `ci.yml:133-134`; also `release.yml:121-124` | via build | The flag lives only on these two command lines. `Directory.Build.props` sets no `TreatWarningsAsErrors`, so a local build and the `dotnet test` in `generated-analysis` only warn. A fixture project that is wrong on purpose (`test/Parquet.SourceGenerator.GateFixture`, not in the solution) is built under `-warnaserror` by `AnalyzerGateTests` and must fail with RS0016, PARQAPI002 and CA1502 ([#615](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/615)): removing the PublicApiAnalyzers reference from it makes the test fail (checked). The same tests pin `-warnaserror` on the ci.yml and release.yml solution builds and the analyzer wiring of the shipping projects (PublicApiAnalyzers on Attributes, ApiGates as an analyzer with seams.txt on both generators, CA1502, CA1505 and CA1506 at warning). The fixture mirrors the wiring; it cannot see an analyzer dropped from a shipping csproj by some other spelling. | PR, main | A mistyped severity in `.editorconfig` disables a rule silently. Only `CodeMetricsConfig.txt` is validated (below). The generator-author rules (RS1035 banned APIs, RS1036, RS1038, RS1041) run on both shipping generators since [#471](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/471) (`EnforceExtendedAnalyzerRules` plus `Microsoft.CodeAnalysis.Analyzers` 5.9.0, no override; `CiGateIntegrityTests` pins both); RS2000-series release tracking is enforced on both generators since [#593](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/593) (`AnalyzerReleases.Shipped.md` and `.Unshipped.md`; a descriptor missing from them is build error RS2000, and `DiagnosticDocsAndReleaseTrackingTests` also requires a docs/13 section per descriptor). RS1032 is suppressed by name until the ten message formats get their trailing period. |
| Unit, property, behavioural tests | `ci.yml:209-216`, filter excludes `DatasetIntegrity` and `ExternalInterop` | via build | No test-count floor. `ExternalInterop` tests fail, not return, when their input variable is unset and `CI` or `PARQUET_REQUIRE_EXTERNAL_INTEROP` is set (`ExternalInteropInput.cs`, #566), so a step that loses its `env:` block goes red; a plain local run still returns. | PR, main | `release.yml` excludes `Category=ExternalInterop` from its test step (it produces no fixtures, and under `CI=true` the tests would fail). |
| Dataset integrity (committed fixture hashes) | `.github/actions/verify-test-data/action.yml`, called at `ci.yml:144-145` | via build | Hash manifest compared to committed bytes; fails on mismatch. No count assertion. | PR, main, release | None known. |
| Golden regression | `GoldenCodeGenRegressionTests.cs:25-42` (`VerifyAndPublish`) | via build | There is no checked-in `.g.cs` to go missing: output is published and diffed against the base (docs 17). Per model it asserts no generator error, syntax-clean source, and a non-empty public API (`CountMembers > 0`, line 38). | PR, main | [#407](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/407) is closed as obsolete (commit `9ca9d14`, #514). [#468](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/468) targeted the removed self-heal branch and was closed as superseded. The diff itself is a review aid, not a gate (see derived-output review). |
| Semantic compile of every golden model ([#413](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/413)) | `scripts/CodeMetrics.cs:660-700` compiles each emitted file with its `GoldenModels/` declaration; errors fail the run (`:220-227`); called from `DerivedOutputs.cs` in the `derived` job (`ci.yml:477-480`) | via build | Zero published models throws (`CodeMetrics.cs:169-176`); a model with no declaration throws (`:188-196`). It iterates over published files, not declared models, so a model dropped from `GoldenCorpus` would be skipped here. `generated-analysis` does compare against the declared set (`GeneratedCodeAnalysis.cs:101-110,164-170`), which closes that. | PR, main | #413 describes tests that parsed three hand-built goldens without binding them. Main now binds all seven (hand-built and driven, both backends). The issue appears satisfiable; close it after confirming. |
| Emitted-code analyzer gate (NetAnalyzers, Meziantou, Roslynator, IDE rules; no Sonar since docs 55) ([#553](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/553), docs 50) | job `generated-analysis`, `ci.yml:626-` | via build | Positive control: `--self-test` seeds a CA2007 violation in each backend and fails unless it is caught (`ci.yml:648-650`, `GeneratedCodeAnalysis.cs:68`). Examined-N: fails when the emitted file count differs from the declared golden models or either backend compiled none (`:101-110,138-142,164-170`). | PR, main | Baseline is empty by design. Rules outside the `src/` `.editorconfig` section are not applied. |
| Coverage gate ([#416](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/416), closed) | `ci.yml:224-227` runs `scripts/CoverageSummary.cs --min-line 85.0 --min-branch 70.0` | via build | Non-vacuous since #466 (`e6d16d9`): fewer than `--min-packages` shipping packages (default 1), zero lines, or zero branches each exit 1 (`CoverageSummary.cs:234-256`); a missing file or unparseable XML also fails. Positive controls: `CoverageSummaryGateTests`. Residual: unparseable `--min-line`/`--min-branch` values still silently keep the defaults ([#458](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/458)), and `ci.yml` does not pass `--min-packages`, so the floor is one package, not the expected two. | PR, main | `coverlet.runsettings` said coverage was "not yet gated"; corrected in this change. |
| Zero-boxing IL gate ([#422](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/422)) | `ci.yml:206-207` runs `scripts/InterrogateIL.cs --check` over the CLI assembly | via build | No, since [#467](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/467). A failed or empty disassembly is fatal, and a type whose IL yields no instructions fails the run ("refusing to report zero boxing"), so the gate prints `N IL instruction(s) examined across M type(s)`. IL is counted per type, including nested and async state-machine types. Fixtures for the parser are in `IlInterrogationTests`. | PR, main | Landed in #467. |
| Native AOT | `ci.yml:261-290`; `release.yml:137-156` | via build | The native binary is executed and throws on a round-trip mismatch, so a zero exit is a real result. IL trim warnings are filtered by "not attributed to `/Parquet.dll `"; a log with no warnings passes, which is the correct empty case. | PR, main, release | `linux-x64` only. Every warning from Parquet.Net is tolerated wholesale. |
| .NET Framework 4.7.2 legacy consumer | `ci.yml` (and `release.yml`): `dotnet build --framework net472`, compile only; `net472-consumer.yml` (job `net472-consumer`, `windows-latest`) packs Attributes and Legacy and runs the consumer on net472 | compile: via build; execution: **no** (own workflow, not in `build`) | Executed since [#565](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/565): the full consumer (write, read, compression, batching, schema evolution, page-size limit), a blocked-`SynchronizationContext` round trip with a positive control, and a file hand-off net472 to net8.0 and back. `CiGateIntegrityTests` fails if the workflow stops `dotnet run`-ing net472 or the scenario is dropped. Limit: the blocked-context round trip uses a synchronously completing stream, because Parquet.Net 4.25 itself (`ParquetReader.CreateAsync`, column reads and writes) deadlocks a blocked caller over a genuinely asynchronous stream whatever the emitted code does; the emitted code puts `ConfigureAwait(false)` on every await, which no check here can strengthen. | PR and push to main when `src/**`, the consumer, the shared models or build props change; manual | Deliberately not required and not in `build`: a new runner type, and a flaky required job would block every merge. Promote it by moving the job into `ci.yml` and `build`'s `needs` after a clean record. The release build still only compiles net472. Related: [#493](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/493). |
| Package layout and consumption | `ci.yml:308-351`: pack, nupkg layout, then consume v6 and Legacy packages on net8.0 and net9.0 from a local feed | via build | `scripts/VerifyPackageLayout.cs` (composite action `.github/actions/verify-package-layout`, shared by CI and release) compares the exact entry list of every nupkg with the committed manifest `.github/package-layout/<id>.txt`: an unlisted entry, a missing one, a package without a manifest, a manifest without a package, an empty directory or a missing declared nuspec dependency each fail ([#400](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/400); positive controls in `PackageLayoutGateTests`, delegation pinned in `WorkflowConsistencyTests`). The consumers are real programs that must exit 0. | PR, main | Layout is checked again at release (`release.yml:165-212`). |
| Cross-version interoperability (Parquet.Net 6.x and 4.25) | `ci.yml:368-392` | via build | Each hand-off must read back or the program exits non-zero. The matrix file is printed (`:392`), not counted, so a program that wrote no rows would still pass. | PR, main | Row count not asserted. |
| External engines: PyArrow, DuckDB, Apache parquet-cli | `ci.yml:147-155` PyArrow, `:157-176` DuckDB, `:183-197` Apache | via build | Apache conformance refuses to report OK unless every fixture in `test/data/fixture-manifest.json` exists and was examined: a missing fixture or an empty manifest exits 1 before any Java process starts (#436, `ApacheConformanceGateTests`), and the verdict prints `N/M fixtures`. `generate_test_data.py` silently regenerates on an argc mismatch ([#445](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/445)). | PR, main | The DuckDB CLI zip is verified against a pinned SHA-256 (`DUCKDB_SHA256`, #567; the value equals the digest GitHub publishes for the release asset); the Apache tools are hash-verified. |
| API change contract: RS0016 and PARQAPI002 | analyzers under `-warnaserror`, `ci.yml:133-134` | via build | RS0016 is a warning by default and no `.editorconfig` entry raises it, so it blocks only because of `-warnaserror`. No test proves the PARQAPI002 analyzer reports; `ApiChangeContractTests` covers the ledger exemption parser only. | PR, main | A positive control for each analyzer would close this. |
| API change ledger (catalogue vs ledger, escape-hatch marker) | `ci.yml:128-131` runs `scripts/CheckApiLedger.cs` | via build | The catalogue-vs-ledger diff needs a range. A pull request supplies one (`GITHUB_BASE_REF`); a push to main uses `GITHUB_EVENT_BEFORE..HEAD` (the first parent only when no previous tip was supplied; a named previous tip that cannot be fetched exits 1), and a run with no range at all exits 1 instead of skipping ([#430](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/430), `CheckApiLedgerGateTests`). Each added ledger entry must carry a non-empty **Surface:**, a **Semver:** starting with one of the four buckets, an **Issue:** and a **Rationale:**, and the printed template's placeholders are rejected ([#426](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/426)). The entry count is deliberately not compared with the number of catalogue lines: one entry legitimately covers many (#481 records 16). Whether an entry's prose is true is still review. | PR, main (both rules) | None known. |
| Emitted consumer API change | `derived` job posts a PR summary as a sticky comment (`Update Sticky Review Comment`): per golden model the public member count before and after, signatures added and removed, and the public types that came and went; the line-level `.api.txt` diff is the `derived-diff.html` artifact the comment links to (`scripts/DerivedReport/`, #519) | **no** | It is not a gate. A removed or changed public method on the emitted surface fails nothing; it is shown to the reviewer. docs 18 states this as the design. | PR | The 0.1 contract centres on this surface. [#522](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/522) proposes an approval gate (in flight, docs only). |
| Derived-output production and review diff | job `derived`, `ci.yml:457-682` | via build (production and its gates), no (the comment) | Head production failing fails the job. The base lookup, the report renders and uploads (a Razor file-based app, `scripts/DerivedReport/`, with no tests of its own) and the comment are `continue-on-error` and best-effort, so a broken renderer costs the review aid, not the merge. | PR, main (also publishes `derived-baseline-<sha>`) | #519 landed the report; #520 (derived history) and #521 (protected paths) are in flight. The report is deterministic for the same two commits and trees; nothing asserts that. |
| Code-metrics ratchets (CA1502, CA1505, CA1506) | thresholds in `CodeMetricsConfig.txt`, enforced by the build (`ci.yml:133-134`); file validated by `CodeMetrics.cs` (`ValidateCodeMetricsConfig`, run in `derived`) | via build | An unrecognised entry in the config silently disables the rules; that is why the script refuses to run on a file it cannot parse. No test proves CA1502 actually fires. | PR, main | None known. |
| Call-graph gates: cycles, fan-out ratchet (28 main, 13 per `CallGraph.cs:68,83`), layering | `scripts/CallGraph.cs:152-207` in `derived` | via build | The `src/` graph has no zero-node assertion; the only `Nodes.Count == 0` check (`:465`) belongs to the generated-code diagram. A parser regression that emitted no edges would pass the fan-out ratchet. | PR, main | Cheap to add: fail when a measured project has no nodes. |
| Fork pull requests may not change CI, build or tooling | `protected-paths.yml` (job `protected-paths`, `pull_request_target`), list in `.github/protected-paths.txt` | **no (not yet in the ruleset)** | Fails closed: an unreadable files listing, a listing whose size differs from the pull request's `changed_files`, more than 3,000 changed files (the files API cap) and an empty path list each fail the job. Same-repository branches pass by design. A rename counts on both sides. `CiGateIntegrityTests.TheProtectedPathsCheckRunsTheBaseBranchAndNeverChecksOutThePullRequest` keeps the base-branch trigger, forbids any checkout of the PR head and pins the job name. | PR from any head (opened, synchronize, reopened, edited) | Advisory until `protected-paths` is added to `REPO_REQUIRED_CHECKS_MAP` in shared-utilities and the ruleset (a PR from a fork can still merge on a red check today). Entries are exact paths or folder prefixes, so a nested `test/Directory.Build.props` or any `*.csproj` is not covered, and test code itself runs in CI whatever this list says: it narrows what a fork can change unreviewed, it is not a sandbox. `pull_request_target` runs only the default branch's copy, so it first reports on a PR opened after it merges. |
| Duplication | `scripts/Duplication.cs` in `derived` | no | Report only; docs 23 states it does not fail the build. | PR, main | By design. |
| Benchmark regression gate ([#404](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/404), [#412](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/412), [#408](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/408)) | job `regression-gate` in `benchmarks.yml`, step `Benchmark Regression Gate`, running `BenchmarkSummaryGenerator --baseline benchmarks/baseline.json` over the set in `benchmarks/gate-filter.txt`; comparison in `tools/BenchmarkSummaryGenerator/RegressionCheck.cs` | no | Fail-closed: a missing or measurement-less baseline fails the step, as does one in an older schema; a missing or empty results directory fails (#408); a run that shares no benchmark with the baseline fails (examined-N, printed as `N benchmark(s) compared`); the step passes `--fail-on-not-run` (a baseline benchmark that did not execute fails) and `--fail-on-new` (an executed benchmark the baseline lacks fails, so it cannot be ungated silently); a check never writes its own baseline (#412: `--bootstrap` or `--update-baseline` is required, and the step compares the file hash before and after). Curated subset: nine in-memory allocation-focused benchmarks at `Count=1000,10000`, keyed by class + method + parameters (no collision between the two `WriteSnappyAsync` declarations or on parameters other than `Count`). Only allocated bytes gate, with a 5% tolerance and a 4 KB floor; wall-clock is not compared (`--no-time`), because the baseline comes from another machine and a shared runner is noisy. The runtime is pinned with `DOTNET_ROLL_FORWARD=Minor` (.NET 8), since allocations differ across runtimes. Positive control: `BenchmarkGateEntryPointTests` drives the entry point through each vacuous case and a seeded allocation regression; `GatedBenchmarkListAndBaselineDescribeTheSameBenchmarks` asserts the list and the committed baseline agree. | Sunday cron, manual dispatch (`gate_only` skips the headline suite) (never a PR check; cannot be required, `BRANCH_PROTECTION.md`) | `notify-failure` (`issues: write` only, de-duplicated by title, #564 pattern) opens or updates `[CI] scheduled benchmarks failed` when the Sunday run fails. The baseline was recorded locally (machine, runtime and load in the file's `environment` and `recordedOn`), so the first scheduled run is the first comparison on a runner; if CI allocations differ from the local ones by more than the tolerance the run goes red and the baseline needs re-recording from the `update_baseline` candidate artifact. Re-recording is a person committing the artifact: the workflow never pushes it or opens a PR for it. A benchmark added to the list needs a baseline entry in the same PR. The `/benchmark` comment path refuses a pull request from a fork before reading its head ref and accepts only OWNER and MEMBER comments ([#386](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/386); `WorkflowConsistencyTests` requires the refusal in every comment-triggered workflow). 47 section 8 lists #404 and #412 as release blockers.
| Mutation testing | `mutation.yml` nightly | no | Report-only by design ([#440](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/440)). **Broken in practice, two causes.** (1) Until #569 it failed at "Restore local tools" (the dotnet/sdk #53783 mis-attribution `ci.yml:116` works around). (2) With that fixed it still failed 20 of 20 runs at the first Stryker step ([#575](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/575)): the `before=$(ls -1d StrykerOutput/*/ 2>/dev/null, piped through sort)` assignment exits non-zero on a fresh runner (no `StrykerOutput/` yet) and `pipefail` carries it into the assignment. Fixed by tolerating the empty listing (an `or true` guard on the four listings) and an explicit error when Stryker creates no output directory; `MutationWorkflowStepTests` runs both steps in bash against a fake `dotnet` (fresh runner, previous output present, no output created). Stryker itself has still never run in CI, so the first real nightly may expose a further fault. | nightly | Retry added in #569; not proven live (running it would also push `mutation/headline` and open a PR, which it now does only with `WORKFLOW_PR_TOKEN` set and a score that changed, #568). A failure now opens or updates one `[CI] scheduled mutation failed` issue (`notify-failure` job, `issues: write` on that job alone, #564). |
| Metrics oracle (Metrics.exe vs `CodeMetrics.cs`) | `metrics-oracle.yml` nightly on `windows-latest` | no | Compares both tools' output, fails on disagreement, and opens an issue (`:120-159`). Green on the last four runs. | nightly | Cannot run per PR (Windows executable). |
| Property-based fuzz, broad seeds | `fuzz.yml` weekly | no | **Could not pass.** `DefaultSeedsAreDeterministicAndStable` asserts the default seed (`SupportedSchemaPropertyTests.cs:198-203`) and the job sets `PARQUET_FUZZ_SEED`, so that one test failed every run (2026-09-14, 09-21, 09-28; 294 of 295 passed). The red run hid whether any real case failed. | weekly, manual | Filter fixed in #569. The per-push run (default seeds) is unaffected. A failure now opens or updates one `[CI] scheduled fuzz failed` issue (`notify-failure` job, #564). |
| Python ban, LFS and provenance, formatting, coverage envelope map, package vulnerability audit | `ci.yml:41-67`, `:76-77`, `:119-120`, `:139-140`, `:93-100` | via build | The audit greps the text `has the following vulnerable packages`, so a changed message passes. It does not cover the file-based `scripts/*.cs` apps. | PR, main | None filed. |
| CHANGELOG validation | `ci.yml:79-80` (`ParseChangelog.cs`); `release.yml:43-48` with `--release` | via build | Structure only: `[Unreleased]` first, well-formed version heading. Nothing requires a pull request to add an entry. In release mode it fails when nothing is cut. | PR, main, release | No "changelog touched" gate; whether one is wanted is a policy call. |
| README examples compile ([#596](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/596), [#595](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/595); 47 section 8 item 8) | `ci.yml` step `Verify README Examples Compile` runs `scripts/CheckReadme.cs`: blocks tagged `csharp compile` and `csharp compile-file` in README.md and PACKAGE_README.md (plus hidden `readme-compile-members` and `readme-compile-file` comments for the stubs and models the prose assumes) are extracted behind `#line` directives and built by `test/Parquet.SourceGenerator.ReadmeSnippets` against the generator and attributes as a consumer references them | via build (the `test` job) | Fails when a README has no compiled block, a fence never closes, or an example does not compile; the error cites the README line. Positive controls: `ReadmeCompileGateTests` (an example using the removed `ToListAsync`, an empty README, an unclosed fence) and a count of tagged blocks per README. Run against the pre-#595 README, it reports the `UserEventBatch` example's members that do not exist. | PR, main | Untagged blocks are not compiled: the diagnostics snippet (it must not compile) and shell or XML. A new example is ungated until it is tagged. |
| Release workflow guards | `release.yml` | no (manual) | Present: a real release from a non-main ref is refused in `prepare` and `publish` is skipped unless `github.ref` is main (#465); permissions are `contents: read` at workflow level and widened only on `publish` (#370 and #373, both closed); changelog is the version authority; an existing tag is refused; `dry_run` defaults to true, so a real publish needs an explicit false; the `verify-ci` job (holds `checks: read` and nothing else) queries the check-runs API for `github.sha` and fails unless every latest `build` check run from GitHub Actions concluded `success` (missing, pending and non-success all fail; on a dry run it warns instead), and `publish` needs it ([#570](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/570)). `CiGateIntegrityTests` keeps the dry_run default, both main-only guards, the `publish` dependency on `verify-ci`, and the single `checks: read` grant. Absent: the `release` environment has no protection rules or branch policy (R2); that is a repository setting, not a file. | manual dispatch | The release build omits CSharpier, the ledger check, the coverage gate, the IL gate, conformance, DuckDB, PyArrow and `generated-analysis`; it now relies on `verify-ci` for the commit having passed `build` on CI. A commit that re-ran `build` green after an earlier failure counts (latest run per name). |
| Action pins: 40-character SHAs (AGENTS.md rule 8) | every `uses:` under `.github/` | via build (test) | Was unenforced: `devskim.yml` and `semgrep.yml` used `@v4` and `@v1`. Now pinned, and `CiGateIntegrityTests.EveryThirdPartyActionIsPinnedToAFortyCharacterCommitSha` fails on any mutable ref (it asserts it examined more than ten references and read `ci.yml` and `release.yml`). | PR, main | The `returntocorp/semgrep` image is pinned by digest (`semgrep.yml`) and `astral-sh/setup-uv` is given an exact `version:` in `ci.yml` and `release.yml` (#567). Bumping either is now a deliberate edit; no updater tracks them. |
| Dependency lock files and package source mapping ([#394](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/394)) | `packages.lock.json` beside every solution project except the two Native AOT ones; every `dotnet restore` in the workflows passes `--locked-mode`; `NuGet.config` maps every id to nuget.org and the package-consumption `nuget.config` files map `Parquet.SourceGenerator*` to the local feed of packages the run just built | via build (the restore steps of `test`, `derived` and `generated-analysis`) | A graph change or a version edit without regenerated lock files fails with NU1004; the consumption projects cannot resolve our own package ids from nuget.org. Not covered: the consumption projects and the AOT projects restore without a lock file (their inputs change every run, or are runtime-specific), the scripts' file-based `#:package` references, and `.config/dotnet-tools.json`. Positive controls: `CiGateIntegrityTests` (a lock file per project, locked mode on every workflow restore, the mappings); a seeded version change fails the restore locally. | PR, main | Dependabot bumps now need the lock files regenerated (CONTRIBUTING). |
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

1. **A release did not verify that the commit passed CI** (fixed by #570: `verify-ci` job, and
   `dry_run` now defaults to true). Remaining: the `release` environment has no branch policy (R2),
   and the release build still re-runs only some of the gates.
2. **The IL gate no longer passes on empty input** (#422, landed in #467), and neither does the
   coverage gate (#416, #466). Both now fail when they examine nothing, as 47 section 8 requires.
3. **The performance gate is wired, weekly, and not a PR check** (#404, #412, #408, landed in #469).
   47 section 8 requires it. It gates allocated bytes of nine curated benchmarks; a regression
   merges on a green `build` and surfaces on the next Sunday run, as an issue.
4. **The emitted consumer API is reviewed, not gated.** A breaking change to the surface the 0.1
   contract protects can merge on a green `build`. Design choice, but it should be a decision
   recorded as such. #522 proposes the gate.
5. **Scheduled gates failed unseen** (fixed by #564). Mutation was red four nights and fuzz three
   weeks with no notification. Both now open or update one `area:ci` issue on failure, as the
   metrics oracle already did. Residue: a workflow that never starts (disabled schedule, GitHub
   dropping the cron) still notifies nobody.
6. **net472 was never executed** (#565: now executed by `net472-consumer.yml`, advisory until promoted into `build`). The release build still only compiles it.
7. **Ledger and call-graph vacuity.** The ledger rules are fixed (#430, #426); the missing zero-node assertion on
   the `src/` call graph.
8. **ExternalInterop tests passed when their input variable was unset** (fixed by #566: they fail under
   `CI`, and the release test step excludes the category).
9. **Supply chain residue** (fixed by #567): DuckDB CLI checksum, Semgrep image digest and `uv` version are pinned.
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

Workflow and test follow-ups (R5 to R8 are filed; R9 to R11 are not).

- **R5 (done in #565, advisory).** ([#565](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/565)) Add a `windows-latest` job that executes `PackageConsumptionLegacy` on net472, from the
  packages it builds itself. Add it to the `build` aggregate once it is stable.
- **R6 (done in #564).** ([#564](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/564)) Open an issue when a scheduled workflow fails, as `metrics-oracle.yml` does, for
  `mutation.yml` and `fuzz.yml`.
- **R7 (done in #566).** ([#566](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/566)) Make an `ExternalInterop` test fail, not return, when `CI` is set and its variable is
  empty; filter the category out of `release.yml:131-132` or supply the variables there.
- **R8 (done in #567).** ([#567](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/567)) Verify the DuckDB CLI zip against a pinned SHA-256, pin the Semgrep image by digest, and
  pin the uv version.
- **R9.** Add positive controls where only absence of evidence exists today: a seeded RS0016 and
  PARQAPI002 violation, a seeded CA1502 violation, a nonzero node count on the `src/` call graph, a
  row count on the cross-version matrix, and a test-count floor on the main suite.
- **R10 (benchmark gate done in #469).** Re-read the golden row and replace "in flight" with the merged
  behaviour.

- **R11. Create the `WORKFLOW_PR_TOKEN` secret** ([#568](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/568): a pull request opened or pushed with `GITHUB_TOKEN` starts no workflows, so it never gets `build` or `pr-title` and cannot merge). Until it exists the two PR-opening workflows open no PR: each prints `WORKFLOW_PR_TOKEN is not configured` and puts the result in the run summary and an artifact. (The one remaining `GITHUB_TOKEN` write is `benchmarks.yml` pushing a dispatch-targeted non-default branch; it warns that the push starts no checks.) To switch them on, the owner creates one repository secret:
  - **Name:** `WORKFLOW_PR_TOKEN`, under Settings, Secrets and variables, Actions, "New repository secret" (a repository secret, not an environment secret).
  - **Value:** a fine-grained personal access token (GitHub, Settings, Developer settings, Personal access tokens, Fine-grained tokens). Resource owner `rtkelly-labs`; repository access "Only select repositories" with `rtkelly-labs/Parquet.SourceGenerator`; repository permissions **Contents: Read and write** and **Pull requests: Read and write** (Metadata: Read-only is added automatically); nothing else. Expiry at most one year. Only a missing secret takes the "not configured" path: an expired token fails the push, and the scheduled-failure issue reports that, so put the renewal in a calendar. A machine-user account owning the token keeps the PRs from being authored by the owner; a GitHub App installation token (`actions/create-github-app-token`) avoids the expiry and is a drop-in replacement for the one `PR_TOKEN` env line in each workflow, at the cost of an App to maintain.
  - **Covers:** `benchmarks.yml` (the headline table PR, and the push when a dispatch targets a branch) and `mutation.yml` (the `mutation/headline` branch push and its PR). It does not cover `docs-dispatch.yml`, which opens no PR and has its own `DOCS_DISPATCH_TOKEN`; the `update_baseline` candidate is an artifact a person commits, so it needs no token.
  - Not done by the workflows: closing or refreshing [#474](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/474), the existing check-less headline PR.

## 6. In flight at the time of the audit

Merged since the first pass of this audit: [#465](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/465) (release permissions, main-only publish) and [#466](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/466) (coverage gate on empty report); their rows above describe the merged behaviour.

| Pull request | Rows it changes |
|:--|:--|
| [#519](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/519), [#520](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/520), [#521](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/521) derived report, derived history, protected paths | Derived-output rows, `ci.yml` line numbers (#519 merged; #520, #521 remain) |
| [#522](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/522) derived-output approval gate (docs) | Emitted consumer API change |

## 7. What this audit could not establish

- Whether the mutation retry fixes the nightly. The evidence is the error text and the fact that
  `ci.yml` carries the same retry for the same message; a live run was not made because the job
  force-pushes `mutation/headline` and opens a PR.
- Whether Dependabot reads file-based `#:package` directives and the tool manifest.
- Whether `dotnet test` exits non-zero when a filter matches no tests; no floor exists, so a
  discovery regression would depend on that behaviour.
- Socket and DeepSource thresholds; both are configured outside this repository.
