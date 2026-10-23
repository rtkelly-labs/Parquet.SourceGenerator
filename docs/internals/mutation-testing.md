# Mutation testing

`mutation.yml` runs Stryker.NET every night over the generator (`Parquet.SourceGenerator`) and
the shipped runtime helpers (`Parquet.SourceGenerator.Attributes`). Coverage tells you that a
line executed. Mutation testing tells you whether a test would have failed if that line were
wrong. The run reports a score and never gates on it (#261): there is no floor, and the
workflow does not fail because the number is low.

## What the score measures

The score is measured over the **behavioural suite only**. `stryker-config.json` filters out
these tests:

| Excluded | Why |
|:--|:--|
| `GoldenCodeGenRegressionTests`, `GeneratedApiBaselineTests`, `IlInterrogationTests` | They assert the text or shape of the emitted output, so they "kill" almost every mutation of an emitter, whether or not the behaviour changed. Leaving them in would report a flattering ~100% that says nothing about whether a test notices a wrong result. |
| `Category=ExternalInterop` | Needs external engines and inputs that the mutation job does not install. |
| `Category=DatasetIntegrity` | Checks the dataset files, not the generator. |

The round-trip, property-based, diagnostic and compile-check tests stay in. Before you change
the filter, ask whether the test fails because behaviour changed or only because text changed.
Only the first kind belongs in this suite.

`scripts/MutationSummary.cs` recomputes the score itself. It does not read Stryker's headline
number. Detected means `Killed` plus `Timeout`. The denominator leaves out `CompileError`,
`Ignored` and `Pending` mutants, and the summary names each of those counts so a reader can see
what the percentage covers. `NoCoverage` mutants count as undetected, and the summary reports
how many there are.

## How the sweep runs

A single Stryker run cannot finish inside one job. It creates 9358 mutants for the generator
and ran out of its 100-minute budget long before the end (#668). So the sweep is split into the
partitions listed in `stryker-partitions.json`:

1. **`plan`** checks the partition list and turns it into the job matrix. Each partition needs
   a unique name, a project, and either `mutate` globs or `"rest": true`. Each project needs
   exactly one `rest` partition.
2. **`mutate`** runs one job per partition. The job passes the partition's globs to Stryker as
   `--mutate` patterns. A `rest` partition passes `**/*.cs` and excludes the globs of every
   other partition of the same project, so a new source file is always mutated somewhere. Each
   job uploads its `mutation-report.json` as the artifact `stryker-report-<name>`.
3. **`summarize`** downloads every report and merges them with `MutationSummary.cs`. In each
   report, Stryker marks a mutant outside that partition's globs as `Ignored` with the reason
   "Removed by mutate filter". Every other status belongs to the partition that produced it.

A pattern can be limited to a line range with `{start..end}`, for example
`**/Parser/TargetParser.cs{1..891}`. That is how the two 2300-line files are split. Put
boundaries between methods. A mutant that crosses a boundary (a block removal spanning both
ranges) falls in neither range, so the `rest` partition picks it up.

### When the run fails

The run is meant to be a complete report or a red run, never a partial number that looks
complete. `summarize` fails, and says why, when:

- a partition has **no report**: its job failed, timed out or was cancelled;
- a partition's report is **not valid JSON**;
- a partition **tested no mutant**: its globs match nothing. Compile errors do not count,
  because every partition reports the ones it saw;
- **two partitions tested the same mutant**: their globs overlap.

The summary is still written to the job summary, followed by an "Incomplete report" section.
The headline PR is refreshed only when every partition succeeded and the run is on the
default branch. A failure on the default branch opens or updates one `[CI] scheduled mutation
failed` issue.

A partition job fails before it reaches Stryker when the benchmark datasets are not real
Parquet files. Stryker itself stops when its initial test run has any failing test
(`break-on-initial-test-failure`). A score measured against a broken baseline is wrong for
every mutant those tests cover. Until #668 this happened every night: the checkout did not
fetch Git LFS objects, so the 12 tests that read `benchmarks/data/*.parquet` read 131-byte
pointer files and failed, and Stryker carried on with "12 tests are failing".

`MutationSummaryGateTests` covers each of these failure cases. `MutationWorkflowStepTests`
runs the plan step and the partition step in bash against a fake `dotnet`.

## Rebalancing the partitions

Each partition job has a 100-minute budget, and its Stryker step gets 85 minutes. Most of that
time goes to the mutants: each test session starts a new test host, and the generator's
mutants are covered by many Roslyn-compiling tests. The fixed cost of a job is roughly the
setup, the Debug build of the solution, Stryker's initial test run and its coverage capture.
The summary lists how many mutants each partition tested. When a partition's job gets close to
its budget, split its globs, or add a line range, and keep one `rest` partition per project.

## Running one partition locally

```bash
dotnet tool restore --disable-parallel
git lfs pull                      # the dataset tests read benchmarks/data/*.parquet
dotnet tool run dotnet-stryker -- -p Parquet.SourceGenerator.csproj \
  --mutate "**/Emitter/Components/BatchValidationComponent.cs" \
  --output "$PWD/StrykerOutput/local" --concurrency 2
dotnet run scripts/MutationSummary.cs -- --partitions <a file listing just that partition> \
  --report-path "StrykerOutput/{name}/reports/mutation-report.json"
```

Even a one-file partition builds the whole solution and runs the full initial test run, so
expect about ten minutes before the first mutant is tested.
