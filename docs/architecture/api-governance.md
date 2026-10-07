# API Governance & Change Contract

## The rule

> **Nothing enters a governed API surface without appearing in a catalogue file *and* carrying a
> `docs/api/LEDGER.md` entry that classifies its semver impact.**

An unapproved addition fails the **build**, not merely a test. Visibility is necessary and is not sufficient: a listing that
only a test consults is a listing that grows while nobody is looking.

Two surfaces are governed by catalogues today. The third — the emitted consumer API — was, until its
checked-in baselines were retired; it is now **reviewed** rather than catalogue-gated, through the
derived-output diff CI posts on every pull request ([below](#the-emitted-surface-reviewed-not-catalogued)).

This exists to serve [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230), the
`0.1.0` API freeze. At the freeze, the ledger *is* the record of what was decided and why. A ledger
that was filled in retrospectively would record nothing, which is why the seeding decision below is
deliberate.

## The three surfaces

| # | Surface | What it is | Catalogue | Gate | Enforced by |
|:--|:---|:---|:---|:---|:---|
| 1 | **Emitted consumer API** | Everything the emitters write into a consumer's own compilation. Exists in no shipped assembly. | `src/api/emitted-api-budgets.txt` (budgets) | shape budget gate + PR review diff | `scripts/DerivedOutputs.cs` & `GeneratedApiBudgetGateTests` (budget gate); sticky derived PR comment (review diff) |
| 2 | **Shipped package API** | `public` members of `Parquet.SourceGenerator.Attributes`. (The generator assemblies ship only as analyzers and have no public surface; their types are `internal` since #461.) | `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` | `RS0016` | `Microsoft.CodeAnalysis.PublicApiAnalyzers` (pre-existing; unchanged) |
| 3 | **Internal seams** | Members in `src/` widened past `private` so another component can call them. | `src/api/seams.txt` | `PARQAPI002` | `InternalSeamGateAnalyzer` (build **error**) |

Both catalogues, and the derived emitted `.api.txt`, use **one grammar** — the grammar
`PublicAPI.Shipped.txt` already used in Roslyn ecosystems. One signature per line, self-contained,
ordinal-sorted, `->` introducing the return type. Learn it once.

## The Catalogue Signature Grammar

The grammar is shared across all surfaces so contributors and reviewers learn one syntax:

| Construct | Grammar Line Example |
|:---|:---|
| Header | `#nullable enable` — always line 1 |
| Type | `Sample.Space.Widget` — generic types keep their parameter list, `Ns.Box<T>` |
| Nested type | `Sample.Space.Widget.Batch` |
| Method | `static Ns.T.ReadAsync(System.IO.Stream stream, int max = -1) -> System.Threading.Tasks.Task<int>` |
| Constructor | `Ns.T.T(int rowGroupIndex, long rowCount) -> void` |
| Property | `Ns.T.RowCount.get -> int`, `Ns.T.Label.set -> void`, `Ns.T.Label.init -> void` |
| Indexer | `Ns.T.this[int index].get -> string?` |
| Field | `static readonly Ns.T.Schema -> Parquet.Schema.ParquetSchema` |
| Constant | `const Ns.T.Limit = 512 -> int` |
| Enum member | `Ns.E.Ten = 10 -> Ns.E` |
| Event | `Ns.T.Changed -> System.EventHandler` |
| Operator | `static Ns.T.operator +(Ns.T left, Ns.T right) -> Ns.T` |

### Invariant Rules:

1. **`->` introduces the return type.** `void` is written out explicitly rather than omitted.
2. **Every line carries its fully-qualified containing type.** One generated or internal file routinely holds
   several types. A line is therefore self-contained: adding or removing an unrelated member never rewrites it.
3. **Parameters carry type, name and default value**, in source order:
   `(System.IO.Stream stream, ParquetSerializerOptions? options = null)`. Parameter modifiers
   (`this`, `ref`, `out`, `in`, `params`, `scoped`) are preserved as part of the contract.
4. **API-relevant modifiers are prefixed in one fixed canonical order**:
   `const static readonly required abstract virtual override sealed`. Accessibility keywords (`public`, `internal`),
   `partial`, `async`, `unsafe`, `extern`, `new`, and `volatile` are implementation details and are omitted.
5. **Only externally-reachable declarations are listed.** A member appears when it is accessible past `private`.
6. **One member per line, ordinal-sorted, duplicates collapsed.**

Surface 2 is deliberately left exactly as it was. It already had a build-error gate that works, has
years of Microsoft maintenance behind it, and produces messages contributors already recognise.
Reimplementing it would have been a downgrade dressed as consistency.

## What triggers a gate, and what does not

**This is the point of the whole design.** A golden model's `.g.cs` holds the full emitted body, so
a retuned buffer loop and a new public overload produce diffs of the same visual shape. The
`.api.txt` rendering holds only signatures. Therefore:

| Change | Emitted `.g.cs` | API listing | Gate fires? | Ledger entry? |
|:---|:---:|:---:|:---:|:---:|
| Retune a buffer loop, change an allocation strategy | changes | unchanged | no | no |
| Reword an XML doc comment on emitted code | changes | unchanged | no | no |
| Reorder emitted statements, rename a local | changes | unchanged | no | no |
| Add a public method, overload, property or nested type | changes | `.api.txt` **gains a line** | no — shown in the PR comment for review | not required |
| Change a parameter type, name, default, or return type | changes | `.api.txt` **line changes** | no — shown in the PR comment for review | not required |
| Remove a public member | changes | `.api.txt` loses a line | no — shown in the PR comment for review | not required |
| Widen a `private` member in `src/` to `internal` | — | `seams.txt` gains a line | **yes** (`PARQAPI002`) | **yes** |
| Add a `public` member to the `Attributes` package | — | `PublicAPI.Unshipped.txt` gains a line | **yes** (`RS0016`) | **yes** |

Body, perf and comment changes are not API changes, and the contract must not tax them — a gate
that fires on every performance commit is a gate that gets suppressed. The derived-output diff keeps
that property for the emitted surface: those changes leave its "Emitted public API" section empty.

A model-specific surface claim in `GoldenCodeGenRegressionTests` (a `ShouldContain` /
`ShouldNotContain` on the emitted source) still fails the test run when a change breaks it, and
`BackendCompatibilityPolicyTests` still holds the classic backend to its declared core surface
([Compatibility Matrix](../reference/compatibility-matrix.md)). Those are behavioural claims about specific members, not a
catalogue.

### Division of labour between the build and CI

Each gate enforces the half of the rule it can actually see.

- **The build** can see whether a member exists outside its catalogue, so `RS0016` and
  `PARQAPI002` enforce *"nothing exists that is not catalogued"*. A newly widened member or a new
  public attribute member fails `dotnet build`, in the IDE, before a test is ever run.
- **CI** can see the diff against the pull request's base, which is the only place *"new"* is
  definable, so `scripts/CheckApiLedger.cs` enforces *"nothing is catalogued without a ledger
  entry"* for `src/api/seams.txt` and every `PublicAPI.Unshipped.txt`.

Neither half is redundant and neither can do the other's job.

## The emitted surface: shape budgets and review diffs

`PARQAPI001` used to apply catalogue comparison to the emitted surface: an analyzer compared each
checked-in golden `.g.cs` with its checked-in `.api.txt`, and `CheckApiLedger.cs` demanded a ledger
entry whenever an `.api.txt` changed. Both halves depended on those files being checked in. When
the golden output became a derived CI artifact (`artifacts/golden`), there was nothing left for
an analyzer to compare, so `PARQAPI001` was retired.

To prevent emitted surface bloat from regressing silently (#459, #588), two complementary controls govern the emitted surface:

1. **The Shape Budget Gate (`src/api/emitted-api-budgets.txt`)**:
   Enforced by `scripts/DerivedOutputs.cs` and unit-tested by `GeneratedApiBudgetGateTests`. Emitted code
   generation must satisfy hard ceilings on public `MEMBERS` and `PARAMETERS` per model category:
   - `flat` and `sorted` models: at most **40** members, at most **36** parameter slots.
   - `compound` models: at most **20** members, at most **20** parameter slots.
   - `legacy` models: at most **10** members, at most **15** parameter slots.
   Raising any budget ceiling in `src/api/emitted-api-budgets.txt` is governed by `CheckApiLedger.cs`
   and requires a corresponding `docs/api/LEDGER.md` entry (`surface: generated-shape`).
   The gate fails closed if 0 models are examined (positive control requirement).

2. **The Review Diff**:
   The sticky derived-output comment posted by CI contains the base-to-head diff of every golden model's
   `.api.txt` and `.api.shape.txt`. Reviewers check:
   - If empty, no emitted signature changed.
   - If non-empty, every line change must match the pull request's stated intent.

## Public Stability Classifications (#482)

Every public package type and generated capability carries an explicit stability classification.
There is **no preview tier** for 0.1:

| Component | Scope | Stability | Description |
|:---|:---|:---:|:---|
| `[ParquetSerializable]` | Package attribute | **Stable Core** | Marks partial class/record/struct for source generation. |
| `[ParquetColumn]` | Package attribute | **Stable Core** | Configures column name, order, and encoding hints. |
| `[ParquetSortKey]` | Package attribute | **Stable Core** | Marks sorted columns for footer statistics zone mapping. |
| `ParquetSerializerOptions` | Package class | **Stable Core** | Configures compression, row group sizing, and concurrency. |
| `ParquetColumnStatistics<T>` | Package struct | **Stable Core** | Row-group zone map filter statistics; constructor hidden with `[EditorBrowsable(Never)]`. |
| Modern POCO Reads/Writes | Generated extensions | **Stable Core** | `WriteParquetAsync`, `ReadParquetAsync` (`T[]`, `List<T>`, `IAsyncEnumerable<T>`). |
| Modern Fluent Reader & Pruning | Generated reader | **Stable Core** | `<Model>Parquet.From(...).Where(...).ToArrayAsync()`. |
| Columnar Batch Reads | Generated reader | **Stable Core** | `<Model>ParquetReader.Batches()` / `<Model>Batch` (modern backend only). |
| Apache Arrow Ingestion | Generated extensions | **Optional Integration** | `WriteParquetRowGroupAsync(writer, batch)` (modern backend only, conditional on `Apache.Arrow` reference). |
| Legacy V5 Backend Reads/Writes | Legacy package | **Stable Classic Core** | `WriteParquetAsync` and `ReadParquetAsync` targeting classic Parquet.Net 4.x/5.x. |

### Apache Arrow Ingestion (#589, #482)

Arrow ingestion is classified as **Optional Integration** (stable for 0.1, modern backend only).
It emits conditionally when the consumer references `Apache.Arrow` and takes `ParquetWriter` by design
because Arrow batches correspond to individual Parquet row groups in streaming pipelines where the
caller manages the writer lifecycle and compression settings. Arrow export (Parquet to Arrow) is not
shipped and remains independent.

## The buckets

Every ledger entry carries exactly one.

| Bucket | Means |
|:---|:---|
| `additive-minor` | A new member that breaks no existing caller. `0.1.0` → `0.2.0` after 1.0. |
| `breaking-major` | A removal, a rename, or a change to a parameter or return type. |
| `internal` | A seam. Invisible to consumers; recorded because cross-component coupling is a design decision. |
| `generated-shape` | The emitted surface changed shape for every model at once — a new emitted member class, a naming-grammar change, an emitted budget increase. Distinct from `additive-minor` because it multiplies across every `[ParquetSerializable]` type in every consumer. |

### Pre-1.0 stance

Flat-only configuration is supported via the MSBuild property `<ParquetGeneratorFlatOnly>true</ParquetGeneratorFlatOnly>`.

`0.0.x` permits breaking changes without a major bump; the pre-1.0 release-cadence
policy says so, and that is not changing here. The bucket
on an entry therefore does not gate a release today. **It is recorded anyway, because the point is
that the decision was made** — that someone looked at a `breaking-major` label and shipped it
knowingly rather than discovering it from a consumer's bug report. At `0.1.0` the ledger becomes the
freeze record, and from that point the bucket does gate.

## `docs/api/LEDGER.md`

Newest first, one entry per added or changed signature:

```markdown
### 2026-09-11 — `ReadParquetBatchesAsync(Stream, ParquetSerializerOptions?, int, CancellationToken)`
- **Surface:** emitted
- **Semver:** additive-minor
- **Issue:** #147
- **Rationale:** SoA batch shape; no existing member can express a zero-POCO read.
- **Alternatives considered:** overload of `ReadParquetStreamAsync` — rejected, "Stream" already
  denotes shape on that name (#216 defect 1).
```

**Rationale** answers *why no existing member can express this*, not *what the member does*.
**Alternatives considered** is the field that makes the entry worth reading a year later; an entry
without it records a conclusion and loses the reasoning.

### The retrospective seed, stated plainly

The ledger opens with a single entry dated **2026-09-10**, `0.0.x inherited surface`, covering all
**159** emitted public members and all **3** internal seams that existed when this contract landed.

**Everything dated before 2026-09-10 was catalogued retrospectively and was not reviewed under this
contract.** No per-member rationale exists for those members and none should be inferred. Writing
159 retroactive rationales would have manufactured 159 fabrications and made the ledger *less*
trustworthy, not more — the reviews those members actually received did not ask the questions this
contract asks.

This is what makes the [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230)
freeze diff meaningful: the set of members that were genuinely reviewed under the contract is
exactly the set of entries dated on or after 2026-09-10.

## The escape hatch

A ledger entry marked `**Unapproved-by-design:**` suppresses `PARQAPI002` for the signature it
names. (It also suppressed `PARQAPI001` while that gate existed.)

```markdown
### 2026-09-12 — `TryReadColumnSlice(int, int)`
- **Surface:** seam
- **Unapproved-by-design:** spike for #222; measuring whether a slice read beats a row-group read
  before deciding whether this member should exist at all.
```

**Why it exists.** A strict gate with no relief is a gate that gets deleted. An experiment branch
that must refresh a catalogue and write a rationale — for a member the author is trying to find out
whether they want — is an experiment that does not get run, and the cost lands on exactly the
exploratory work this project depends on. The hatch is therefore a designed part of the contract,
not a hole in it.

**Why it cannot leak.** `scripts/CheckApiLedger.cs` fails whenever the marker is present, and CI
runs on `main` and on every pull request targeting `main`. A branch carrying one compiles locally
and cannot merge. The failure message tells the author the two ways out: promote the entry to a real
bucket plus rationale and catalogue the member, or revert the member and drop the entry.

**Matching.** A backtick-quoted token in the entry's heading or on the marker line exempts a
signature when it equals the catalogue line verbatim, equals the member's simple name, or begins
with the simple name followed by `(`. Nothing looser — a substring match would let one hatch entry
silence unrelated members.

## What counts as a seam

`PARQAPI002` reports a **member** — method, constructor, accessor, field, event — whose own declared
accessibility is `internal` or `protected internal`. That spelling is the deliberate act of widening
something past `private` so a different component can reach it, and that is the decision worth a
name.

Excluded, deliberately:

- **Type declarations.** An `internal` type is this repository's ordinary unit of composition —
  every emitter component is one — and is already unreachable outside the assembly. Cataloguing all
  twenty-odd of them would turn `seams.txt` into a mirror of the file listing. Three entries are
  three real decisions; twenty-three would hide them.
- **Compiler polyfills.** `IsExternalInit`, anything else in `System.Runtime.CompilerServices`, and
  anything carrying `[CompilerGenerated]` or `[GeneratedCode]`. These exist so a language feature
  compiles on `netstandard2.0`. They are excluded **by rule rather than listed once**, so that the
  next polyfill needs no catalogue edit — and note this repository carries two copies of
  `IsExternalInit`, one per generator assembly, which listing would have turned into recurring
  meaningless churn.
- **Accessors that merely inherit their member's accessibility.** `internal int X { get; set; }` is
  one decision and produces one violation, not three. An `internal set` on a `public` property *is*
  its own decision and is reported.

**Known limit, stated rather than hidden.** A member spelled `public` inside one of the generator
assemblies reaches no further than an `internal` one does — those assemblies ship only as analyzers
and are never referenced by a consumer — and the gate does not catch it. Closing that would mean
cataloguing every `public` member of every `internal` helper type, which is the churn problem above.
The `Attributes` assembly, where `public` genuinely means public, is guarded by `RS0016`. Since
#461 every type in both generator assemblies is declared `internal`, including the `[Generator]`
entry points (Roslyn instantiates them by reflection and does not need them public), so their
remaining `public` member spellings are effectively `internal` and neither assembly carries
`PublicAPI.*.txt` files any longer.

## The author process

Four steps.

1. **Make the change.** If it widens a member in `src/`, build once and paste the signature
   `PARQAPI002` prints into `src/api/seams.txt` — the message contains the exact line, so there is
   nothing to transcribe. If it touches the emitted surface there is nothing to refresh; the
   emitted API is derived in CI.
2. **Read the API diffs.** `git diff -- src/api/seams.txt '**/PublicAPI.Unshipped.txt'` is the
   review-sized statement of what your change does to the catalogued surfaces, and the "Emitted
   public API" section of the derived-output comment is the same statement for the emitted surface
   (run `dotnet run scripts/DerivedOutputs.cs` on both commits to see it before pushing). If both
   are empty, you changed an implementation, and steps 3 and 4 do not apply.
3. **Write the ledger entry**, newest first, one per added or changed signature. Pick the bucket.
   Fill in *Alternatives considered* — that is the field with a reader in a year's time.
   CI checks the shape of every entry you add: a non-empty **Surface:**, a **Semver:** that starts
   with one of the four buckets, an **Issue:** and a **Rationale:** (#426).  One entry may cover
   several catalogue lines.
4. **Verify.**
   ```bash
   dotnet build Parquet.SourceGenerator.slnx --configuration Release -warnaserror
   dotnet run scripts/CheckApiLedger.cs -- --base main
   ```

## Mechanism, and why

The gates this contract added were **Roslyn analyzers fed their catalogues as `AdditionalFiles`**,
referenced by path with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"
PrivateAssets="all"`. They live in `tools/Parquet.SourceGenerator.ApiGates`; `PARQAPI002` is the one
that remains.

An analyzer was chosen over an MSBuild target for three reasons:

1. **It is the mechanism already in the repository.** Surface 2 is guarded by
   `Microsoft.CodeAnalysis.PublicApiAnalyzers` reading `PublicAPI.Shipped.txt` as an
   `AdditionalFile`. Making surfaces 1 and 3 work the same way meant one mental model, one failure
   shape, and one place a contributor learns to look.
2. **The rules need to parse C#.** `PARQAPI001` re-derived the signature grammar from the golden
   source; `PARQAPI002` needs symbols to render a fully-qualified, canonically-ordered signature. An
   MSBuild target could only shell out to a program that does that — which is a second executable,
   a second build-ordering problem, and a second copy of the renderer.
3. **`PARQAPI002` reports at the declaration.** The error lands on the member's own line in the
   editor, with the catalogue line ready to copy. An MSBuild target can only report against a
   project.

### One renderer, not two

`GeneratedApiBaseline.cs` was compiled into **both** the `PARQAPI001` analyzer and the test
assembly, so the build gate and the test gate could not disagree about what a signature looks like.
It moved from `test/Parquet.SourceGenerator.Tests/` to `tools/Parquet.SourceGenerator.ApiGates/`
for that reason, and the test project still compiles it by link — it is the renderer that produces
every derived `.api.txt`.

### Why it can never run in a consumer's compilation

A consumer's own code appears in none of this repository's catalogues, so a gate running in their
build would fail every compilation. Three independent guards, any one of which suffices:

1. `tools/Parquet.SourceGenerator.ApiGates` is `IsPackable=false`.
2. Both generator packages build their nupkg payload from `$(TargetPath)` alone (see the
   `PackBuildOutputs` target in each `.csproj`), so no transitive analyzer is packed; the project
   references are additionally `PrivateAssets="all"`.
3. The rule short-circuits unless the compilation was handed its catalogue as an
   `AdditionalFile`: no `seams.txt`, no `PARQAPI002`.

CI proves it rather than asserting it: `PackageConsumption` and `PackageConsumptionLegacy` restore
the built `.nupkg` files from a local feed and compile against them on four target frameworks.

## Reference

| Thing | Where |
|:---|:---|
| Grammar for every catalogue line | [The Catalogue Signature Grammar](#the-catalogue-signature-grammar) |
| Emitted API review diff | `derived` job in `.github/workflows/ci.yml`; `scripts/DerivedOutputs.cs`, `scripts/DerivedReport/` |
| The ledger | [`docs/api/LEDGER.md`](../api/LEDGER.md) |
| Seam catalogue | [`src/api/seams.txt`](../../src/api/seams.txt) |
| Analyzer (`PARQAPI002`) | `tools/Parquet.SourceGenerator.ApiGates/` |
| CI check | `scripts/CheckApiLedger.cs` |
| `0.1.0` freeze | [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230) |
| Surface audit and naming grammar | [#216](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/216) |
