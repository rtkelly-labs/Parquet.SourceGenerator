# 25 — The Call Graph (#252): structure as a first-class, gated artifact

Layers 1–3 ([21](./21-CODE-METRICS.md), [22](./22-GENERATED-CODE-METRICS.md),
[23](./23-DUPLICATION.md)) measure *volume, branching, and overlap* — all properties of
individual methods. The defects that have actually cost this repository time were never
line-level: three hand-rolled copies of `ResolveSchemaField` broke together on #196, and
#264 is a receipt for two components independently reading one footer. Those are
**graph** defects — the shape of who-calls-whom — and until this page, nothing observed
the shape.

## The artifacts

| what | where | gated? |
|:--|:--|:--|
| method-level edge list, one `E:` line per static call edge | `artifacts/callgraph/<project>.callgraph.txt` | report — diffed against the base on every PR |
| every declared cycle, with its reason | `graph/callgraph.allowlist.txt` (checked in) | presence in the allowlist *is* the gate |
| type-level Mermaid of the generator | `artifacts/callgraph/callgraph.md` | report |
| Mermaid of the generated code's graph — "what a consumer's debugger walks" | `artifacts/callgraph/callgraph-generated.md` | documentation only |
| run summary (nodes/edges/unresolved/fan-out/depth) | `artifacts/callgraph/callgraph-summary.md` → step summary | informational |

Everything under `artifacts/` (gitignored) is derived from the code on every run and never
checked in; `--out` moves it, and `--golden` points the generated-code graph at the golden
models the test suite publishes (default `artifacts/golden`). The allowlist is the one checked-in
file, because it is a decision rather than an output. Produce it all with
`dotnet run scripts/CallGraph.cs`, or with every other derived output via
`dotnet run scripts/DerivedOutputs.cs`. CI runs the latter in its `derived` job, uploads the
result in the `derived-outputs` artifact, and on a pull request shows the edge and Mermaid diff
against the merge base in the "Call graph" section of the derived-output comment
([17](./17-GENERATED-API-BASELINES.md#the-review-diff)).

## The three gates

Until the derived-output change there was a fourth: **edge drift**, which failed CI whenever the
edge list differed from checked-in `graph/<project>.callgraph.txt` baselines (and kept
`docs/callgraph.md` and `docs/callgraph-generated.md` checked in beside them). A new edge is still
a new dependency and a removed edge still a decoupling, and either should still be a decision — but
the review diff now puts that decision in front of the reviewer without a refresh commit, and the
drift gate could never reject anything a refresh would not have accepted. The three gates below run
on the freshly computed graph and still fail CI (the `derived` job):

1. **Cycles.** No multi-node strongly-connected component without a catalogued,
   justified `SCC` line. Direct self-recursion needs a `SELF` line with a stated reason.
   Today's entire cycle inventory is the #176 compound parser and the emitter's
   definition-ladder — genuinely recursive over trees, bounded by `MaxCompoundDepth` and
   the containment path. That is the case for recursion, kept *countable* — three `SELF`
   lines and one `SCC`, not a silent mesh. Mutual recursion *across* components has no
   allowlist kind at all, because it has no legitimate use in this architecture.
2. **Fan-out.** `Mark Seemann`'s chunking argument says a method calling more than about
   seven others can't be held in the head at once; 7 is an archetype, not a law — but the
   archetype is where the ratchet is aimed, so the caps (28 today — one method,
   `CodeEmitter.EmitSource`, sits on it) come down over time and never up, per the
   CodeMetrics policy.
3. **Layering.** No `Emitter.Components.*` → `CodeEmitter`/`LegacyCodeEmitter` edge: a
   spoke must not call its hub. Had this gate existed, the `ResolveSchemaField`
   divergence would have been visible as it happened.

The picture when this page was written: 211 nodes / 356 edges / max depth 6 from public entry
points in the main project; 105 / 121 / 6 in Legacy. The run summary has today's.

## What this graph does NOT capture — read this before trusting an absence

This is a **static approximation over syntax**: no compilation, no symbol resolution. A
call site is attributed by receiver name and method name + argument count against the
project's own methods, and anything ambiguous or external is **counted unresolved, never
guessed**. Concretely:

- **Delegates are invisible.** `Action<>`/`Func<>` fields invoked without a method name
  (the emitter's per-component lambda seams) produce no edge. Their call sites are inside
  the unresolved count — currently 86% of all call sites, dominated by framework and
  Parquet.Net calls, which is why the number is in the header, not hidden.
- **Virtual and interface dispatch resolves to the name**, not the runtime target. An
  edge into an interface method means "somewhere that interface is implemented" — treat
  the edge as weaker than a concrete one.
- **Complex receivers are refused**: `a.b.M()`, `x?.M()`, `((T)y).M()` are unresolved by
  policy — a wrong guess here is worse than a counted blind spot. (The same rule,
  learned the hard way during calibration, keeps `item.GetHashCode()` inside
  `EquatableArray.GetHashCode` from becoming a fabricated recursion — absent an edge is
  *not* proof of independence; the gates only reject cycles they actually saw.)
- **Overloads sharing name and arity merge** into one node; parameter types join the
  method identity precisely so that calls between sibling overloads read as real edges
  instead of false self-loops.

## Why the generated graph is documentation, not a gate

Layer 2 already reports the emitted code's size and shape per capability; its call graph is
a *description* of what the emitter outputs — the point lookup → pruned-range → column
read → materialisation chain that a consumer's debugger walks. Duplicating a gate there
would add noise without adding a question anyone can answer differently.

Refs: #252 (this), #251 (epic), #260 (overlap; this is the connectivity view of the same
failure), #264 (two mechanisms, one concept — a graph-shape problem the allowlist format
makes visible).
