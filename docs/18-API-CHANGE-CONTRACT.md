# 18 - The API Change Contract

## The rule

> **Nothing enters a governed API surface without appearing in a catalogue file *and* carrying a
> `docs/api/LEDGER.md` entry that classifies its semver impact.**

An unapproved addition fails the **build**, not merely a test. That is the whole difference between
this document and [17 - Generated Public API Baselines](./17-GENERATED-API-BASELINES.md), which
made the emitted surface *visible*. Visibility is necessary and is not sufficient: a listing that
only a test consults is a listing that grows while nobody is looking.

This exists to serve [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230), the
`0.1.0` API freeze. At the freeze, the ledger *is* the record of what was decided and why. A ledger
that was filled in retrospectively would record nothing, which is why the seeding decision below is
deliberate.

## The three surfaces

| # | Surface | What it is | Catalogue | Gate | Enforced by |
|:--|:---|:---|:---|:---|:---|
| 1 | **Emitted consumer API** | Everything the emitters write into a consumer's own compilation. Exists in no shipped assembly. | `*.api.txt` beside each golden file | `PARQAPI001` | `EmittedApiGateAnalyzer` (build **error**) |
| 2 | **Shipped package API** | `public` members of `Parquet.SourceGenerator.Attributes` and `Parquet.SourceGenerator`. | `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` | `RS0016` | `Microsoft.CodeAnalysis.PublicApiAnalyzers` (pre-existing; unchanged) |
| 3 | **Internal seams** | Members in `src/` widened past `private` so another component can call them. | `src/api/seams.txt` | `PARQAPI002` | `InternalSeamGateAnalyzer` (build **error**) |

All three catalogues use **one grammar** — the one
[17 §The grammar](./17-GENERATED-API-BASELINES.md#the-grammar) documents, which is the grammar
`PublicAPI.Shipped.txt` already used. One signature per line, self-contained, ordinal-sorted,
`->` introducing the return type. Learn it once.

Surface 2 is deliberately left exactly as it was. It already had a build-error gate that works, has
years of Microsoft maintenance behind it, and produces messages contributors already recognise.
Reimplementing it would have been a downgrade dressed as consistency.

## What triggers a gate, and what does not

**This is the point of the whole design.** A golden `.g.cs` file holds the full emitted body, so a
retuned buffer loop and a new public overload produce diffs of the same visual shape. The `.api.txt`
baseline holds only signatures. Therefore:

| Change | Golden `.g.cs` | `.api.txt` | Gate fires? | Ledger entry? |
|:---|:---:|:---:|:---:|:---:|
| Retune a buffer loop, change an allocation strategy | changes | unchanged | no | no |
| Reword an XML doc comment on emitted code | changes | unchanged | no | no |
| Reorder emitted statements, rename a local | changes | unchanged | no | no |
| Add a public method, overload, property or nested type | changes | **gains a line** | **yes** | **yes** |
| Change a parameter type, name, default, or return type | changes | **line changes** | **yes** | **yes** |
| Remove a public member | changes | loses a line | test failure, not a build error (see below) | **yes** |
| Widen a `private` member in `src/` to `internal` | — | `seams.txt` gains a line | **yes** | **yes** |
| Add a `public` member to the `Attributes` package | — | `PublicAPI.Unshipped.txt` gains a line | **yes** (`RS0016`) | **yes** |

Body, perf and comment changes are not API changes, and the contract must not tax them — a gate
that fires on every performance commit is a gate that gets suppressed.

### Division of labour between the build and CI

Each gate enforces the half of the rule it can actually see.

- **The build** can see whether a member exists outside its catalogue, so `PARQAPI001` and
  `PARQAPI002` enforce *"nothing exists that is not catalogued"*. A hand-edited golden file, a
  half-applied refresh, a newly widened member — all fail `dotnet build`, in the IDE, before a test
  is ever run.
- **CI** can see the diff against the pull request's base, which is the only place *"new"* is
  definable, so `scripts/CheckApiLedger.cs` enforces *"nothing is catalogued without a ledger
  entry"*.

Neither half is redundant and neither can do the other's job. Note in particular that
`UPDATE_GOLDEN_FILES=true` rewrites the `.g.cs` and the `.api.txt` from the same emitted string (by
design — see [17](./17-GENERATED-API-BASELINES.md), it is what makes the two unable to drift), so
after a refresh the build is green again and the CI check is what demands the rationale. Removals
are the one case handled only by the test suite: a removal is not "something entering a surface",
and `GoldenCodeGenRegressionTests` already compares the two files byte for byte and names the
removed member.

## The buckets

Every ledger entry carries exactly one.

| Bucket | Means |
|:---|:---|
| `additive-minor` | A new member that breaks no existing caller. `0.1.0` → `0.2.0` after 1.0. |
| `breaking-major` | A removal, a rename, or a change to a parameter or return type. |
| `internal` | A seam. Invisible to consumers; recorded because cross-component coupling is a design decision. |
| `generated-shape` | The emitted surface changed shape for every model at once — a new emitted member class, a naming-grammar change, a feature profile. Distinct from `additive-minor` because it multiplies across every `[ParquetSerializable]` type in every consumer. |

### Pre-1.0 stance

`0.0.x` permits breaking changes without a major bump; the release-cadence note in
[04 - Roadmap](./04-ROADMAP-AND-CONTRIBUTING.md) says so, and that is not changing here. The bucket
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

A ledger entry marked `**Unapproved-by-design:**` suppresses `PARQAPI001` / `PARQAPI002` for the
signature it names.

```markdown
### 2026-09-12 — `TryReadColumnSlice(int, int)`
- **Surface:** emitted
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
The `Attributes` assembly, where `public` genuinely means public, is guarded by `RS0016`.

## The author process

Four steps.

1. **Make the change.** If it touches the emitted surface, refresh the baselines:
   ```bash
   UPDATE_GOLDEN_FILES=true dotnet test test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj \
     --configuration Release --filter "FullyQualifiedName~GoldenCodeGenRegressionTests"
   ```
   Or comment `/update-golden` on the pull request. If it widens a member in `src/`, build once and
   paste the signature `PARQAPI002` prints into `src/api/seams.txt` — the message contains the exact
   line, so there is nothing to transcribe.
2. **Read the catalogue diff.** `git diff -- '*.api.txt' src/api/seams.txt '**/PublicAPI.Unshipped.txt'`
   is the review-sized statement of what your change does to the API. If it is empty, you changed an
   implementation, and steps 3 and 4 do not apply.
3. **Write the ledger entry**, newest first, one per added or changed signature. Pick the bucket.
   Fill in *Alternatives considered* — that is the field with a reader in a year's time.
4. **Verify.**
   ```bash
   dotnet build Parquet.SourceGenerator.slnx --configuration Release -warnaserror
   dotnet run scripts/CheckApiLedger.cs -- --base main
   ```

## Mechanism, and why

Both new gates are **Roslyn analyzers fed their catalogues as `AdditionalFiles`**, referenced by
path with `OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all"`. They live
in `tools/Parquet.SourceGenerator.ApiGates`.

An analyzer was chosen over an MSBuild target for three reasons:

1. **It is the mechanism already in the repository.** Surface 2 is guarded by
   `Microsoft.CodeAnalysis.PublicApiAnalyzers` reading `PublicAPI.Shipped.txt` as an
   `AdditionalFile`. Making surfaces 1 and 3 work the same way means one mental model, one failure
   shape, and one place a contributor learns to look.
2. **The rules need to parse C#.** `PARQAPI001` re-derives the signature grammar from the golden
   source; `PARQAPI002` needs symbols to render a fully-qualified, canonically-ordered signature. An
   MSBuild target could only shell out to a program that does that — which is a second executable,
   a second build-ordering problem, and a second copy of the renderer.
3. **`PARQAPI002` reports at the declaration.** The error lands on the member's own line in the
   editor, with the catalogue line ready to copy. An MSBuild target can only report against a
   project.

### One renderer, not two

`GeneratedApiBaseline.cs` is compiled into **both** the analyzer and the test assembly. The build
gate and the test gate therefore cannot disagree about what a signature looks like — there is no
second implementation to drift. It moved from
`test/Parquet.SourceGenerator.Tests/` to `tools/Parquet.SourceGenerator.ApiGates/` for this reason,
and the test project compiles it by link.

### Why it can never run in a consumer's compilation

A consumer's own models appear in none of this repository's baselines, so a gate running in their
build would fail every compilation. Three independent guards, any one of which suffices:

1. `tools/Parquet.SourceGenerator.ApiGates` is `IsPackable=false`.
2. Both generator packages build their nupkg payload from `$(TargetPath)` alone (see the
   `PackBuildOutputs` target in each `.csproj`), so no transitive analyzer is packed; the project
   references are additionally `PrivateAssets="all"`.
3. Both rules short-circuit unless the compilation was handed their catalogue as an
   `AdditionalFile`. No `.api.txt`, no `PARQAPI001`; no `seams.txt`, no `PARQAPI002`.

CI proves it rather than asserting it: `PackageConsumption` and `PackageConsumptionLegacy` restore
the built `.nupkg` files from a local feed and compile against them on four target frameworks.

## Reference

| Thing | Where |
|:---|:---|
| Grammar for every catalogue line | [17 - Generated Public API Baselines](./17-GENERATED-API-BASELINES.md) |
| The ledger | [`docs/api/LEDGER.md`](./api/LEDGER.md) |
| Seam catalogue | [`src/api/seams.txt`](../src/api/seams.txt) |
| Analyzers | `tools/Parquet.SourceGenerator.ApiGates/` |
| CI check | `scripts/CheckApiLedger.cs` |
| `0.1.0` freeze | [#230](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/230) |
| Surface audit and naming grammar | [#216](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/216) |
