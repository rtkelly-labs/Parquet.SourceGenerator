# 53 - Test Sufficiency Assessment for the 0.1 Baseline (#477)

> **Question:** is every supported path tested enough, at enough levels and with enough kinds of
> test, to cut `0.1`? **Short answer:** for the modern flat core, yes. For the legacy backend, the
> hostile-input contract, and every shape that is not a flat POCO, no. Section 8 has the verdict and
> section 9 the ordered list of tests to add.
>
> **Evidence baseline.** `main` at `2cc1508` (2026-10-01). CI run
> [36911545026](https://github.com/rtkelly-labs/Parquet.SourceGenerator/actions/runs/36911545026)
> (`CI & PR E2E Checks`, success) supplied the coverage report (`code-coverage`), the derived outputs
> (`derived-outputs`), the compatibility matrix and the job logs. The nightly mutation, weekly fuzz and
> nightly metrics-oracle histories come from `gh run list`. Nothing in this page was produced by running
> the suite locally. Every other claim was checked in the test sources. Where a document disagrees with
> the sources, the sources win and the disagreement is called out.
>
> **Not in the artifacts.** CI does not upload TRX files or a per-test report. Counts below are
> `[Fact]`/`[Theory]` declarations from the sources plus the totals printed in the job log. A per-test
> duration or flake history does not exist anywhere (see section 11).
>
> **In flight, not counted.** Draft #489 (one `<Model>ParquetReader`, drops `ToListAsync`) and draft
> #576 (one `<Model>Batch`, `AsBatches()`) rename most of the read surface the tests call. Sections 5
> and 9 name the tests that will have to move; the *paths* they cover do not change. #576 also adds the
> decompression guard to the batch reader (#358) with a test, so that row is marked "pending #576".
>
> **Scored against.** `docs/47` has no checklist called "strong tests". The yardstick used in section 8
> is its section 1 bullets (supported paths well tested; security-sensitive behaviour has explicit
> tested failure modes; generated code correct across the supported type matrix) and the section 8
> release gate.

## 1. Summary

| Area | State | Why |
|:--|:--|:--|
| Modern, flat, row-oriented write and read | **Strong** | Every supported primitive and logical type, required and nullable, across six codecs and five read terminals; differential tests against Parquet.Net; property-based harness; Native AOT executes the same paths. |
| Modern compound (struct, list, list of POCO) | **Adequate, narrow** | Round-trips on four hand-written models. `Where`, async-enumerable list reads, codecs, encodings, AOT and every external engine are untested for compound shapes. |
| Modern columnar (`Batches`, `ColumnarBatch`, Arrow ingest) | **Thin on types** | Of 23 flat types, `Batches` has behavioural tests for 3, columnar write for 10 (two only inside the AOT binary), Arrow ingest for 19. The rest are golden/compile only, and these emitters changed this week (#560, #562). |
| Legacy backend (Parquet.Net 4.x/5.x, net472) | **Weak** | One 5-column model executes. 15 of the 21 supported types never run. 20 emitter tests assert on text. Parquet.Net 5.x is never exercised. |
| Hostile input | **Partial** | Four modern read terminals are pinned. Eight open security issues have no test that fails today. Batch, `Where`, Arrow and legacy are not covered. |
| Gates | **Mostly honest, three dead** | Mutation has failed 20 of 20 runs and never produced a score. Broad fuzz has failed 3 of 3 and never run green. The benchmark gate is not wired. |
| Coverage number | **Measures the wrong thing for runtime risk** | 88.2% line / 71.9% branch is coverage of the generator and the Attributes helpers. Emitted code is not instrumented. |

## 2. Test inventory

### 2.1 By level and type

Counts are `[Fact]`/`[Theory]` declarations. One theory is many cases at run time: CI executed
**1302** cases in `Parquet.SourceGenerator.Tests` from **552** declarations (59 theories, 238
`[InlineData]` rows plus `[MemberData]`). Files are bucketed by name and purpose; a few straddle two
buckets and were placed by what they mainly assert.

| Level / type | Decl. | Where | What it proves | Behaviour or text |
|:--|--:|:--|:--|:--|
| Round-trip and behavioural (generated code executed) | 193 | `GeneratedTypeMatrixTests`, `TypeCoverageTests`, `NestedStruct/NestedList RoundTripTests`, `ColumnarHandoffTests` (19), `ColumnBatchReadTests` (11), `ParallelReadTests` (13), `RowGroupPruningTests` (20), `SortedRowGroupPruningTests` (13), `ReadBuilderTests`, `ColumnEncodingTests`, `StringDeduplication*`, `SerializerOptionsTests`, `NullVsEmptyAndLargePayloadTests`, `VersionAndSchemaEvolutionMatrixTests`, `TestDataIntegrationTests`, `Blittable*`, `StreamingChunkLifecycleTests`, others | Write then read through the generated API and compare values | Behaviour (some also assert on emitted text, e.g. `ReaderAllocationTests`, parts of `ColumnBatchReadTests` and `ColumnarHandoffTests`) |
| Parser and emitter unit | 132 | `DiagnosticTests` (26), `CompoundModelParsingTests` (22), `LegacyEmitterTests` (20), `ParserAndEmitterTests`, `LegacyCompoundModelParsingTests`, `AttributesTests`, `IncrementalityTests`, `ArrowConditionalEmissionTests`, `GeneratorConfigurationTests`, `SchemaFieldResolutionTests`, others | Model building, PARQ diagnostics, incremental caching, emitted text | Text, plus driver-level diagnostics |
| Golden / API-shape | 25 | `GoldenCodeGenRegressionTests` (9), `GeneratedApiBaselineTests` (11, tests of the API renderer), `BackendCompatibilityPolicyTests` (5) | Seven models emit, parse, and expose a non-empty API; legacy has no modern-only member | Text. Not snapshots (section 6.4). |
| Semantic compile of emitted output | 0 (script) | `scripts/CodeMetrics.cs` in the `derived` job | The seven golden models compile with their declarations | Compile only |
| Differential against Parquet.Net | 12 | `CorpusDifferentialSweepTests` (10), `BaselineDifferentialTests` (2) | Generated reader/writer agree with Parquet.Net's reflection serializer both ways | Behaviour |
| Property-based | 10 | `PropertyBased/SupportedSchemaPropertyTests` (3 properties x 24 seeds in CI, plus fixtures, shrinker, determinism checks) | Random flat schema, values, row-group layout, codec and encoding agree with an independent Parquet.Net dynamic-API engine | Behaviour |
| Hostile input and hardening | 29 | `Security/HostileParquetTests` (17), `PropertyBased/CorruptedParquetTests` (7), `DecompressionGuardStreamContractTests` (5) | Limits throw `InvalidDataException`; 16 named corruptions are rejected or read exactly; bit flips terminate under a 30 s timeout and a 256 MB allocation ceiling | Behaviour, except 24 `ShouldContain` text checks in `HostileParquetTests` |
| Fuzz (weekly) | same 10 | `fuzz.yml`, `PARQUET_FUZZ_CASES=250`, fresh seed | Wider seed range of the same suite | Behaviour. **Never green** (section 4.3). |
| Arrow ingestion | 19 | `ArrowRecordBatchBridgeTests` (10), `ArrowBatchStructuralValidationTests` (5), `ArrowDecimalExactnessTests` (4) | `RecordBatch` to Parquet for 19 kinds; malformed batches rejected | Behaviour. Export (Parquet to Arrow) is not shipped. |
| Runtime-helper unit | 31 | `VectorizedColumnTransformsTests` (13, 92 rows), `NullableColumnExtractorTests`, `BranchlessNullExtractionTests`, `Utf8StringDeduplicatorPrototypeTests` | SIMD and null-extraction helpers in the Attributes package | Behaviour (the prototype tests are an exception, section 6.1) |
| IL, zero-boxing, bounds checks | 18 | `ZeroBoxingSerializationTests` (6), `IlInterrogationTests` (8), `BoundsCheckEliminationTests` (2), `BranchlessDefinitionLevelIlTests` (2), plus `scripts/InterrogateIL.cs --check` in CI | Emitted IL has no `box`; extraction loops have no bounds-checked element access | IL, by design |
| API ledger and contract | 8 + build errors | `ApiChangeContractTests`, `scripts/CheckApiLedger.cs`, RS0016 and PARQAPI002 under `-warnaserror` | Catalogue and ledger agree | Parser of the ledger only. No test proves either analyzer reports (doc 51). |
| CI and tooling integrity | 70 | `CiGateIntegrityTests` (12), `WorkflowConsistencyTests`, `CoverageSummaryGateTests`, `BenchmarkRegressionTests` (19), `BenchmarkSummaryGeneratorTests`, `BenchmarkBaselineEquivalenceTests`, `BenchmarkDatasetsIntegrationTests` (8), `ParquetHashRegressionTests` (14), `ExternalInteropInputTests` | Gates and fixtures cannot pass vacuously | Behaviour of the tooling |
| ABI of frozen generated code | 8 | `test/Parquet.SourceGenerator.AbiMatrix` (v1.0 `OrderEvent` and `NestedOrder` executed against current Parquet.Net) | No `MissingMethodException` against the runtime | Behaviour. One Parquet.Net version (section 6.5). |
| External interop | 2 + 3 scripts | `PyArrowInteropTests` (2, `Category=ExternalInterop`); `generate_test_data.py --verify-generated`; `VerifyDuckDbInterop.cs`; `VerifyApacheConformance.cs` (parquet-cli 1.18.1) | PyArrow, DuckDB and parquet-cli read generated output; the generated reader reads PyArrow and DuckDB output | Behaviour. One canonical 7-column model. |
| Native AOT | 15 checks | `test/Parquet.SourceGenerator.AotTest/Program.cs`, published `-r linux-x64` and executed | Reflection-free round-trips incl. all six codecs, columnar hand-off, Arrow ingest | Behaviour. Flat only. |
| Package consumption | 2 consumers | `test/PackageConsumption` (net8, net9), `test/PackageConsumptionLegacy` (net8, net9, **net472 on Windows**) | The shipped `.nupkg`s compile and run from a local feed | Behaviour. Legacy consumer has five scenario groups. |
| Cross-version interop | 2 matrix rows | `test/CrossVersionInterop`, `cross-version.jsonl` artifact | Modern v6 writes, legacy 4.25 reads, and the reverse; reorder plus missing optional | Behaviour. Row count is printed, not asserted. |
| Compatibility matrix | 22 rows | `VersionAndSchemaEvolutionMatrixTests` and the `compatibility-matrix` artifact | Producer x consumer x schema case, incl. PyArrow 1.0 and 2.6, DuckDB 1.5.4, Parquet.Net 6.0.3 fixtures | Behaviour |
| Benchmarks and regression | 19 unit tests | `benchmarks/`, `tools/BenchmarkSummaryGenerator`, `benchmarks.yml` | The regression checker works. **The gate is not wired** (#469 and #577 draft, #404, #408, #412 open). | Not a gate |
| Mutation | 0 reports | `mutation.yml`, `stryker-config.json` | Nothing yet | Section 4.2 |
| Code-metrics ratchets | build analyzers | `CodeMetricsConfig.txt` (CA1502/1505/1506), `scripts/CodeMetrics.cs`, nightly Windows oracle | Complexity and maintainability cannot regress | Gate, not a test |
| Emitted-code analyzer gate | 7 files | job `generated-analysis`, `analysis/` | Every `src/` analyzer is clean on the emitted source; positive control seeds a CA2007 | Gate. 0 findings on 7 emitted files (1 legacy, 6 modern). |

**Totals.** `Parquet.SourceGenerator.Tests` 552 declarations; `AbiMatrix` 8; `AotTest` 15 console checks;
the two consumers are console programs. CI log counts per step: DatasetIntegrity 19, main suite 1302
plus AbiMatrix 8, ExternalInterop 2.

**Skips.** None. `Skip =`, `[Fact(Skip`, `[Ignore`, `Assert.Skip` and `SkipUnless` have zero hits under
`test/` and `tools/`. Trait exclusions are listed in 3.

### 2.2 Which tests run where

| Tier | What | Trigger | Blocks merge? |
|:--|:--|:--|:--|
| Required, through `build` | The `test` job: full suite (1302 + 8), DatasetIntegrity (19, own step), ExternalInterop (2, own step), IL gate, AOT publish and execute, package layout and consumption (modern and legacy on net8 and net9), cross-version interop, PyArrow/DuckDB/Apache scripts, coverage gate, coverage-envelope map, API ledger, `-warnaserror` build, CSharpier. `derived` (semantic compile, call graph, metrics). `generated-analysis`. | PR, push to main | Yes |
| Advisory | `net472-consumer.yml` (Windows: pack, run the legacy consumer on net472, hand a file net472 <-> net8). Path-filtered to `src/**`, the legacy consumer and interop models. Not in `build`. Semgrep, DevSkim, CodeRabbit, DeepSource, Socket. | PR, push | No (#565 done, promotion into `build` pending) |
| Nightly | Metrics oracle (green on every one of the last 5). Mutation (red, 20 of 20). | cron | No |
| Weekly | Broad fuzz (red, 3 of 3). | cron Monday 03:00 | No |
| Manual | `benchmarks.yml` (`/benchmark` comment or dispatch; comment runs on the last PRs were skipped), `release.yml` (dry run by default; its test step filters out only `ExternalInterop`). | dispatch | No |

## 3. Excluded and trait-gated

| Mechanism | Tests | Runs in |
|:--|:--|:--|
| `Category=DatasetIntegrity` | 19 (`ParquetHashRegressionTests`) | `.github/actions/verify-test-data` step, before the main run. Excluded from the main run by filter. |
| `Category=ExternalInterop` | 2 (`PyArrowInteropTests`) | Own step after the main run. They throw, not return, when `CI` is set and the input variable is empty (#566). Excluded from `release.yml`. |
| `Category=Integration` | `IlInterrogationTests`, `CoverageSummaryGateTests` | Not filtered anywhere; runs in the main run. Excluded from mutation. |
| Mutation `test-case-filter` | golden, `GeneratedApiBaselineTests`, `IlInterrogationTests`, ExternalInterop, DatasetIntegrity | By design (doc 26). |
| Fuzz filter | `DefaultSeedsAreDeterministicAndStable` | Excluded from `fuzz.yml` (#569). |

No test count floor exists. A discovery regression that dropped tests would still be green (doc 51).

## 4. Coverage, mutation, fuzz

### 4.1 Coverage (CI artifact `code-coverage`, `coverage.cobertura.xml`)

Gate: `scripts/CoverageSummary.cs --min-line 85.0 --min-branch 70.0` on the **aggregate**, non-vacuous
since #466. Codecov patch target 80% (advisory).

| Scope | Line | Branch |
|:--|--:|--:|
| **Aggregate, three packages summed** | **88.23%** (11,470 / 12,999) | **71.89%** (1,860 / 2,587) |
| `Parquet.SourceGenerator` (modern generator) | 92.97% | 79.85% |
| `Parquet.SourceGenerator.Attributes` (shipped runtime helpers) | 97.22% | 88.60% |
| `Parquet.SourceGenerator.Legacy` (legacy generator) | 76.06% | 51.00% |

Branch headroom over the gate is **1.9 points**. The aggregate hides the per-package numbers.

Modern generator by namespace:

| Namespace | Line | Branch |
|:--|--:|--:|
| `Emitter` (`CodeEmitter`, `ArrowBridgeEmitter`, pruning, batch) | 94.7% (7984/8430) | 86.9% |
| `Emitter.Columnar` | 98.8% | 88.1% |
| `Emitter.Compound` | 93.0% | 86.5% |
| `Emitter.Components` | 88.8% (3026/3406) | 70.5% |
| `Parser` | 89.3% (1854/2076) | 75.0% |
| `Models` | 89.0% | 64.3% |
| `Diagnostics`, root | 100% | 88.9% |

Lowest classes in the modern package: `ArrowMappingComponent` 17%, `ArrowBridgeEmitter` 74%,
`GeneratorConfiguration` 78%, `BufferPoolComponent` 83%, `RowGroupLayoutComponent` 84%,
`EmissionPlan` 84%, `TargetParser` 89%.

**Read the legacy number correctly.** `Emitter.Components`, `Models` and `Parser` are the same source
files compiled into both generator assemblies. Legacy-specific code is `Legacy.Emitter` 100% line / 98.2%
branch and `Legacy` 100% / 80%. The 76% comes from shared files the legacy path never calls
(`ArrowMappingComponent`, `RowGroupLayoutComponent`, `SortKeyEligibility` at 0%). It says legacy does
not use them, not that legacy is untested.

**Two caveats that matter more than the percentage.**

1. **Emitted code is not measured.** `coverlet.runsettings` excludes `[Parquet.SourceGenerator.Tests]*`,
   `[...CLI]*`, `[...AotTest]*` and anything with `GeneratedCodeAttribute`. Every generated
   `ReadParquetAsync`, writer, `Where`, `Batches` and Arrow body runs inside the excluded test assembly.
   "Emitter 94.7% line" means the emitter's string-building code executed, not that emitted branches did.
   `ArrowMappingComponent` at 17% in the modern package, with 10 Arrow tests that exercise 19 kinds, shows
   the gap: those tests drive emitted Arrow code, not the mapping component's own branches.
2. **Generator coverage comes only from in-process emitter and `CSharpGeneratorDriver` tests.** The
   generator that the compiler loads as an analyzer to build the test project's roughly 200 models is
   not instrumented.

The net effect: the coverage gate protects the generator's own logic and the Attributes helpers. It says
nothing about whether `Where` on a compound model, or `Batches()` over a `decimal` column, ever executed.
Section 5 is the substitute for that missing number.

### 4.2 Mutation

**No report exists.** `mutation.yml` ran 20 times since 2026-09-13; all 20 failed. No run uploaded a
Stryker report, and no `docs/MUTATION-HEADLINE.md` exists. The last four failed at `Restore local tools`
(dotnet/sdk #53783, fixed for the restore step by #569). On 2026-10-02 (run 36996757626) the restore
passed, but the step `Stryker - the generator` exited 2 about 20 ms after it began, printed nothing and
uploaded nothing. The cause is almost certainly the step's own shell, not Stryker:
`before=$(ls -1d StrykerOutput/*/ 2>/dev/null | sort)` under `set -euo pipefail` exits 2 on a fresh runner
where `StrykerOutput/` does not exist yet, and `pipefail` carries that into the assignment. A local
`bash -c 'set -euo pipefail; b=$(ls -1d nope/*/ 2>/dev/null | sort); echo ok'` prints nothing. The same
line is in the Attributes step. One-line fix (`|| true` on the `ls`); it is not in this PR. #575 is the
failure issue that #572 opened.

So the surviving-mutant list does not exist, and "do the behavioural tests detect a wrong emitter?" is
unmeasured. Doc 26 is explicit that goldens are excluded so the score cannot be inflated, and that no
threshold is set before a score is measured.

### 4.3 Fuzz (weekly)

`fuzz.yml` ran 3 times (2026-09-14, 09-21, 09-28), all red. Each time 294 of 295 passed and the one
failure was `DefaultSeedsAreDeterministicAndStable`, which asserts the default seed while the workflow
overrides it. A red run hides whether any real case failed. #569 excluded that test; the next scheduled
run is Monday 2026-10-05. **No broad fuzz run has ever completed green**, so there is no evidence that 250
seeds per property pass.

What the harness covers (read from `PropertyBased/*.cs`):

| Dimension | Covered | Not covered |
|:--|:--|:--|
| Backend | modern | legacy |
| Shape | flat, `FuzzWideRecord` (31 columns) | list, struct, list of POCO |
| Types | bool, sbyte, byte, short, ushort, int, uint, long, float, double, string, byte[], decimal (2 precisions), DateTime (ms, us), DateOnly, TimeSpan, Guid, enum, nullable forms of 10 of them | ulong, TimeOnly, `ReadOnlyMemory<byte/char>`, Interval, nullable DateOnly/TimeSpan |
| Write path | `WriteParquetBatchedAsync` only | `WriteParquetAsync` collection and async-enumerable overloads, columnar batch, Arrow |
| Read path | `From(stream).ToListAsync()` only | `ToArrayAsync`, memory, `AsAsyncEnumerable`, `Parallel`, `Where`, `Batches` |
| Layout and options | row-group size, 6 codecs, Dictionary/Delta/ByteSplit hints, string dedupe | dictionary threshold, `Max*` limits |
| Oracle | `IndependentParquetEngine` (Parquet.Net's dynamic `DataColumn` API: independent of generated code, **not** of Parquet.Net) | a second Parquet implementation |
| Corruption | 16 named corruptions; trailer bit flips (8 seeds, must reject); whole-file bit flips (terminate, bounded allocation) | semantic corruption of page headers, levels or dictionaries; only one baseline file |

Committed regression fixtures: one (`FuzzFixtures/seed-baseline-smoke.json`). #489 removes
`ToListAsync`, so `FuzzRunner` needs rewiring when it lands.

### 4.4 Other scheduled and derived evidence

| Gate | Evidence | State |
|:--|:--|:--|
| Metrics oracle | `metrics-oracle.yml` | Green, last 5 nightly runs (2026-09-28 to 10-02) |
| Emitted-code analyzers | `generated-diagnostics` artifact | `# examined 7 emitted files (legacy 1, modern 6)`, `# total 0` |
| Compatibility matrix | `compatibility-matrix` artifact | 22 rows, all `Compatible`, `CompatibleWithNulls` or `RejectedWithClearError`; PyArrow nested file is `RejectedWithClearError` (not read) |
| Cross-version | `cross-version.jsonl` | 2 rows, both `CompatibleWithNulls`, 3 rows each, one schema case |
| Code metrics | `derived-outputs/head/code-metrics.md` | Highest CC method 25 (`ArrowMappingComponent.TryMap`); `CodeEmitter` 2,729 SLOC |
| net472 execution | `net472-consumer.yml` | Green on the three most recent PR runs |

## 5. Path matrix

Cell key. **B** behavioural test executes generated code (write then read, or read of a real fixture).
**G** golden/compile only: the shape is emitted and semantically compiled (`derived`, plus the Tests
project's own build) but no test runs it. **U** unit test of parser or emitter text only. **P** parser
seam only. **X** not covered. **n/a** not shipped, rejected by a diagnostic, or absent by design.
A suffix names the strongest evidence. Derived from the test sources, not from docs 14 or 28.

### 5.1 Shape x entry point (modern backend)

| Shape | Write rows | Write async-enum | Write columnar | Read list/array (stream) | Read memory | `AsAsyncEnumerable` | `Batches` | `Parallel` | `Where` / prune | Sorted lookup | Arrow ingest |
|:--|:--|:--|:--|:--|:--|:--|:--|:--|:--|:--|:--|
| Flat required, wide | B | B | G (not in the matrix model) | B | B | B | G | B | G | n/a | B |
| Flat nullable, wide | B (Snappy, rg=1 only) | B | G | B | B | X | G | B | G | n/a | B (5 columns) |
| Flat, 9 columns incl. Guid, DateTime, `double?`, `long?`, `byte[]?` | B | X | **B** (byte-identical to row write) | B | X | X | X | X | X | n/a | X |
| Flat, 3 to 4 columns | B | X | X | B | B | B | **B** | B | **B** | n/a | X |
| Sorted flat (long and DateTime keys) | B | X | G | B | X | X | G | G | B | **B** (point, range, missing, duplicates, unsorted fallback) | X |
| List of leaf | B | X | n/a | B | B | **G** | n/a | B | **G** | n/a | n/a |
| List of POCO | B | X | n/a | B | B | **G** | n/a | B | **G** | n/a | n/a |
| Struct and struct-in-struct | B | B | n/a | B | B | B | n/a | B | **G** | n/a | n/a |
| Nullable value-type struct member | B | X | n/a | B | X | X | n/a | X | X | n/a | n/a |
| Map (`Dictionary<string,T>`) | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a |

Notes.

- **Models behind each row.** Flat required: `GeneratedTypeMatrixRecord` (23 columns), plus the models
  in `TypeCoverageTests`; its Arrow cell is `ArrowPrimitiveRow` (19 kinds). Flat nullable:
  `NullableGeneratedTypeMatrixRecord`, Arrow cell is `ArrowNullableRow`. The `ColumnarHandoffModel` row
  has 9 columns. The `ColumnBatchOrder`/`ColumnBatchMetric`, `PrunedOrder` and `ParallelRow` models have
  3 to 4 columns. Compound rows use `NestedOrder`, `ListRow`, `TripRow` and `NullableStructRow` from
  `NestedStructRoundTripTests` and `NestedListRoundTripTests`.
- **Map is parser-only.** The shipping generator's `CompoundKinds` is `Struct | List`
  (`ParquetIncrementalGenerator.cs:160`), so a map member is rejected. `CompoundModelParsingTests`
  covers the parser seam (string-keyed map builds, non-string key triggers PARQ006). No test drives a
  `Dictionary<string,T>` member through the shipping pipeline and asserts the diagnostic. Docs 14, 28
  and 42 claim it; nothing pins it.
- **Compound x `Where` is G only.** `NestedOrder`, `ListOrder` and `PocoOrder` all expose
  `Where(Func<...RowGroupMetadata,bool>)` in the derived API (checked in `derived-outputs`), but every
  `.Where(` call in `RowGroupPruningTests`, `SortedRowGroupPruningTests` and `ReadBuilderTests` is on a
  flat model.
- **Invalid combinations (doc 47 section 4.2).**
  `ReadBuilderTests.UnsupportedCombinationsAreAbsentRatherThanThrowing` asserts they are
  unrepresentable today (no `Parallel` on a stream source, none after `Where`). Once #489 collapses the states into one reader, doc 47
  requires a test per `NotSupportedException` row; those tests do not exist yet.
- **Columnar write is B on one model** (`ColumnarHandoffModel`) and, for more types, only inside the
  Native AOT binary.

### 5.2 Type x path (flat, modern unless stated)

| Type | Round-trip, required and nullable | Parallel | Columnar write | `Batches` | Arrow ingest | Fuzz | Native AOT | Legacy executed | External engine |
|:--|:--|:--|:--|:--|:--|:--|:--|:--|:--|
| `bool` | B | B | B | G | B | B | B | **X** (U) | X |
| `byte`, `ushort` | B | B | X | G | B | B (required only) | X | X | X |
| `sbyte`, `short` | B | B | X | G | B | B (required only) | X | X | X |
| `int` | B | B | B | G | B | B | B | B | B |
| `uint` | B | B | X | G | B | B (required only) | X | X | X |
| `ulong` | B | B | X | G | B | **X** | X | X | X |
| `long` | B | B | B | **B** | B | B | B | B | X |
| `float` | B | B | X | G | B | B | B | X | X |
| `double` | B | B | B | **B** | B | B | B | B | X |
| `decimal` (precision 18) | B | B | X | G | B (plus exactness) | B | B | **X** | B |
| `string` | B | B | B | **B** | B | B | B | B | B |
| `byte[]` | B | B | B | G | B | B | B | B | B |
| `ReadOnlyMemory<byte>`, `ReadOnlyMemory<char>` | B | B | G | G | X | **X** | X | n/a (PARQ011, U) | X |
| `Interval` | B | B | G | G | X | X | X | X | X |
| `Guid` | B | B | B | G | B | B | B | X | X |
| `DateTime` (ms) | B | B | B | G | B | B | B | **X** | B |
| `DateTime` (us) | B | B | X | G | B | B | B | X | X |
| `DateOnly` | B | B | X | G | B | B | X | X | X |
| `TimeOnly` | B | B | X | G | B | **X** | X | X | X |
| `TimeSpan` | B | B | B (AOT) | G | B | B | B | X | X |
| enum (int underlying) | B | B | B (AOT) | G | X | B | B | B (nullable only) | B (nullable) |
| enum, other underlying | **X** | X | X | X | X | X | X | X | X |
| `char`, `DateTimeOffset`, `BigInteger` | n/a (PARQ006 via driver, U) | | | | | | | | |

Reading the table: the "Round-trip" column is the strong one (`GeneratedTypeMatrixTests` runs 23
required columns across 6 codecs and 5 read terminals, plus a nullable twin). Every column to its right
is narrower. `Batches` is B for three types (`long`, `double`, `string`), columnar write for ten
(`bool`, `int`, `long`, `double`, `string`, `byte[]`, `Guid`, `DateTime`, and `TimeSpan` and enum only
inside the AOT binary), and legacy for six (`int`, `long`, `double`, `string`, `byte[]`, enum, with
nullable forms of `int`, `double` and enum).

### 5.3 Options, codecs, encodings

| Option | Modern flat | Modern compound | Legacy |
|:--|:--|:--|:--|
| 6 codecs (None, Snappy, Gzip, Lz4, Brotli, Zstd) | B (matrix, fuzz, AOT) | **X** (default codec only) | Gzip vs None size check only; 4 codecs **X** |
| `CompressionLevel` | **X** (`AttributesTests` checks `Enum.IsDefined`) | X | U (emitted text) |
| Encoding hints Dictionary, DeltaBinaryPacked, ByteSplitStream | B (`ColumnEncodingTests`, fuzz) | X | X |
| Dictionary threshold and sample size | B | X | X |
| `DeduplicateStrings` | B | X | X |
| `RowGroupSize` | B | B | B |
| `MaxDegreeOfParallelism` | B | B | n/a (no `Parallel` yet) |
| `MaxAllocationValues`, `MaxRowGroupCount`, `MaxNestingDepth`, `MaxDictionaryEntries`, `MaxStringLengthBytes` | B on four terminals (`HostileParquetTests`) | B (list, struct) on `ToListAsync` only | **U** (text assertions) |
| `MaxDecompressedPageSize`, `MaxDecompressionExpansionRatio` | B on four terminals | X | B (`DecompressionLimitIsAppliedAsync` in the consumer, net8 and net9 and net472) |
| Schema evolution (reorder, extra columns, missing optional, missing required) | B (22 matrix rows, fuzz) | X | B (`SchemaEvolutionRoundTrips`) |

### 5.4 Legacy backend x everything

Legacy emits flat models only, with `WriteParquetAsync`, `WriteParquetBatchedAsync`, `ReadParquetAsync`
and `ReadParquetArrayAsync` (`LegacyRecordParquetLegacyExtensions.api.txt`, 5 members). Streaming,
memory, `Parallel`, `Where`, columnar and Arrow are absent by the allowlist in doc 49.

| Path | Evidence |
|:--|:--|
| Write rows, batched write, read list, read array | B: `PackageConsumptionLegacy`, 5-column `Measurement`, net8, net9, **net472** |
| Cross-version with modern | B: 2 matrix rows, 4 columns, one schema case |
| Compression | B for Gzip only (size ratio), U for the rest |
| Decompression limit | B: one scenario |
| Sync-context blocking (classic ASP.NET style) | B: `SynchronizationContextScenario` with a positive control |
| 15 supported types (bool, byte, sbyte, short, ushort, uint, ulong, float, decimal, DateTime, DateOnly, TimeOnly, TimeSpan, Guid, Interval) | **X** executed. `LegacyCodeEmitterEmitsAllSupportedTypesCorrectly` asserts on text; the test project pins Parquet.Net 6.1 and cannot execute v4 output. |
| Nullable of any type except `int`, `double`, `string`, `byte[]`, enum | X |
| Row-count bound, narrowing, column length (#362) | X, and unfixed |
| Parquet.Net 5.x | **X** (consumer pins 4.25.0; docs claim "4.x and 5.x") |
| `Where`, `Parallel`, columnar, async-enum | n/a today; #494 and #495 will add them, and #493 is meant to run the shared behavioural suite against both backends |

## 6. What is weak

### 6.1 Tests that cannot fail, or fail for the wrong reason

| Test | Problem |
|:--|:--|
| `AttributesTests.ParquetCompressionMethodEnumValuesAreValid`, `...CompressionLevelEnumValuesAreValid` (10 rows) | `Enum.IsDefined` on a literal member. Removing a member is a compile error first. |
| `Utf8StringDeduplicatorPrototypeTests` (7) | Tests `Utf8StringDeduplicator`, a struct declared inside the test file and used nowhere in `src/` (its own comment says so). Zero product coverage. |
| `ReaderAllocationTests.EmittedReaderGuardsPageDecompressionBeforeParquetNetReadsIt` | `source.ShouldContain("CreateGuardedReadStream")` passed on `main` while `ReadBatchesIteratorAsync` handed the raw stream to `ParquetReader.CreateAsync` (#358). A text check on one emitted path is not evidence about the others. |
| `GoldenCodeGenRegressionTests` (9) | Asserts no error, syntax-clean, and `CountMembers > 0`. Nothing compares output to a baseline. The review diff is a sticky comment, not a gate (doc 51, #522). |
| `ColumnBatchReadTests` and `ColumnarHandoffTests` text cases (about 8) | Assert on emitted strings such as pool rentals. Fine as emitter tests, but the behaviour they stand for (no rentals, no POCOs) is checked only by an allocation ratio on one 4-column model. |

No test is without any assertion (a scan of all 555 `[Fact]`/`[Theory]` methods found none with no
assertion call), but that checks presence, not strength.

### 6.2 Text instead of behaviour

54 `ShouldContain`/`ShouldNotContain` calls in `LegacyEmitterTests` (20 tests, none executes generated
code), 34 in `DiagnosticTests` (appropriate), 24 in `HostileParquetTests` (partly message checks), 17 in
`ArrowConditionalEmissionTests` (appropriate), 16 in `ColumnarHandoffTests`, 11 in
`SortedRowGroupPruningTests`. The legacy backend's only executed proof is the consumer program; every
legacy hostile-input limit (`LegacyReaderEmitsDictionaryAndStringSafetyGuards`,
`LegacyStringLimitIsPostMaterializationValidation`) is text only.

### 6.3 Nondeterminism and load sensitivity

Data is seeded (`Bogus.UseSeed(42)`, `new Random(seed)`, a fixed default fuzz seed), so failures
reproduce. Remaining sensitivity:

| Test | Sensitivity |
|:--|:--|
| `CorruptedParquetTests` (whole-file bit flips, 16 corruptions) | 30 s read timeout and a 256 MB allocation ceiling measured with the process-wide `GC.GetTotalAllocatedBytes`; the `allocation-measurement` collection disables parallelism so the counter is trustworthy, at the cost of serialising these tests. On a loaded runner the timeout is the only real risk. |
| `ColumnBatchReadTests.BatchReaderAllocatesFarLessThanPocoReader`, `ReaderAllocationTests` | Ratio and absolute allocation thresholds. Deterministic in principle; runtime or Parquet.Net upgrades move them. |
| `EmittedConfigureAwaitTests` | 200 ms cancel, 30 s and 60 s waits on a single-threaded context. |
| `ParallelReadTests.RepeatedReadsAreStable` | The only concurrency repeat test. It cannot find a race that needs more than a few iterations. |
| Test data with `Guid.NewGuid()` | Values vary per run; assertions compare round-tripped values, so outcomes do not. |

### 6.4 Golden coverage that is only golden

The doc 05 section 1 text ("`Verify.SourceGenerators` snapshot testing") is stale. The test project
does not reference Verify; nothing is snapshot-compared. What golden gives today is (a) every one of
the seven models emits without error, (b) it parses, (c) `derived` compiles it with its declaration, and
(d) `generated-analysis` runs every analyzer over it. Seven models are 6 modern and 1 legacy, with about 28
distinct property type spellings between them (counting nullable and list forms separately). Types absent from every golden model: `ulong`, `uint`, `ushort`,
`sbyte`, `DateOnly`, `TimeOnly`, `Interval`, `ReadOnlyMemory<>`, microsecond timestamps. Those are
semantically compiled only because the Tests project declares them and is built `-warnaserror`, and
they are not analyzed by `generated-analysis`.

### 6.5 Matrix cells that are single-valued

- **Parquet.Net versions.** `AbiMatrix` declares `-p:ParquetNetPackageVersion` but no workflow passes it,
  so the "matrix" is one cell (6.1.0). Legacy is 4.25.0 only.
- **Operating systems and architectures.** Everything except the net472 consumer runs on
  `ubuntu-latest` (x64). AOT is `linux-x64` only. The SIMD helper tests (`VectorizedColumnTransforms`,
  `NullableColumnExtractor`) never run on ARM64.
- **Runtimes.** The xUnit assembly targets net8.0 only. net9 runs the consumers; net472 runs only the
  legacy consumer. The xUnit suite has never executed on net472.
- **Cross-version interop** is two rows over one 4-column schema.
- **External interop** is one 7-column model (int, string, string?, byte[]?, decimal(18,4), DateTime,
  enum?). No Guid, DateOnly, TimeSpan, narrow integers, nullable numerics, list or struct, and one
  codec. Legacy output is not checked by any external engine.

### 6.6 Other risks in the test machinery

- The golden review diff and the emitted consumer API change are advisory (#522).
- `RS0016` and `PARQAPI002` blocking depends on `-warnaserror` being on two command lines; no test seeds
  a violation (doc 51).
- `build` cannot see a job missing from `needs`, but `CiGateIntegrityTests` now fails if one is.
- The benchmark regression checker exits 0 when its results directory is missing (#408, open).
- `ApiChangeContractTests` covers the ledger parser, not the analyzers.

## 7. Hostile input against the open issues

For each issue: is there a test that fails on `main` today, and where would the pin go? "Code check"
means the defect was confirmed still present at `2cc1508` by reading the emitter.

| Issue | Defect | Pinned today? | Evidence |
|:--|:--|:--|:--|
| #358 `Batches()` skips the decompression guard | Raw stream to `ParquetReader.CreateAsync` | **No on main.** #576 adds `CreateGuardedReadStream` to the batch reader and a `SerializerOptionsTests` case. Code check: still raw in `ColumnBatchComponent.cs:222`. | `MaxDecompressedPageSizeRejectsCompressedPagesAcrossReadPaths` lists `ToListAsync`, `ToArrayAsync`, `Parallel`, `AsAsyncEnumerable` and not `Batches`. `HostileParquetTests` calls no `Batches`. |
| #359 Page-header parser accepts one Thrift shape, fails open | `(header & 0x0F) != 5 \|\| (header >> 4) != 1` | **No** | Code check: `DecompressionGuardComponent.cs:166`. `DecompressionGuardStreamContractTests` covers dispose, flush and Span/Memory overloads only. |
| #360 Guard validates only on `Seek` | `Read` and `ReadByte` are passthroughs | **No** | Code check: `DecompressionGuardComponent.cs:59-61`. |
| #361 `MaxAllocationValues` is a count, not a budget | Footer `NumValues` sizes buffers before any page is read | **No** | `ListColumnValuesExceedMaxAllocationValues...` and `TotalRowCountExceeds...` test the count limit. `CorruptedParquetTests` applies a 256 MB ceiling to corruptions of one 40-row file, but random damage rarely lands on `NumValues` and no corruption in `CorruptionCatalog` sets it deliberately; the issue's repro (a ~1 KB file allocating ~1 GB) is not reproduced anywhere. |
| #362 Legacy read: no row-count bound, unchecked narrowing, no column-length check | `totalRows += (int)RowCount` | **No** | Code check: `LegacyCodeEmitter.cs:750, 779`. Legacy limits are text assertions. |
| #363 Code injection via `[ParquetColumn]` name into `ColumnEncodingHints` | Unescaped literal | **No** | Code check: `CodeEmitter.cs:351`. `EmitterEscapingTests` uses quote, backslash and tab, and its model sets no `Encoding`, so it never reaches that line. |
| #364 Statement injection via Arrow `//` comment | Unescaped name in a comment | **No** | Code check: `ArrowBridgeEmitter.cs:519`. No test uses a name containing a newline. |
| #374 Footer fully parsed before any bound applies | | **Partly** | `CorruptedParquetTests`: footer length zero, huge, negative, overshooting, zeroed metadata. Rejection is asserted; allocation bound is the same 256 MB ceiling. |
| #369 `ColumnBatch` escapes the iterator | Copyable struct over pooled buffers | **No** (design pending #576) | `PooledBuffersAreRecycledAcrossRowGroups` proves the recycling that makes the hazard real. No misuse test exists. |
| #463 Guard's fail-open is unobservable | | **No** | |
| #403 Parallel-worker rentals leak on a throw; #393 TOCTOU on a caller-owned buffer; #399 eager pool return before writer dispose | | **No** (`EagerBufferReturnTests` (2) and `AbortedHostileReadPreservesArrayPoolHygiene` cover adjacent cases) | |
| #389 Blittable `MemoryMarshal.Cast` path never checks the property is the field | | Not verified | `BlittableStructPropertyTests` (10) exist; whether one pins the field-identity case was not read. |

Coverage across read terminals for the limits that *are* pinned:

| Terminal | Hostile test | Notes |
|:--|:--|:--|
| `From(stream).ToListAsync/ToArrayAsync` | B | |
| `From(bytes).Parallel().ToArrayAsync` | B | |
| `AsAsyncEnumerable` | B | |
| List and struct models | B on `ToListAsync` only | |
| `Where(...)` (filtered read) | **X** | |
| `Batches()` | **X** (#576 pending) | |
| Arrow ingestion | B for structural validation only (`ArrowBatchStructuralValidationTests`) | No limit on array length or total bytes |
| Legacy | one decompression scenario (B), the rest text | |
| `ReadPrunedRangeAsync` (#387) / sorted lookups | X | |

## 8. Verdict

**Not sufficient for a `0.1` baseline as docs 47 defines it. Sufficient for the modern flat core.** The
suite is broad and mostly honest, and the CI integrity work of the last two weeks closed the vacuous
gates. The shortfalls are specific.

| Doc 47 section 1 bullet | Rating | Basis |
|:--|:--|:--|
| The supported paths are well tested | **Partial** | Modern flat: strong (section 5.2 first column). Modern compound: adequate for write/read, missing `Where`, AOT, codecs, external engines. Legacy: weak (5.4). |
| Security-sensitive behaviour has explicit, tested failure modes | **Partial** | Limits pinned on four modern terminals and 16 corruptions. Of the nine issues #358 to #364, #374 and #463 (doc 47 section 5.2 names #358, #359, #360, #374, #463), none has a test that fails on `main` today: #358 gets one with #576, #374 is partly pinned, the other seven are not. `Where`, `Batches`, Arrow and legacy are unpinned. |
| Generated code is correct across the supported type matrix | **Partial** | 23 types x 6 codecs x 5 terminals for flat modern. Columnar write, `Batches`, Arrow, AOT and legacy each cover a subset (5.2). |
| The architecture can accommodate a future Arrow generator | n/a | Not a test property. |

| Doc 47 section 8 gate item | State |
|:--|:--|
| Critical/high correctness and security paths addressed or scoped out | **Open.** #358 to #364, #369, #389 open. |
| Coverage, golden, IL, AOT, performance gates non-vacuous (#416, #422, #407, #404, #412, #413) | #416, #422, #407, #413 closed. **#404 and #412 open: no benchmark is checked in CI.** AOT is non-vacuous but flat only. The coverage gate is non-vacuous and measures the generator, not emitted code. |
| Modern and legacy supported subsets documented | Documented (14, 42, 49), but the legacy subset is not tested to the extent documented. |
| Nested-support scope matches the actually tested backend matrix (42) | **Mostly.** Doc 42 states modern compound is tested on the golden corpus and that AOT, property-based fuzzing and external interop for compound are later work. That is accurate. Map is stated as unsupported and is not pinned through the shipping pipeline. |

What is genuinely strong and should be said plainly: the flat modern type matrix, the differential
tests against Parquet.Net, the CI integrity tests (gates have positive controls), the corrupted-file
harness, Native AOT executing the real binary, and the net472 consumer now running on Windows.

## 9. Tests to add before 0.1, in priority order

Sizes: **S** is under about 100 lines or a workflow edit. **M** is 100 to 400 lines. **L** is a new
harness or over 400 lines. Sequence anything that calls the read or batch API after #489 and #576 land,
or write it against the names they introduce.

| # | Test | Put it in | Size | Closes |
|:--|:--|:--|:--|:--|
| 1 | Legacy executed type matrix: one model with every legacy-accepted type, required and nullable, all six codecs, multi-row-group, run on net8, net9 and net472 | `test/PackageConsumptionLegacy/TypeMatrixScenario.cs` (new, alongside `SynchronizationContextScenario.cs`), called from `Program.cs`. Move into the shared two-backend suite when #493 lands. | M | 15 untested legacy types, 4 untested codecs |
| 2 | Hostile terminal sweep: a theory over every read terminal (`ToList`, `ToArray`, memory, `AsAsyncEnumerable`, `Parallel`, `Where`, `Batches`, compound and flat) x every limit and corruption in `HostileParquetTests`, so a new terminal fails until it is covered | `Security/HostileReadPathTests.cs` (new) | M | #358, `Where`, `Batches`, compound |
| 3 | Guard internals: hand-built page headers with reordered fields, a CRC field, the long field form; `Read` and `ReadAsync` crossing a page boundary without a `Seek`; fail-open path raises an observable signal | `Security/DecompressionGuardTests.cs` (new), reflecting into the emitted nested type as `DecompressionGuardStreamContractTests` does | M | #359, #360, #463 (fix likely needed first) |
| 4 | Allocation budget: a ~1 KB file with footer `NumValues` set huge across many columns must stay under a fixed budget | `Security/HostileParquetTests.cs` using `AllocationMeasurement` | S | #361 |
| 5 | Name injection: column names with newline, `*/`, `"; Environment.Exit(1);`, with `Encoding = ...` set and with Apache.Arrow referenced; assert zero error diagnostics and no statement outside the intended literal or comment | `EmitterEscapingTests.cs` | S | #363, #364 |
| 6 | Legacy hostile: crafted legacy-readable files with a lying row count, an oversized column and a short column (`reader.RowGroups[r].RowCount` beyond int) | `test/PackageConsumptionLegacy/HostileScenario.cs` (new) | M | #362 |
| 7 | Fix `mutation.yml` (`|| true` on both `ls` lines), dispatch once, record the first score and survivors | `.github/workflows/mutation.yml`, then `docs/26` | S, then open-ended triage | No mutation score exists |
| 8 | Confirm the first green broad fuzz run (2026-10-05 or dispatch) | `fuzz.yml` | S | No green broad run exists |
| 9 | Widen the property harness: add `ulong`, `TimeOnly`, `ReadOnlyMemory<byte/char>`, `Interval`, remaining nullable forms; add checks that write via the collection overload, the async-enumerable overload and columnar batches, and read via memory, `Parallel`, `Where` (compare with LINQ over the rows) and `Batches` | `PropertyBased/FuzzWideRecord.cs`, `FuzzColumns.cs`, `FuzzRunner.cs` | M to L | Property coverage of paths and types |
| 10 | `Batches()` and `ColumnarBatch` type matrix: `GeneratedTypeMatrixRecord` through `Batches` against `ToList`, and columnar write byte-identical to row write for all 23 types | `ColumnBatchTypeMatrixTests.cs` (new) | M | 20 types without a `Batches` test and 13 without a columnar-write test, on the paths changed by #560 and #562 |
| 11 | Compound path matrix: `Where` with statistics on struct and list leaf columns, list `AsAsyncEnumerable`, every codec and encoding hint over `NestedOrder` and `TripRow`, memory read | `NestedPathMatrixTests.cs` (new) | M | Compound x `Where`, codecs, encodings |
| 12 | Doc 47 section 4.2 combinations: one test per `NotSupportedException` row once #489 defines them | `ReadBuilderTests.cs` (`UnsupportedCombinationsAreAbsentRatherThanThrowing` becomes it) | S | Doc 47 explicit requirement |
| 13 | Concurrency and cancellation: `Parallel` under MaxDOP 1 to 16 repeated 200 times over one buffer; cancel mid-flight (not pre-cancelled); a throwing worker returns every rental; cancel during a write leaves no pooled array rented | `ParallelReadStressTests.cs` (new) | M | #403, #393, cancellation gap |
| 14 | `Batches()` lifetime misuse (retain a batch past `MoveNext`, dispose twice, read after dispose) once #576 defines the model | `UnifiedBatchTests.cs` (from #576) | S | #369 |
| 15 | Map member through the shipping pipeline asserts PARQ006 | `DiagnosticTests.cs` | S | Map claim in docs 14, 28, 42 |
| 16 | Legacy x Parquet.Net 5.x consumption (consumer with `Parquet.Net` 5.x on net8) | `test/PackageConsumptionLegacy` second restore, `ci.yml` step | S | "4.x and 5.x" claim |
| 17 | Native AOT on the new surface: a compound model, `Where`, `Batches` or `AsBatches`, and a legacy-free path | `test/Parquet.SourceGenerator.AotTest/Program.cs` | M | AOT flat only |
| 18 | Shared two-backend behavioural suite run on `net472;net8.0` | #493 | L | xUnit never runs on net472 or against legacy |
| 19 | Large-file and streaming memory: write 2M rows via async-enumerable and read via `AsAsyncEnumerable` with a peak working-set bound; mark slow and run nightly | `LargeFileStreamingTests.cs` (new, trait `Slow`) | M | No bounded-memory test |
| 20 | Widen external interop: canonical model gains Guid, DateOnly, TimeSpan, narrow ints, nullable numerics, every codec, plus a list/struct file read by DuckDB and PyArrow; run legacy output through the same engines | `test/Parquet.SourceGenerator.CLI/PyArrowInteropModel.cs`, `scripts/generate_test_data.py`, `VerifyDuckDbInterop.cs` | M | One 7-column model only |
| 21 | Remove or relocate vacuous tests; add a test-count floor; seed a RS0016, PARQAPI002 and CA1502 violation (doc 51 R9) | `AttributesTests.cs`, `Utf8StringDeduplicatorPrototypeTests.cs`, `CiGateIntegrityTests.cs` | S | Section 6.1 |
| 22 | ARM64 run of the helper tests (`ubuntu-24.04-arm`) | `ci.yml` matrix on the SIMD test subset | S | SIMD only on x64 |

Items 1 to 6 belong before the tag. 7 and 8 are cheap and should happen this week. 9 to 13 should land
before `0.1` unless the owner scopes them out in writing (doc 47 allows "scoped out of the stable
contract"). 14 follows the batch design. 15 to 22 are release-quality rather than release-blocking.

## 10. Test types missing entirely

| Type | Today | Note |
|:--|:--|:--|
| Shared behavioural suite across both backends | Absent | #493 plans it. Today legacy has no xUnit execution at all. |
| Mutation score | Absent | No report in 20 attempts. |
| Concurrency stress on `Parallel` | Absent | One repeat-three-times test. |
| Mid-flight cancellation | Absent | Pre-cancelled token and iterator contracts only. |
| Large-file and bounded-memory | Absent | Largest data is the LFS benchmark set and a 256 MB ceiling on a 40-row file. |
| Hostile-input corpus from real files | Absent | Corruptions are synthetic mutations of one file. A corpus (for example the bad-data files in `apache/parquet-testing`) would exercise structure the mutator cannot reach. |
| Property-based or differential tests for compound shapes | Absent | Differential tests exist for flat models and for three compound models (`NestedOrder`, `ListRow`, `TripRow` in `CorpusDifferentialSweepTests`); fuzz is flat only. |
| Property-based and differential for the legacy backend | Absent | |
| Snapshot tests of emitted code | Absent | Doc 05 section 1 describes them; the project does not have them (section 6.4). Goldens are review aids. |
| Windows, macOS and ARM64 execution of the xUnit suite | Absent | Only the net472 consumer runs off Linux x64. |
| Differential against a second Parquet implementation | Partial | PyArrow, DuckDB and parquet-cli read generated output (one model); the fuzz oracle is Parquet.Net. |
| Native AOT on compound and the new batch surface | Absent | |
| Performance regression as a gate | Absent | Checker exists and is unit tested; not wired (#469, #577). |

**Differential testing is not on this list.** `BaselineDifferentialTests`, `CorpusDifferentialSweepTests`
(10) and the fuzz harness's independent engine are all differential checks against Parquet.Net. The gap
is narrower: the fuzz harness drives one write overload and one read terminal, and no differential check
exists for legacy.

## 11. What could not be verified

- **Per-test results, durations and flake history.** No TRX is uploaded. Section 6.3 is from reading the
  sources.
- **Run-time case counts per class.** Only the totals (1302, 8, 19, 2) are in the log.
- **Whether `#389` is pinned.** `BlittableStructPropertyTests` was not read for the field-identity case.
- **The cause of the 2026-10-02 mutation failure.** Inferred from the step's timing and script, and the
  shell behaviour was reproduced locally in isolation; a real run on a runner has not confirmed it.
- **Whether `Where` on a compound model prunes correctly.** Only that it is emitted and compiled and not
  executed.
- **Effect of #489 and #576 on any count here.** Both are drafts and were read only for the #358 change.
- **Legacy hostile limits at run time.** No executed evidence either way beyond the decompression case.
