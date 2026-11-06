# 17 - Generated Public API Baselines

## Why this file format exists

`Parquet.SourceGenerator` has two public surfaces, and only one of them was reviewable.

The first is the **shipped** surface — the attributes, options and helpers inside
`Parquet.SourceGenerator` and `Parquet.SourceGenerator.Attributes`. That one is guarded by
`Microsoft.CodeAnalysis.PublicApiAnalyzers`: every public member is listed in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, and adding one without updating the file is a
build error.

The second is the **generated** surface — everything the emitters write into a consumer's own
compilation. It is the surface consumers actually call, and it was invisible to review:

- It exists in no shipped assembly, so no .NET API documentation or API-diff tool can see it.
- The golden files under `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.g.cs` contain the full
  emitted body. A new public overload and a retuned buffer loop produce diffs of the same visual
  shape, so a reviewer cannot tell an API change from an implementation change by looking.

The `.api.txt` baselines close that gap. Every golden `Name.g.cs` has a companion `Name.api.txt`
holding **only** the signatures of the public members that file emits: no bodies, no comments, no
`#nullable` scaffolding beyond the header.

> Introduced by issue #215. #216 (the API surface audit) reads these files rather than
> hand-transcribing signatures; #217 uses them to demonstrate the surface *shrinking*; #227 extends
> them into a per-profile matrix; #229 renders the docs-site API grid from them.

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

Both follow from the baseline being derived from **syntax** rather than from symbols: at the point
the golden files are produced, the emitted source has not been compiled against the consumer's
references, so no semantic model exists.

- **The `!` non-null reference marker is not synthesized.** Whether a type name denotes a reference
  type is a semantic fact. Nullable annotations the emitter actually wrote (`string?`,
  `System.Guid?`, `System.ReadOnlyMemory<char>?`) are preserved verbatim, so a reference type
  becoming nullable still shows up as a diff.
- **`global::` qualifiers are stripped.** They are a collision-proofing device of the emitter, not
  part of the API, and stripping them keeps the lines readable and shaped exactly like the shipped
  baselines.

## How the files are produced

`GoldenCodeGenRegressionTests.AssertGoldenMatch` writes the golden `.g.cs` and the `.api.txt`
**from the same emitted string, in the same call**. The two cannot drift, because there is no code
path that produces one without the other. The renderer lives in
`tools/Parquet.SourceGenerator.ApiGates/GeneratedApiBaseline.cs` (compiled into both the test
assembly and the `PARQAPI001` analyzer) and parses the emitted source with
Roslyn (`CSharpSyntaxTree.ParseText`), walking declaration syntax — never regex over text, never
reflection, never symbol enumeration order.

Both emitters are covered: `Parquet.SourceGenerator` (Parquet.Net v6) and
`Parquet.SourceGenerator.Legacy` (V5) each have golden models, and each golden file has a baseline.

### Determinism

Ordering is by `StringComparer.Ordinal`. This is not incidental: a culture-sensitive sort reorders
identifiers differently on different machines and locales, and this repository has already paid for
that once (the MA0002 fix in `tools/BenchmarkSummaryGenerator`). `GeneratedApiBaselineTests`
asserts that rendering under `tr-TR` and under the invariant culture produces byte-identical
output.

### Refreshing

Exactly the same mechanism as the golden files:

```bash
UPDATE_GOLDEN_FILES=true dotnet test test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj \
  --configuration Release --filter "FullyQualifiedName~GoldenCodeGenRegressionTests"
```

Or comment `/update-golden` on a pull request, which dispatches
`.github/workflows/update-golden-files.yml`. That workflow stages the whole `GoldenFiles/`
directory, so baselines are committed alongside the `.g.cs` files without further plumbing.

### The gate

Running the golden suite *without* `UPDATE_GOLDEN_FILES` fails when the emitted surface no longer
matches the checked-in baseline, and the failure names the file and the members rather than dumping
two multi-thousand-character strings:

```
Generated public API baseline drifted: GoldenFiles/OrderEventParquetExtensions.api.txt
The emitted public surface no longer matches the checked-in baseline. If the change is intended,
refresh it with UPDATE_GOLDEN_FILES=true (or comment /update-golden on the PR) and commit the result.
  - removed: SampleDomain.Models.OrderEventColumnarBatch.RowCount -> long
  + added:   SampleDomain.Models.OrderEventColumnarBatch.RowCount -> int
  1 removed, 1 added, 55 public members emitted in total.
```

Because the ordinary CI test run executes this suite, an unreviewed API change cannot land. Since
[18](./18-API-CHANGE-CONTRACT.md), the same comparison also runs at **build** time as `PARQAPI001`,
from the same renderer — `GeneratedApiBaseline.cs` is compiled into both this test assembly and the
analyzer in `tools/Parquet.SourceGenerator.ApiGates`, so the build gate and the test gate cannot
disagree about what a signature looks like. The build error covers additions; this test still
covers removals and ordering.

## What the baselines currently say

| Golden model | Emitter | Public members | Emitted ELOC | ELOC per member |
|:---|:---|---:|---:|---:|
| `OrderEventParquetExtensions` | v6 | 82 | 890 | 10.9 |
| `ScalarMetricParquetExtensions` | v6 | 80 | 806 | 10.1 |
| `NestedOrderParquetExtensions` | v6 | 47 | 1,075 | 22.9 |
| `ListOrderParquetExtensions` | v6 | 47 | 1,543 | 32.8 |
| `LegacyRecordParquetLegacyExtensions` | V5 (legacy) | 7 | 115 | 16.4 |
| **Total** | | **263** | **4,429** | |

A bare nine-property `OrderEvent` model emits 82 public members from a single
`[ParquetSerializable]` attribute. That number is the point of this document: it was not previously
visible anywhere, and #216 and #217 exist to bring it down. It was **159** across all five models
when this page was written and is **263** today, which is the growth those issues are about.

The last two columns come from the `*.metrics.txt` companion introduced by layer 2 of #251 — see
[22 - Generated Code Metrics](./22-GENERATED-CODE-METRICS.md). Those baselines read their member
count out of the `.api.txt` files above rather than recomputing it, so the two artefacts cannot
disagree about what an emitted member is; `GeneratedCodeMetricsBaselineTests` asserts that.

## Stability contract

[18 - The API Change Contract](./18-API-CHANGE-CONTRACT.md) now enforces this format as a **build
error** (`PARQAPI001`) and requires a `docs/api/LEDGER.md` entry per added line, so the format is
fixed in the two ways that matter:

- **A line is self-contained.** It never depends on another line's presence or position.
- **A line is stable.** It changes only when the member it describes changes.

Anything that would violate either — omitting the containing type, folding overloads together,
sorting by anything other than ordinal — is out of bounds.
