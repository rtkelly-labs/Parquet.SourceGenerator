# Diffra shadow payloads

CI turns some of its existing outputs into the shapes [Diffra](https://github.com/rtkelly-labs/Diffra)
defines for its standard types, and uploads them as artifacts. **Nothing gates on them.** Every
step is `continue-on-error`, and the checks that decide a build are the ones in the
[CI gate matrix](./ci-gate-matrix.md). The purpose is to try Diffra's schemas on real data before
anything depends on them, and to write down where the data does not fit.

The benchmark payloads are described in [the benchmarks reference](../reference/benchmarks.md).

## Files

| File | Purpose |
|---|---|
| `diffra.project.yml` | The project file: scope, dimensions and the declared metrics. |
| `diffra/schemas/` | The schemas the payloads are checked against, vendored so that no step or test fetches one. Its README records each file's source. |
| `scripts/BenchmarkDiffraPayload.cs` | Converts the regression gate's BenchmarkDotNet export to one `diffra/benchmark@1` payload and a `collection-v1` document. |
| `scripts/TestResultsDiffraPayload.cs` | Converts TRX reports to `diffra/test-results@1` payloads and a `collection-v1` document. |
| `scripts/JsonSchemaCheck.cs` | Validates JSON documents against a JSON Schema file. It carries its own positive control. |

## Test results

The `test` job writes three TRX reports, the same three that
`scripts/VerifyRequiredTestExecution.cs` reads. The step `Emit Diffra test-results payloads
(shadow)` converts each to one payload, with the report as the `suite` stratum:

| Suite | Report |
|---|---|
| `core` | `test/Parquet.SourceGenerator.Tests/TestResults/ci-core.trx` |
| `abi` | `test/Parquet.SourceGenerator.AbiMatrix/TestResults/ci-core.trx` |
| `external` | `test/Parquet.SourceGenerator.Tests/TestResults/ci-external.trx` |

The artifact `diffra-test-results` holds:

- `test-results.<suite>.json`: `{ scope, methodology, stratum, plan, metrics.tests.results }`,
  where each result is `success` (with a duration), `error` (duration, kind and message) or
  `skipped` (reason);
- `collection.json`: each suite as `collected`, `missing` or `failed`, and `complete` only when all
  three were collected;
- `summary.md`: the counts that show how the data fits, also appended to the job summary.

The step then validates the payloads and the collection document against the vendored schemas.

### How a TRX result becomes a payload entry

| Payload | Rule |
|---|---|
| Key | The TRX `testName`, prefixed with the class name when it does not already start with it. Control characters are written as `\uXXXX` text. |
| One key, several results | The worst outcome is kept: error, then success, then skipped. |
| `success` | Outcome `Passed`. |
| `error` | `Failed`, `Error` and `Aborted` with kind `exception`; `Timeout` with kind `timeout`. The message is the TRX error message, cut to 4096 characters, or the outcome when there is none. |
| `skipped` | Every other outcome. The reason is the TRX message, or the outcome when there is none. |
| `duration` | Whole nanoseconds as a decimal string. TRX records 100 ns ticks. `0` when the report has no duration. |
| `plan.tests.keys` | The result keys, plus each test the report defines without a result; those are also listed under `plan.tests.failed`. |

The step runs after a failed test step as well. When the core tests fail, the external tests
never run, so `ci-external.trx` does not exist and the collection document is `incomplete`. When
no report can be converted the script writes nothing and exits 1, because a collection document
needs at least one collected stratum.

`TestResultsDiffraPayloadTests` drives both scripts on a synthetic report that has the outcomes a
green CI run never produces, and checks that the validator rejects a payload with a field outside
its tag and a collection that claims `complete` with a missing suite.

### Where the data does not fit

The findings are reported to Diffra rather than kept here in full. The ones that shape this
prototype:

- **The design has moved.** Diffra's current decision record
  ([ADR 0002](https://github.com/rtkelly-labs/Diffra/blob/main/docs/adr/0002-project-file-and-datasets.md))
  replaces the project file and the collection document used here with one root file and
  record entries, and has Diffra read TRX itself. `project-v1` and `collection-v1` remain in
  Diffra only until the replacement exists. They are used here because they are the only schemas
  that say what a test result looks like.
- **No published payload schema.** Diffra's example payload schema fixes the example's own scope,
  methodology and strata, so this repository derives its own copy.
- **A report is not a dimension.** The collection document has nowhere to name an input file, so
  the three reports are modelled as a `suite` dimension. The project file cannot say which report
  belongs to which suite.
- **TRX does not record where it ran.** The operating system and architecture come from the
  machine that runs the converter, and the target framework is not recorded at all.
- **TRX has no plan.** The planned keys are the keys that ran. The plan this repository enforces
  is a list of required test classes, which the payload cannot express.
- **Failures are all `exception`.** TRX does not separate an assertion from any other exception,
  so the `assertion` kind is never produced.
- **Nothing collected cannot be written.** `collection-v1` requires one collected stratum.
- **Benchmark keys need a parameter list.** Diffra's example schema for a benchmark key requires
  `Type.Method(...)`. A benchmark without parameters is `Type.Method` in `benchmarks/baseline.json`,
  so the copy here makes the parentheses optional.
- **Thresholds combine with "and" only.** The earlier GC budget warned when either the absolute or
  the relative increase was passed. A project file can only require both.
- **One payload, several strata.** The benchmark payload carries the processor in its stratum, but
  `allocatedBytes` is stratified without it, so the collection document names the same payload
  under two different strata.

## Running it locally

```bash
dotnet run scripts/TestResultsDiffraPayload.cs -- artifacts/diffra-test-results \
  --commit "$(git rev-parse HEAD)" \
  --input core=test/Parquet.SourceGenerator.Tests/TestResults/ci-core.trx

dotnet run scripts/JsonSchemaCheck.cs -- \
  --schema diffra/schemas/test-results-payload.schema.json artifacts/diffra-test-results/test-results.core.json \
  --schema diffra/schemas/collection-v1.schema.json artifacts/diffra-test-results/collection.json
```
