# 17 - Generated Public API Baselines

## Why this file format exists

`Parquet.SourceGenerator` has two public surfaces, and only one of them was reviewable.

The first is the **shipped** surface — the attributes, options and helpers inside
`Parquet.SourceGenerator.Attributes` (the generator assembly itself has had no public types since
#461: it ships only as an analyzer). That one is guarded by
`Microsoft.CodeAnalysis.PublicApiAnalyzers`: every public member is listed in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, and adding one without updating the file is a
build error.

The second is the **generated** surface — everything the emitters write into a consumer's own
compilation. It is the surface consumers actually call, and it was invisible to review:

- It exists in no shipped assembly, so no .NET API documentation or API-diff tool can see it.
- The emitted source of a golden model contains the full body. A new public overload and a
  retuned buffer loop produce diffs of the same visual shape, so a reviewer cannot tell an API
  change from an implementation change by looking.

The `.api.txt` rendering closes that gap. Every golden model's `Name.g.cs` has a companion
`Name.api.txt` holding **only** the signatures of the public members that file emits: no bodies, no
comments, no `#nullable` scaffolding beyond the header.

> Introduced by issue #215. #216 (the API surface audit) reads these files rather than
> hand-transcribing signatures; #217 uses them to demonstrate the surface *shrinking*; #227's
> profile matrix is a post-freeze follow-up recorded in [document 38](38-FEATURE-PROFILE-MATRIX-SCOPE-227.md).
> #229 rendered the docs-site API grid from the checked-in contracts on `main`. Those files are no
> longer checked in (see [below](#why-none-of-it-is-checked-in)); a version's output now lives on its
> release instead (see [Per-release output](#per-release-output)), which also pins the grid to what
> was actually shipped rather than to whatever `main` held.

## Per-release output

`release.yml` runs `scripts/DerivedOutputs.cs` on the commit it publishes and attaches the result
to the GitHub release as `derived-outputs.tar.gz`:

```
manifest.json    {"version": "...", "tag": "v...", "commit": "<sha>"}
golden/          *.g.cs, *.api.txt, *.api.shape.txt for every golden model
metrics/         src/ metrics, generated/ metrics, duplication.txt
callgraph/       edges and Mermaid pages
```

The tag is the address: `https://github.com/rtkelly13/Parquet.SourceGenerator/releases/download/<tag>/derived-outputs.tar.gz`.
After a full release, `release.yml` calls `docs-dispatch.yml` with that tag, and the `docs_update`
dispatch carries it as `client_payload.tag`. An empty tag (a docs-only change on `main`) means
"keep the API grid on the last tag". Prereleases publish to NuGet.org only and have no GitHub
release, so they carry no asset. Releases before this change (`v0.0.1`–`v0.0.3`) predate the
`.api.txt` files entirely.

## The grammar

The grammar is deliberately the one `PublicAPI.Shipped.txt` already uses in this repository, so a
reader learns one grammar and applies it to both surfaces.

| Construct | Line |
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

Rules that hold for every line:

1. **`->` introduces the return type.** `void` is written out rather than omitted.
2. **Every line carries its fully-qualified containing type.** One generated file routinely holds
   several types — the extensions class, a nested `ColumnBatch`, a `{T}RowGroupMetadata` struct, a
   `{T}ColumnarBatch` struct, and the conditional Arrow bridge partial. A line is therefore
   self-contained: adding or removing an unrelated member never rewrites it.
3. **Parameters carry type, name and default value**, in source order:
   `(System.IO.Stream stream, ParquetSerializerOptions? options = null)`. Parameter modifiers
   (`this`, `ref`, `out`, `in`, `params`, `scoped`) are kept — they are part of the contract.
4. **API-relevant modifiers are prefixed in one fixed canonical order**:
   `const static readonly required abstract virtual override sealed`. The order in the file is
   this order, not the order the emitter happened to spell them, so a cosmetic reordering in the
   emitter produces no diff. Accessibility keywords, `partial`, `async`, `unsafe`, `extern`, `new`
   and `volatile` are implementation detail and are dropped — `async` in particular is not part of
   any caller's contract.
5. **Only externally-reachable declarations are listed.** A member appears when it is `public`,
   `protected` or `protected internal` *and* every type containing it is likewise. The emitted
   `private struct StringDeduplicator` has `public` members; none of them are public API and none
   of them appear.
6. **One member per line, ordinal-sorted, duplicates collapsed.** A type split across two `partial`
   declarations in one file contributes one entry.

### Two deliberate deviations from `PublicAPI.txt`

Both follow from the rendering being derived from **syntax** rather than from symbols: at the point
the golden models are rendered, the emitted source has not been compiled against the consumer's
references, so no semantic model exists.

- **The `!` non-null reference marker is not synthesized.** Whether a type name denotes a reference
  type is a semantic fact. Nullable annotations the emitter actually wrote (`string?`,
  `System.Guid?`, `System.ReadOnlyMemory<char>?`) are preserved verbatim, so a reference type
  becoming nullable still shows up as a diff.
- **`global::` qualifiers are stripped.** They are a collision-proofing device of the emitter, not
  part of the API, and stripping them keeps the lines readable and shaped exactly like the shipped
  baselines.

## How the files are produced

The golden models are declared in code, in `test/Parquet.SourceGenerator.Tests/GoldenCorpus.cs`:
seven of them — `OrderEvent`, `ScalarMetric`, `LegacyRecord`, `NestedOrder`, `ListOrder`,
`PocoOrder` and `SortedShipment`. The consumer-side declarations they extend are hand-written
inputs, checked in under `test/Parquet.SourceGenerator.Tests/GoldenModels/` and `Compile`-removed
from the test assembly.

`GoldenCorpus.Publish` writes each model's `.g.cs`, its `.api.txt` and its `.api.shape.txt`
([19](./19-PUBLIC-API-SURFACE.md)) **from the same emitted string, in the same call**. The three
cannot drift, because there is no code path that produces one without the others. They go to
`artifacts/golden/` (gitignored; `GOLDEN_OUTPUT_DIR` overrides it). The renderer lives in
`tools/Parquet.SourceGenerator.ApiGates/GeneratedApiBaseline.cs`, is linked into the test assembly,
and parses the emitted source with Roslyn (`CSharpSyntaxTree.ParseText`), walking declaration
syntax — never regex over text, never reflection, never symbol enumeration order.

Both emitters are covered: `Parquet.SourceGenerator` (Parquet.Net v6) and
`Parquet.SourceGenerator.Legacy` (V5) each have golden models, and each model is rendered.

To produce them locally:

```bash
dotnet test test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj \
  --configuration Release --filter "FullyQualifiedName~GoldenCodeGenRegressionTests"
# or everything derived — golden models, metrics, duplication, call graph — into artifacts/:
dotnet run scripts/DerivedOutputs.cs
```

### Determinism

Ordering is by `StringComparer.Ordinal`. This is not incidental: a culture-sensitive sort reorders
identifiers differently on different machines and locales, and this repository has already paid for
that once (the MA0002 fix in `tools/BenchmarkSummaryGenerator`). `GeneratedApiBaselineTests`
asserts that rendering under `tr-TR` and under the invariant culture produces byte-identical
output. It matters more now than when the files were checked in: the review diff compares two
renderings made on two runs, and any nondeterminism would show up in every pull request as a
change nobody made.

### What the golden suite asserts

`GoldenCodeGenRegressionTests` asserts invariants, not equality with a stored copy:

- every model's emitted source parses with no errors;
- the driver-generated models compile, in memory against Parquet.Net, with zero errors;
- each model's specific surface claims hold (`ShouldContain` / `ShouldNotContain` — for example,
  `SortedShipment` emits `TryPruneSortedRowGroups<` and no `ReadParquetByCarrierAsync(`);
- every model emits a non-empty public API.

`BackendCompatibilityPolicyTests` ([14](./14-COMPATIBILITY-MATRIX.md)) applies the classic/modern
policy to the same models' API, rendered in memory by `GeneratedApiBaseline.Create`.

A change to what the emitters write therefore fails the suite only when it breaks one of those
claims. Every other change is shown, not gated.

## Why none of it is checked in

Until this change the `.g.cs`, `.api.txt`, `.api.shape.txt` and `.metrics.txt` files lived in
`test/Parquet.SourceGenerator.Tests/GoldenFiles/`, the suite failed on any difference from them,
and `UPDATE_GOLDEN_FILES=true` (or the `/update-golden` pull-request comment) rewrote and committed
them. That arrangement is retired, for three reasons:

1. **The code is the source of truth.** A checked-in rendering of the emitter is a second copy of a
   fact the emitter already states. The only thing the copy could say that the code does not is
   "this is what the output was last time" — and git already knows what the code was last time.
2. **Refresh commits polluted history and diffs.** Every emitter change carried a mechanical
   refresh commit of hundreds or thousands of lines, by hand or from the `/update-golden`
   workflow. Those commits swamped the change that caused them and made `git log -p` and blame on
   the test directory hard to use.
3. **The review value survives without the files.** What the `.api.txt` convention was *for* is a
   reviewer seeing an API change as an API change. That needs a base-to-head diff of the rendering,
   not a stored rendering, and CI now produces exactly that.

## The review diff

The `derived` job in `.github/workflows/ci.yml` runs beside `test`; the required `build` check
passes only when both succeed, so the gates below block a merge:

1. `scripts/DerivedOutputs.cs` produces every derived output for the head — `golden/` (this page),
   `metrics/` ([21](./21-CODE-METRICS.md), [22](./22-GENERATED-CODE-METRICS.md),
   [23](./23-DUPLICATION.md)) and `callgraph/` ([25](./25-CALL-GRAPH.md)). The gates that remain run
   here, so a failing gate fails the job. On a push to main the tree is also uploaded as
   `derived-baseline-<sha>` (kept 90 days). Every main commit gets one: main runs have a
   concurrency group per commit and are never cancelled.
2. On a pull request the merge base's tree is downloaded from its `derived-baseline-<sha>`
   artifact, accepted only when it was uploaded from this repository by a push run of `ci.yml` on
   `main` at exactly that commit (the name alone proves nothing: any run can upload one).
   Only when there is none — the base is another branch of a stack, its main run has not finished, or the artifact expired — is it regenerated here in a `git worktree`. Outputs are
   byte-identical across runs and between Linux and macOS, so the two are interchangeable; the
   comment's footer says which was used. A base commit that predates this change has no
   `GoldenCorpus`; for it the checked-in files *were* its derived output, so they are copied into
   the same layout rather than regenerated. Nothing about the base can fail the job: if it cannot
   be produced, the comment says so and the head's gates alone decide the check.
3. `scripts/DerivedReport/` renders the review. It is a file-based app spread over one folder:
   `dotnet run scripts/DerivedReport/DerivedReport.cs` compiles the C# and the Razor components
   beside it, and renders the HTML pages with the official `HtmlRenderer`
   (`Microsoft.AspNetCore.Components.Web`) — no web host, no ASP.NET shared framework, no project
   file. It has two views:
   - **`diff`** — the sticky comment, a deterministic **summary of the whole PR**: files and lines
     by area, the API catalogues and changelog, test methods added and removed, CI and tooling
     touched (`.github/protected-paths.txt`), all read from git between the merge base and the head
     commit; then what drifted in the derived outputs, read from the two trees' own reports —
     public members and signatures per golden model and the public types added or removed,
     generated-code and `src/` metrics that moved, duplication totals, call-graph nodes and edges.
     No diff text. Everything above the footer is a function of the two commits, so the same pair
     always renders the same bytes; only the footer's links name the run. It also writes the full
     `review.patch` and the **full diff as HTML**: every changed file in the section order
     **Emitted public API** (expanded) → Emitted code → Generated-code metrics → Duplication →
     Hand-written code metrics (src/) → Call graph → Other, with the summary tables on top.
   - **`state`** — one tree as it is: the current numbers and every file in full. A PR gets it for
     its head; main's weekly and release snapshots use it too.
4. Both pages are self-contained (styles and script inlined from `report.css` and `report.js`,
   nothing fetched when they open) and each is uploaded unzipped as its own artifact
   (`derived-diff.html`, `derived-state.html`, `archive: false`), so the comment's footer links
   download the pages themselves. Both trees, the full `review.patch` and the rendered
   `review-diff.md` are uploaded as the `derived-outputs` artifact. PR artifacts expire with the
   repository's retention.
5. The job creates, or edits in place, **one** sticky comment on the pull request, marked
   `<!-- derived-review-diff -->`. It is updated on every run, including failed ones: a head that
   failed its gates, or a base that could not be produced, replaces the previous diff with a
   statement saying so, so a stale diff never stands as current. Only a run for the PR's current
   head posts: an older run re-run after a newer push leaves the comment alone. A fork's token
   cannot write comments; that step is `continue-on-error`, and the result is still in the step
   summary and the artifact.

The "Emitted public API" table in the comment says which models' surfaces moved and which public
types came and went; the same section of the full diff page is the line-level review surface for
the emitted consumer API, and reads exactly as the old baseline diff did:

```diff
-SampleDomain.Models.OrderEventColumnarBatch.RowCount -> long
+SampleDomain.Models.OrderEventColumnarBatch.RowCount -> int
```

An empty section on a pull request that only retunes a loop is the evidence that it changed no
signature. [18](./18-API-CHANGE-CONTRACT.md) states what the reviewer is expected to do with it.

## Historical snapshot: what the baselines said when this page was written

The table below was read from the checked-in baselines while they existed. It is not maintained;
current numbers are in the `derived-outputs` artifact and the generated-code metrics report
([22](./22-GENERATED-CODE-METRICS.md)).

| Golden model | Emitter | Public members | Emitted ELOC | ELOC per member |
|:---|:---|---:|---:|---:|
| `OrderEventParquetExtensions` | v6 | 82 | 890 | 10.9 |
| `ScalarMetricParquetExtensions` | v6 | 80 | 806 | 10.1 |
| `NestedOrderParquetExtensions` | v6 | 47 | 1,075 | 22.9 |
| `ListOrderParquetExtensions` | v6 | 47 | 1,543 | 32.8 |
| `LegacyRecordParquetLegacyExtensions` | V5 (legacy) | 7 | 115 | 16.4 |
| **Total** | | **263** | **4,429** | |

A bare nine-property `OrderEvent` model emitted 82 public members from a single
`[ParquetSerializable]` attribute. That number was the point of this document: it was not previously
visible anywhere, and #216 and #217 existed to bring it down. It was **159** across all five models
when this page was first written and **263** at the time of the table, which is the growth those
issues were about.

The last two columns come from the generated-code metrics of layer 2 of #251 — see
[22 - Generated Code Metrics](./22-GENERATED-CODE-METRICS.md). That report reads its member count
out of the published `.api.txt` files rather than recomputing it, so the two cannot disagree about
what an emitted member is.

## Stability contract

The format no longer backs a build gate — `PARQAPI001`, which enforced it at build time, is retired
([18](./18-API-CHANGE-CONTRACT.md)) — but it backs the review diff, and a diff is only readable if
the format is fixed in the two ways that matter:

- **A line is self-contained.** It never depends on another line's presence or position.
- **A line is stable.** It changes only when the member it describes changes.

Anything that would violate either — omitting the containing type, folding overloads together,
sorting by anything other than ordinal — is out of bounds.
