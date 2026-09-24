# 23 — Duplication Measurement (Layer 3 of #251)

Layers 1 ([21 — Code Metrics](./21-CODE-METRICS.md)) and 2 ([22 — Generated Code
Metrics](./22-GENERATED-CODE-METRICS.md)) measure *volume and branching*. Neither can see the
thing that has actually cost this project time: **the same code written twice**. This page
documents layer 3 — `scripts/Duplication.cs` and the report it writes to
`artifacts/metrics/duplication.txt`.

## Why this repo, specifically

The demonstrated failure mode is duplication, not size:

- **Three emitted read paths each hand-rolled `ResolveSchemaField`**, and all three broke when
  #196 added a parameter to it. A **fourth** copy surfaced during the #245 merge. Four copies of
  one idea, discovered one at a time, each by a build failure.
- `CodeEmitter.cs` is ~2,536 SLOC across 39 methods. Layer 1 says those methods are individually
  well-behaved; it cannot say whether they share structure. Layer 3 can.
- #264 is the next case: two pruning components, two reads of the same footer statistics, two
  mental models of one mechanism — knowable by reading the code, but only *counted* by this
  measurement.

## What is measured

For every method and constructor under `src/`, a stream of normalized token units:

| token | unit |
|:--|:--|
| keyword | its syntax kind — structure is preserved |
| literal (string, number, …) | its **text** — same strings and numbers are the fingerprint of real duplication |
| identifier, operator, punctuation | its syntax kind only — **renames do not un-duplicate code** |

A sliding window of **16 units** is FNV-hashed; every window hash shared by two methods seeds a
diagonal alignment between them; a maximal consecutive run along a diagonal is one duplicated
span, reported if it covers at least **40 tokens**. The knobs are calibrated, not picked (below).

The report is `artifacts/metrics/duplication.txt` (gitignored), in the same grammar as the other
metrics reports: a comment header explaining it, ordinal-sorted lines, one command to regenerate:

```
K | span=180 copies=2 | src/.../CodeEmitter.cs:CodeEmitter.EmitReadArrayAsync | src/.../CodeEmitter.cs:CodeEmitter.EmitReadAsync
```

Cluster lines carry **method identities, not line numbers**. A refactor that moves code without
changing the overlap must not show up when two reports are diffed — position is not duplication.

## What is deliberately NOT measured

**Emitted (generated) code.** One `ResolveSchemaField` per model is the design, not a defect;
generated output is *supposed* to repeat itself. The scope is `src/` only, and that is not an
oversight to be "fixed" later: a gate on emitted duplication would be a permanent false positive,
and even report-only would invite the number to be read as a defect count. Layer 2 already
measures emitted code's *size per capability* — the metric that can be argued with.

## Calibration — the test the tool had to pass first

The issue's bar: *a candidate tool that cannot find the copies that already broke the build is
not fit for this repo.* Before adopting anything, the detector was run against the pre-#79
history (`8d4a097`), where `ResolveSchemaField` existed as separate emitted-text copies in
`CodeEmitter.Schema.cs` and `LegacyCodeEmitter.Schema.cs`, and the three read paths were
independent:

```
Duplication: 23 cluster(s), 4378 duplicated token(s), worst span 278 tokens across 2 methods.
K | span=278 | CodeEmitter.Read.cs:EmitReadAsync | CodeEmitter.Read.cs:EmitReadStreamAsync
K | span=209 | CodeEmitter.Parallel.cs:EmitReadParallelAsync | CodeEmitter.Read.cs:EmitReadStreamAsync
K | span=123 | CodeEmitter.Schema.cs:EmitSchema | LegacyCodeEmitter.Schema.cs:EmitSchema
```

It names the exact incident — all three broken read paths, pairwise, plus the v6/Legacy emitter
copy — and on the current tree the same configuration reports **93 clusters / 12,034 duplicated
tokens** (worst pair: `EmitReadArrayAsync` ↔ `EmitReadAsync`, 180 tokens). That number is the
evidence the refactor case rests on; #263 and #264 are its first two debtors.

## In CI

`ci.yml` runs `dotnet run scripts/Duplication.cs -- --summary duplication.md` beside the code
metrics step, appends the summary (totals and the largest clusters) to the job summary, and
uploads the full report in the `code-metrics` artifact. It does not fail the build.

It used to: the report was checked in as `metrics/duplication.txt` and any difference failed CI
with zero tolerance. That was removed for the reasons in
[21 § Why the hand-written numbers are not checked in](./21-CODE-METRICS.md#why-the-hand-written-numbers-are-not-checked-in)
— the file was a pure function of `src/`, and the answer to a red build was always to refresh it.
A new cluster is still a question with a name attached — fold it, or justify carrying it — and the
way to ask it is to diff two reports:

```bash
dotnet run scripts/Duplication.cs                     # artifacts/metrics/duplication.txt
dotnet run scripts/Duplication.cs -- --report -       # to stdout
```

Determinism contract: same Roslyn version pinned as layer 1 (`Microsoft.CodeAnalysis.CSharp
@4.14.0`), ordinal ordering everywhere, and verified byte-stable under `LC_ALL=tr_TR.UTF-8` —
the locale trap that layers 1 and 2 already documented.

## Honest limits

- **Token identity, not semantic identity.** Two blocks that compute the same value with
  different string literals or different control flow read as distinct. This is the same trade
  jscpd makes, and the calibration incident argues it is the right one *for this repo's
  failure*: the copies broke on shared emitted text, which is exactly what literal-preserving
  token windows see.
- **The unit model ignores trivia** (comments). A copy with reworded comments is still caught —
  correct for duplication — but two blocks that differ only in comments are one cluster.
- **Pairwise clusters.** A 3-way copy reports as three pairs, which is arguably the truthful
  representation and matches how `ResolveSchemaField` actually behaved. `total duplicated tokens`
  therefore counts a shared block once per pair.
- **Windows, not maximal blocks.** A duplicated run is reported with span
  `runLength + 15` — exact at the boundaries the window granularity permits; treat spans as
  scale, not as cut lines.

Refs: #251 (epic), #260 (this layer), #253/#256 (layers 1–2), #252 (structure), #264 (the next
convergence the number justifies).
