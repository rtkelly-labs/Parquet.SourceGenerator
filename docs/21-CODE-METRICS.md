# 21 - Code Metrics Baselines & The Complexity Ratchet

> Layer 1 of [#251](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/251). This page
> covers the **hand-written** code under `src/`. Metrics over the *generated* code are layer 2 and
> live in [22 - Generated Code Metrics](./22-GENERATED-CODE-METRICS.md) — same computation, same
> grammar, a different tolerance policy, and a different answer about which metrics carry signal.
> Duplication (layer 3) and mutation testing (layer 4) are separate work.

## Why this exists

Before this, the repository measured its public surface ([17](./17-GENERATED-API-BASELINES.md),
[18](./18-API-CHANGE-CONTRACT.md)), its test coverage, and the correctness of its emitted output —
and measured the *maintainability of the code doing the emitting* not at all. `CodeEmitter.cs` grew
to 2,536 source lines with nothing recording the fact. Four separate hand-rolled copies of
`ResolveSchemaField` accumulated across the emitted read paths and all broke together, because
nothing was counting.

This is the `.api.txt` pattern applied to quality: **emit a deterministic artifact, check it in,
gate on drift.** A reviewer should be able to look at a pull request diff and see the sentence
"this method got more complex" written out in numbers.

## The metrics

All five come from `Microsoft.CodeAnalysis.CodeMetrics.CodeAnalysisMetricData` — the same type the
`CA1502` / `CA1505` / `CA1506` analyzers and Microsoft's own `Metrics.exe` use.

| Key | Metric | What it counts | What it is good for |
|:--|:--|:--|:--|
| `MI` | Maintainability Index | `171 - 5.2·ln(Halstead volume) - 0.23·(cyclomatic complexity) - 16.2·ln(lines of code)`, rescaled to 0-100 | Detecting *change*. See the caveat below. |
| `CC` | Cyclomatic complexity | Independent paths through a method — one per branch, loop, `catch`, `&&`, `??`, pattern arm | The number of cases a reader, and a test suite, has to hold at once |
| `CL` | Class coupling | Distinct named types the entity references | How much of the rest of the system you must understand to change this one |
| `DIT` | Depth of inheritance | Length of the base-type chain | Indirection introduced by hierarchy |
| `SLOC` / `ELOC` | Source / executable lines | Lines in the declaration; lines with executable IL | Size. Not quality, but the thing that makes everything else worse |

### What these numbers do NOT tell you

**The Maintainability Index is a poor absolute judgement of quality.** It is a 1990s regression fit
over Halstead volume — operator and operand *counts* — and it has no idea whether a name is right,
whether an abstraction leaks, or whether a `switch` is a state machine or a typo farm. A long,
perfectly clear method of straight-line `builder.AppendLine(...)` calls scores badly
(`StringDeduplicatorComponent.EmitStringDeduplicator`, MI 28, cyclomatic complexity **2**) and a
dense nest of conditionals can score well. Treat a low MI as a prompt to look, never as a verdict,
and never as a score to optimise. It earns its place here only as a **change detector**.

Cyclomatic complexity is the more honest number of the five, and coupling is the more honest number
still — but none of them measure whether the code is *right*. That is what the conformance, matrix,
fuzz and golden-file suites are for.

## Where the numbers live

`metrics/<Project>.metrics.txt`, checked in, one file per measured project:

```
T:Parquet.SourceGenerator.Emitter.CodeEmitter | MI=44 CC=144 CL=26 DIT=1 SLOC=2536 ELOC=1287
M:Parquet.SourceGenerator.Emitter.CodeEmitter.EmitReadAsync(System.Text.StringBuilder, Parquet.SourceGenerator.Models.TargetClassModel) | MI=30 CC=6 CL=11 DIT=- SLOC=194 ELOC=110
```

Prefixes are `A` assembly, `N` namespace, `T` type, `M` method, `P` property, `E` event. Fields are
omitted: every field scores MI 100 / CC 0, so a line per field would be pure rename churn with no
signal.

### A normalised rendering, not the tool's XML

Microsoft's `Metrics.exe` emits XML. This repository checks in a flat, one-line-per-entity text
rendering instead, for the same reason `*.api.txt` is not a serialized `Compilation`:

- **A line is self-contained and stable.** It carries its own fully-qualified identity, so adding
  or removing an unrelated member never rewrites it. XML nests entities inside their parents, so
  inserting one method reindents and re-diffs its siblings.
- **The diff is the deliverable.** `CC 6 -> 11` on a named method is the whole point of the
  artifact. `<Metric Name="CyclomaticComplexity" Value="11" />` three levels down an element tree
  is not reviewable at a glance.
- **Parameter *names* are excluded** from the identity (types are kept, so overloads stay distinct).
  A parameter rename is not a maintainability change and must not produce a diff.

### Determinism

The same rules as [17](./17-GENERATED-API-BASELINES.md):

- Every ordering is `StringComparer.Ordinal`. Never culture-sensitive — this repository has already
  paid for that once.
- The Roslyn that parses the source and computes the metrics is **pinned in the script's
  `#:package` directives**, not taken from the SDK, so the numbers do not move when a developer or
  a runner updates their SDK. Empirically the numbers are also identical across Roslyn 4.12.0 and
  4.14.0.
- `scripts/CodeMetrics.cs` fails rather than reporting if the compilation it measures has any
  error: unresolved references produce degraded `IOperation` trees and therefore fictional metrics.

Verified by regenerating twice (byte-identical) and once under `LC_ALL=tr_TR.UTF-8`
(byte-identical).

### Why not `Microsoft.CodeAnalysis.Metrics`

Because it cannot run here. That package's entry point is `Metrics/Metrics.exe`, a .NET Framework,
Windows-only executable — `PE32 executable (console) ... for MS Windows`, with an `.exe.config`
beside it. Invoking its `/t:Metrics` MSBuild target on this repository's `ubuntu-latest` runner, or
on a macOS or Linux developer machine, fails with `cannot execute binary file` (exit 126). This is
still true of the latest release (5.6.0).

`Microsoft.CodeAnalysis.AnalyzerUtilities` ships the *same computation* as a `netstandard2.0`
library. `scripts/CodeMetrics.cs` is therefore that tool, cross-platform, with a diff-friendly
renderer in place of XML.

"But cannot run on the current runner" is not "cannot be in CI": #254 added a nightly
`windows-latest` job that runs `Metrics.exe` itself and cross-checks it, type by type, against
the baselines this page gates. The division of labour: **the cross-platform computation is the
per-PR gate** (fast feedback, same OS as the rest of CI), **Microsoft's binary is the oracle**
("is our ruler accurate?" is a nightly question). See [24 — The Metrics Oracle](./24-METRICS-ORACLE.md).

## Refreshing

One command, the same verb as every other checked-in artifact in this repository:

```bash
UPDATE_GOLDEN_FILES=true dotnet run scripts/CodeMetrics.cs
```

Or comment `/update-golden` on a pull request, which dispatches
`.github/workflows/update-golden-files.yml` — it regenerates the golden files, the `.api.txt`
baselines and the metrics baselines in one commit.

> The file-based `dotnet run scripts/*.cs` apps need the .NET 10 SDK. If your `PATH` SDK is older,
> run them under `~/.dotnet/dotnet`.

## The tolerance, and why it is what it is

CI runs `dotnet run scripts/CodeMetrics.cs` and fails when the regenerated metrics do not match the
checked-in baseline. The comparison is **not** uniform:

| Metric | Tolerance | Why |
|:--|:--|:--|
| `CC`, `CL`, `DIT`, `SLOC`, `ELOC` | **Exact** | Integer counts of syntactic facts. A pinned Roslyn reproduces them bit-for-bit; nothing about them can wobble. |
| `MI` | **±2** | A rounded floating-point function of a Halstead volume. The band absorbs rounding and reference-assembly noise, at the cost of ignoring a change too small to be worth a reviewer's attention anyway. |

The tolerance was chosen *after* measuring, not before: two regenerations and a `tr_TR` run
produced byte-identical output, and Roslyn 4.12.0 and 4.14.0 agree on every number, so exact
matching is demonstrably viable for the counts. The MI band exists as insurance, not because a
wobble was observed.

**The gate is on change, not on level.** Every entity in the baseline is compared; nothing is
compared against an absolute "good" value. A pull request that legitimately makes a method more
complex refreshes the baseline, and the refreshed diff is exactly what the reviewer is asked to
approve.

A failure names the entity and the before/after:

```
Code metrics baseline drifted: metrics/Parquet.SourceGenerator.metrics.txt
  ~ changed: M:Parquet.SourceGenerator.Emitter.ArrowBridgeEmitter.CanEmit(...)
      MI 97 -> 72 (tolerance +/-2)
      CC 1 -> 4
      SLOC 5 -> 18
      ELOC 1 -> 5
```

## The analyzer rules

`CA1502` (cyclomatic complexity), `CA1505` (maintainability index) and `CA1506` (class coupling)
are enabled for `[src/**.cs]` in `.editorconfig` at `warning`, which CI's `-warnaserror` promotes
to an error — the same mechanism every other analyzer rule in this repository uses, and the reason
`Directory.Build.props` deliberately does not set `TreatWarningsAsErrors` for local builds.

They are **scoped to `src/` on purpose.** A table-driven test with forty cases is not a
maintainability problem, and gating it would teach everyone to reach for a suppression.

Thresholds live in `CodeMetricsConfig.txt` at the repository root, which is an `AdditionalFiles`
entry on each `src/` project — that file is the only place these three rules read configuration
from. `.editorconfig` sets severity; it cannot set thresholds.

> **A trap worth knowing about.** A single unrecognised entry in `CodeMetricsConfig.txt` — writing
> `CA1506(NamedType)` where the parser wants `CA1506(Type)`, say — makes all three rules stop
> reporting **entirely and silently**. No `CA1509` diagnostic is emitted, the build goes green, and
> the gate is gone. Verified empirically against the analyzers shipped with the .NET 9 SDK. The
> accepted symbol kinds are `Assembly`, `Namespace`, `Type`, `Method`, `Field`, `Property`,
> `Event`. `scripts/CodeMetrics.cs` validates the file before doing anything else, so the gate the
> build depends on is itself checked by the gate CI depends on.

### Where the thresholds came from, honestly

The guidance this repository draws on is Mark Seemann's
([*Code That Fits in Your Head*](https://www.oreilly.com/library/view/code-that-fits/9780137464302/),
Addison-Wesley 2021, and blog.ploeh.dk):

- **Cyclomatic complexity ≤ 7 per method.** The argument is not numerological: it is Miller's
  "magic number seven, plus or minus two" — the limit of human short-term memory. A method with
  more branches than a reader can hold in their head cannot be reasoned about reliably, only
  guessed at. ([Put Cyclomatic Complexity to Good Use](https://blog.ploeh.dk/2019/12/09/put-cyclomatic-complexity-to-good-use/))
- **"It's not the specific threshold value that improves your code; paying attention does."**
  ([Curb code rot with thresholds](https://blog.ploeh.dk/2020/04/13/curb-code-rot-with-thresholds/))
  The value of a gate is that it forces a conversation at the moment of violation. It is not a
  claim that the number is optimal, and this page makes no such claim.
- The **80/24 rule** — methods no longer than 24 lines, lines no wider than 80 characters — is the
  same chunking idea applied to size. It is noted here as context; it is **not** implemented as a
  gate in this work, and `max_line_length` in `.editorconfig` is 100.

Now the uncomfortable part, stated rather than buried:

**These thresholds are deliberately weaker than the guidance they cite.** Seemann's position is
the opposite of grandfathering — "once you've responded to the situation, find a way to bring the
offending code back in line". The calibrated values below are a *pragmatic starting point* chosen
so that `main` builds clean on day one and the gate can exist at all; they are not the recommended
values, and they should not be quoted as if they were.

| Rule | Threshold | Current worst case | Distance from the cited guidance |
|:--|--:|:--|:--|
| `CA1502` cyclomatic complexity (method) | 25 | **25** — `ArrowMappingComponent.TryMap` | 25 is 3.6× the recommended 7. No method exceeds the threshold at all. |
| `CA1505` maintainability index | 10 | **27** — `SortedRowGroupPruningComponent.EmitPruningHelpers` | Live, not vacuous, and nowhere near the floor. |
| `CA1506` class coupling (type) | 95 | **66** — `TargetParser` | Considerable headroom; the weakest of the three. |
| `CA1506` class coupling (method) | 40 | **32** — `TargetParser.BuildCompoundModel` | Eight points of headroom. |

When this page was written, the picture was different: `TargetParser.CollectMembers` measured
cyclomatic complexity **105** in 463 lines with 11 parameters, `GetTargetModelCore` measured 38,
and both sat under name-by-name `[SuppressMessage]` attributes — a list of exactly two entries,
which was the ratchet made countable. **#263 deleted both entries** by decomposing the two methods
into the context/sink pipeline now in `TargetParser.cs` (a `MemberScope` + `MemberSink` replacing
the eleven parameters; one small method per attribute family, rule, and model shape). The
decomposition was proved behaviour-preserving by byte-identical golden files across five models
and two backends, and the numbers above are its measurement. `CA1502` stays at 25 rather than
ratcheting down because the new repository worst is exactly 25 — the pre-existing
`ArrowMappingComponent.TryMap`, not anything #263 wrote; the next ratchet step belongs with a
refactor of that method, not with a number tuned to pass.

> A small irony worth keeping in mind: adding a `[SuppressMessage]` attribute *raises* the
> measured class coupling of the method it sits on by one, because `SuppressMessageAttribute` is
> a referenced named type. It moved `GetTargetModelCore` from 39 to 40 while the exemption was
> live. Suppressions now count zero.

### The ratchet policy

1. **Thresholds only ever come down.** They are never raised to accommodate new code. A violation
   is a conversation about the code, not about the number.
2. **Suppressions only ever come off.** A new `[SuppressMessage]` for a metrics rule is not a
   remedy; it is a request to change this policy, and belongs in review as one.
3. **The target is cyclomatic complexity ≤ 7.** `CA1502` comes down from 25 towards it as the
   `TargetParser` and `CodeEmitter` refactors land, in steps that keep `main` green.
4. **Raising a number requires a written reason here**, in this file, next to the number.

## The baseline as it stands

Measured across `src/Parquet.SourceGenerator`, `src/Parquet.SourceGenerator.Legacy` and
`src/Parquet.SourceGenerator.Attributes`. `TargetParser` and its neighbours are compiled into both
generator projects, so they appear in two baselines with identical numbers.

**Assemblies**

| Assembly | MI | CC | CL | SLOC | ELOC |
|:--|--:|--:|--:|--:|--:|
| `Parquet.SourceGenerator` | 71 | 983 | 119 | 10,288 | 3,815 |
| `Parquet.SourceGenerator.Legacy` | 76 | 512 | 98 | 4,733 | 1,305 |
| `Parquet.SourceGenerator.Attributes` | 88 | 178 | 31 | 1,687 | 219 |

**The five least maintainable types**

| Type | MI | CC | CL | SLOC |
|:--|--:|--:|--:|--:|
| `Emitter.SortedRowGroupPruningComponent` | 37 | 11 | 9 | 343 |
| `Emitter.Components.StringDeduplicatorComponent` | 39 | 7 | 8 | 326 |
| `Emitter.CodeEmitter` | 44 | 144 | 26 | 2,536 |
| `Emitter.ArrowBridgeEmitter` | 47 | 38 | 9 | 747 |
| `Emitter.Components.SchemaComponent` | 50 | 14 | 7 | 259 |

The first two are the MI caveat in action: both are long `StringBuilder` emission methods with a
cyclomatic complexity of 11 and 7. They are *long*, not *tangled*.

**The five most complex methods**

| Method | CC | MI | CL | SLOC |
|:--|--:|--:|--:|--:|
| `Parser.TargetParser.CollectMembers` | **105** | 14 | 31 | 463 |
| `Parser.TargetParser.GetTargetModelCore` | 38 | 34 | 40 | 213 |
| `Emitter.Components.ArrowMappingComponent.TryMap` | 25 | 50 | 5 | 185 |
| `Parser.TargetParser.BuildCompoundModel` | 22 | 39 | 32 | 295 |
| `Emitter.CodeEmitter.GetWritePrimitiveCall` | 19 | 40 | 4 | 97 |

**Highest class coupling**

| Entity | CL | MI | CC | SLOC |
|:--|--:|--:|--:|--:|
| `Parser.TargetParser` (type) | 61 | 54 | 242 | 1,610 |
| `ParquetIncrementalGenerator` (type) | 35 | 59 | 15 | 125 |
| `Emitter.CodeEmitter` (type) | 26 | 44 | 144 | 2,536 |
| `Parser.TargetParser.GetTargetModelCore` (method) | 40 | 34 | 38 | 213 |
| `Parser.TargetParser.BuildCompoundModel` (method) | 32 | 39 | 22 | 295 |

**Distribution.** Of 408 distinct methods under `src/`, 22 (5.3%) exceed a cyclomatic complexity of
7 and 2 exceed 25; the median is 1 and the 90th percentile is 5. Depth of inheritance never exceeds
2 anywhere in the repository. The problem is not diffuse — it is concentrated in two files.

### What this says about `CodeEmitter.cs` — and about `TargetParser.cs`

`CodeEmitter` is the file [#251](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/251)
names, and now it has numbers: **2,536 source lines, 1,287 executable, cyclomatic complexity 144
across 39 methods, class coupling 26, maintainability index 44.** Its worst individual methods are
large rather than convoluted — `EmitWriteRowGroupAsync` is 216 lines at MI 28 with a cyclomatic
complexity of 5. It is a *size* problem, and the case for splitting it rests on that.

**But the measurement found something worse, and it is not the file the issue was looking at.**
`TargetParser` is the most complex and by far the most coupled type in the repository: cyclomatic
complexity **242** and class coupling **61** across 1,610 lines. `CollectMembers` alone carries a
cyclomatic complexity of **105** — fifteen times the recommended maximum, in one 463-line method
with eleven parameters (one of them `ref`) and a maintainability index of **14**, the worst number
in the repository by a margin of 13 points.

A method with 105 independent paths cannot be exhaustively tested and cannot be held in a reader's
head, and this one is the front door of the generator: every `[ParquetSerializable]` type in every
consumer's compilation goes through it. That is the alarming finding of this exercise, and it is
the one to act on first.

### What a large *method* does and does not cost — and which half of this is actionable

Layer 1 measures hand-written code, where a large method is a maintenance cost and the case for
splitting rests on readability. Layer 2 measures **emitted** code, where the same number invites a
performance argument that turns out to be wrong.

[#257](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/257) built four shapes of the
worst emitted method and measured them. Splitting it into smaller `async` methods costs 0.12%
allocation and buys nothing; extracting the non-async chunks into plain helpers *tripled* the
async state machine's field count (8 → 23), grew native code 21% and AOT binary size by 33 KB, and
regressed the `List<T>` fast path 6.8% under Server GC — in exchange for about 1 ms of one-time JIT
per process. The C# compiler had already outlined most of the method for free.

The conclusion, with the numbers, is in
[22 § Is emitted method size actionable?](./22-GENERATED-CODE-METRICS.md#is-emitted-method-size-actionable-no-watch-it-do-not-fix-it).
**Emitted method size is informational — a debuggability and correctness-risk proxy and a drift
detector — not a performance defect to fix.** The layer-1 complexity numbers on this page are a
different claim and remain actionable on their own terms: `CollectMembers` at 105 is a testability
and comprehension problem, not a codegen one.

## Related

- [17 - Generated Public API Baselines](./17-GENERATED-API-BASELINES.md) — the checked-in-artifact
  pattern this borrows wholesale.
- [22 - Generated Code Metrics](./22-GENERATED-CODE-METRICS.md) — layer 2, the emitted half.
- [18 - The API Change Contract](./18-API-CHANGE-CONTRACT.md) — the gating philosophy. Note that the
  metrics baselines are **not** a governed API surface: `metrics/*.metrics.txt` is not a catalogue,
  and changing it needs no `docs/api/LEDGER.md` entry.
- [05 - Testing Machinery & Benchmarking Strategy](./05-TESTING-STRATEGY-AND-BENCHMARKS.md).
