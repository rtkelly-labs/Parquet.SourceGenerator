# 20 - Unified Pushdown & the Generated/Shipped Boundary

> **Status**: proposal for the `0.1.0` API freeze (#230). Specifies the public API shape only;
> the implementation plan and its blocking spike are in #237.
> Prerequisites: #220 (column catalog), #221 (accessor interface).
>
> Numbered 20, not 18: `docs/17` and `docs/18` were taken by #235 and #236 while this was being
> written. That is the third such collision in a week — see #228.

## 1. The problem, stated as API

Four unrelated pushdown mechanisms ship today, and a fifth (#152, bloom probing) is designed to
arrive as a sixth shape:

| Source | Public shape | Coverage |
|:---|:---|:---|
| #149 zone-map predicate | `Func<{T}RowGroupMetadata, bool>` parameter | 4 of 12 read cells |
| #151 ordered lookup | `ReadParquetBy{Column}Async`, `ReadParquet{Column}RangeAsync` | **~2 methods per `[ParquetSortKey]` column** |
| #150 null bypass | none — internal to the read loop | all cells |
| observability | `ParquetPruneStatistics?` mutable parameter | **only** the #151 family |

Three consequences follow directly from that table.

**Pushdown surface multiplies by column, not just by axis.** The `SortedEvent` test model carries
three sort keys and receives six extra methods. Add bloom filters and a model with five marked
columns acquires a double-digit method family that exists for no reason other than that the
predicate could not express the query.

**Pruning is unobservable exactly where it is least predictable.** A caller using `.Where(...)`
cannot ask how many row groups were skipped; only the ordered-lookup family accepts
`ParquetPruneStatistics`.

**The mechanisms cannot compose.** There is no way to express "sorted range on `Timestamp` **and**
equality on `TenantId`" — the two live in different API families.

### 1.1 Root cause

`Func<TMetadata, bool>` is **opaque**. The runtime can only *invoke* it, once per row group.

Binary search over monotonic row groups needs to know *that the filter is a range on a specific
column* before it can search. A bloom probe needs to know *that the filter is an equality on a
column with a filter block*. Neither can be recovered from a delegate.

So #151 did not become a per-column method family through carelessness — the representation forced
it, and every future strategy forks the same way for the same reason. **The unification has to start
with making the filter inspectable.**

---

## 2. Principle

> **Generate what is type-shaped. Ship what is format-shaped.**

Materialising a `Person` from column buffers is type-shaped: it must be generated, and it is the
only genuinely hot code. Deciding which row groups to read, renting buffers, driving the loop,
fanning out workers, planning pushdown — all format-shaped, identical for every `T`, and currently
copied into every generated file at **~2,750 lines per model**.

Two consequences that are not about performance:

- **The public API becomes specifiable.** `PublicAPI.Shipped.txt` and ordinary .NET documentation
  tooling can see shipped assemblies. They cannot see generated code — which is why the baselines in #235
  had to be invented at all, and why #229 has to render its reference from those files.
- **The backends can converge.** V5 emits 6 members to v6's 72 because every feature had to be
  re-emitted per backend. A shipped pipeline behind an accessor is written once.

---

## 3. API shape

### 3.1 Column tokens — generated data

Emitted from the column catalog (#220), one static per leaf column:

```csharp
public static partial class PersonParquet
{
    public static class Columns
    {
        public static readonly ParquetColumn<long>     Id;
        public static readonly ParquetColumn<DateTime> Timestamp;
        public static readonly ParquetColumn<string>   TenantId;
    }
}
```

```csharp
// shipped
public readonly struct ParquetColumn<T>
{
    public int Ordinal { get; }
    public string Name { get; }   // the Parquet column name
    public string Path { get; }   // the schema path; a leaf inside a nested group has one
}
```

`Path` rather than `Name` alone is the primary key, because #176 M2 has landed and leaf columns
inside nested groups exist on `main` today.

### 3.2 Filters — shipped, inspectable, allocation-irrelevant

```csharp
// shipped
public abstract class ParquetFilter
{
    public static ParquetFilter operator &(ParquetFilter left, ParquetFilter right);
    public static ParquetFilter operator |(ParquetFilter left, ParquetFilter right);
    public static ParquetFilter Not(ParquetFilter filter);
    public static ParquetFilter All { get; }

    public abstract ParquetFilterKind Kind { get; }
    public abstract void Accept(IParquetFilterVisitor visitor);
}

public enum ParquetFilterKind { All, And, Or, Not, Compare, Range, Set, Null }
public enum ParquetComparison { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }
```

Leaf nodes stay typed so the planner can compare against typed statistics without boxing:

```csharp
public sealed class ParquetCompareFilter<T> : ParquetFilter
{
    public ParquetColumn<T> Column { get; }
    public ParquetComparison Comparison { get; }
    public T Value { get; }
}

public sealed class ParquetRangeFilter<T> : ParquetFilter   // inclusive bounds
{
    public ParquetColumn<T> Column { get; }
    public T Minimum { get; }
    public T Maximum { get; }
}
```

Built through extension methods on the column token:

```csharp
// shipped
public static class ParquetColumnFilters
{
    public static ParquetFilter EqualTo<T>(this ParquetColumn<T> column, T value);
    public static ParquetFilter NotEqualTo<T>(this ParquetColumn<T> column, T value);
    public static ParquetFilter GreaterThan<T>(this ParquetColumn<T> column, T value);
    public static ParquetFilter GreaterThanOrEqualTo<T>(this ParquetColumn<T> column, T value);
    public static ParquetFilter LessThan<T>(this ParquetColumn<T> column, T value);
    public static ParquetFilter LessThanOrEqualTo<T>(this ParquetColumn<T> column, T value);
    public static ParquetFilter Between<T>(this ParquetColumn<T> column, T minimum, T maximum);
    public static ParquetFilter In<T>(this ParquetColumn<T> column, params T[] values);
    public static ParquetFilter IsNull<T>(this ParquetColumn<T> column);
    public static ParquetFilter IsNotNull<T>(this ParquetColumn<T> column);
}
```

**Methods, not comparison operators.** Overloading `==` to return a non-`bool` obliges
`Equals`/`GetHashCode` overrides on the column token and trips the standard analyzers, for the sake
of syntax that reads no better than `.EqualTo(...)`. `&` and `|` carry no such obligation and are
kept.

**No expression trees.** The tree is ordinary objects, so it is Native AOT clean, and it is built
once per query — allocation there is irrelevant against a file read.

### 3.3 The call site

```csharp
await PersonParquet.From(path)
    .Where(PersonParquet.Columns.Timestamp.Between(from, to)
         & PersonParquet.Columns.TenantId.EqualTo(tenant))
    .ToListAsync(cancellationToken);
```

That is the **only** pushdown entry point. It replaces the predicate parameter, both ordered-lookup
families, and the bloom API that #152 has not yet shipped.

### 3.4 Capability is declared on the model, not chosen at the call site

The filter says what the caller wants. The attributes say what the file can support. The planner
picks the strategy. **The call site above does not change** across any row of this table:

| Declared on the model | What that same call does |
|:---|:---|
| nothing | zone-map scan of footer `[Min, Max]` |
| `[ParquetSortKey]` on `Timestamp` | binary search to a contiguous row-group range (#151) |
| `[ParquetColumn(BloomFilter = true)]` on `TenantId` | bloom probe for the equality term (#152) |
| both | ordered range narrows the span, bloom probes within it |
| `[ParquetSerializable(Pushdown = false)]` | `.Where` is not emitted — see §3.5 |

Adding `[ParquetSortKey]` to a column upgrades a linear scan to a binary search **without touching a
single call site**, and `ReadParquetBySequenceNumberAsync` stops needing to exist.

### 3.5 Capability gating without generated API surface

A shipped generic builder has the same members for every `T`, which collides with both the
attribute-driven capability above and #217's rule that an unsupported combination is *absent* rather
than throwing.

Resolved with capability interfaces on the generated accessor gating shipped extension methods:

```csharp
// shipped
public interface IParquetAccessor<T>        { /* schema, columns, materialize, extract */ }
public interface IZoneMapCapable<T> : IParquetAccessor<T> { /* typed statistics by ordinal */ }
public interface IOrderedCapable<T> : IZoneMapCapable<T>  { /* sort-key ordinals */ }
public interface IBloomCapable<T>   : IParquetAccessor<T> { /* bloom ordinals, probe */ }
public interface IColumnBatchCapable<T> : IParquetAccessor<T> { /* batch struct */ }

public static ParquetFilteredSource<T, TAccessor> Where<T, TAccessor>(
        this ParquetMemorySource<T, TAccessor> source, ParquetFilter filter)
    where TAccessor : struct, IZoneMapCapable<T>;
```

A model whose accessor does not implement `IZoneMapCapable<T>` has no `.Where` — it fails to
resolve at compile time, exactly as #217's absent members do, with **zero generated API surface**
and no per-model façade. #226's diagnostic reports the cause in place of the bare `CS1929`.

> **Constraint: no static abstract interface members.** The runtime assembly targets
> `netstandard2.0`, where they do not exist. Accessors expose instance members and a
> `static readonly Instance`, per #221 — not `static abstract`. Any design that reaches for static
> abstracts or generic math is out of scope until the V5 backend is dropped.

### 3.6 Sources and terminals — four shipped generic structs

Replacing four structs generated *per model*:

```csharp
public readonly struct ParquetStreamSource<T, TAccessor>   where TAccessor : struct, IParquetAccessor<T>
public readonly struct ParquetMemorySource<T, TAccessor>   where TAccessor : struct, IParquetAccessor<T>
public readonly struct ParquetFilteredSource<T, TAccessor> where TAccessor : struct, IParquetAccessor<T>
public readonly struct ParquetParallelSource<T, TAccessor> where TAccessor : struct, IParquetAccessor<T>
```

The type-state rules from #217 carry over unchanged, now expressed as constraints rather than as
emitted-or-not members: no `Parallel` on a stream source; no `Where` after `Parallel` until #222
teaches the parallel reader to prune; `Batches` gated on `IColumnBatchCapable<T>`.

Callers never write the type arguments — `PersonParquet.From(...)` returns the constructed type.

### 3.7 Observability replaces the out-parameter

```csharp
// shipped
public sealed class ParquetReadPlan
{
    public int RowGroupCount { get; }
    public int RowGroupsRead { get; }
    public int RowGroupsPruned { get; }
    public ParquetPruningStrategy StrategiesApplied { get; }   // [Flags]
    public IReadOnlyList<ParquetResidual> Residuals { get; }   // terms no strategy could serve
}

[Flags]
public enum ParquetPruningStrategy { None = 0, ZoneMap = 1, Ordered = 2, Bloom = 4, NullCount = 8 }
```

Reached by `.Explain(cancellationToken)` on any source, filtered or not. This is the honesty
requirement: a filter that silently degrades to a full scan is the classic pushdown failure, and
`Residuals` names the terms that degraded. `ParquetPruneStatistics` is superseded.

---

## 4. What folds

| Today, or planned | Becomes |
|:---|:---|
| `ReadParquetBy{Column}Async` × N columns | `.Where(Columns.C.EqualTo(v))` |
| `ReadParquet{Column}RangeAsync` × N columns | `.Where(Columns.C.Between(a, b))` |
| `predicate` parameter on 4 read cells | `.Where(...)` on every source |
| `ParquetPruneStatistics` parameter | `.Explain()` |
| #152 bloom point lookups | `.Where(Columns.Id.EqualTo(g))` — no new members |
| #148 memory-mapped reader | `From(path)` — a source, not a method family |
| #146 prefetch pipeline | a runtime option on the pipeline |

Four of the six open read issues stop adding public API entirely.

---

## 5. Why the performance requirements permit this

The generated read loop already has the right shape: awaits are per column per row group, and
materialisation is a single synchronous `for` loop over rows. **The accessor boundary is therefore
one call per row group** — amortised over 50,000 rows at the default row group size.

Two measurements already in this repository settle the rest:

- `docs/12` §6.2 — *"Roughly 92% of a Parquet write is not the extraction. Encoding, compression and
  page assembly inside Parquet.Net dominate."*
- `docs/11` §6.3 — *"a 6× win in extraction shows up as a few percent of wall clock."*

The code moving to shipped helpers is orchestration: a fraction of the ~8% that is not Parquet.Net.

**Rules that keep it true, and are part of this specification:**

1. **The seam is per row group, never per row or per value.** Per-row dispatch is disqualifying.
2. **The materialisation loop stays generated.** It is type-shaped, it is the only hot code, and it
   carries the zero-boxing guarantees (`IlInterrogationTests`, `ZeroBoxingSerializationTests`).
3. **Accessors are constrained `struct`.** `where TAccessor : struct` compiles to a `constrained.`
   callvirt that the runtime resolves statically for value types — a specification guarantee, not a
   JIT heuristic. Native AOT specialises the same way.
4. **`Span<T>` never crosses an `await`.** Buffers cross the async boundary as `Memory<T>` and are
   converted inside the synchronous materialiser.
5. **The planner is off the hot path entirely.** It runs once per file against the footer. Today's
   design *costs* throughput, because 8 of 12 read cells cannot express pruning and callers full-scan.

### 5.1 The gate cannot currently see a regression here

Allocation is the benchmark gate; wall-clock is reported and not enforced. `docs/12` §6.3 established
that generator-side changes do not move allocation at all — the allocated bytes are Parquet.Net's own
encode and compress buffers, which every path pays identically.

So a throughput regression from a badly placed seam produces **no allocation change and a green
build**. Before any of this work lands, the benchmark job for it must run with `fail_on_time` on a
quiet machine, and a materialisation micro-benchmark must exist whose denominator is the loop itself
rather than end-to-end. Running the spike against the current gate would prove nothing.

---

## 6. Open question the spike must answer

Tracked in #237, blocking #220/#221/#222.

1. **Throughput.** Port one read path to the shipped shape; measure against the generated one on the
   TPC-H LineItem model. Expected within noise, by §5's 92% finding.
2. **Native AOT binary size.** Struct generics specialise per instantiation. Today's code is already
   duplicated per model so it should be a wash — but nobody has measured it, and it is the one number
   that could genuinely go the wrong way.

Neither question is "is interface dispatch fast enough". §5 rule 3 answers that by construction.

---

## 7. Migration

1. #220 column catalog and #221 accessor land first; both are prerequisites, not parallel work.
2. Spike (#237) answers §6. **If throughput regresses, this proposal is withdrawn** and pushdown
   unification proceeds with generated code — §3's API shape is independent of §2's boundary and
   remains worth doing on its own.
3. Filters, planner and `.Explain()` ship; `.Where(Func<...>)` and the ordered-lookup families are
   retained for one release, then removed at the freeze, per docs/19 decision D3.
4. `ParquetPruneStatistics` is superseded by `ParquetReadPlan` on the same schedule.
5. #152 and #148 are re-scoped to planner strategies and source overloads, and lose their proposed
   public API.

> **Naming note.** `Parquet.SourceGenerator.Attributes` already ships runtime behaviour —
> `ParquetColumnStatistics`, `NullableColumnExtractor`, `VectorizedColumnTransforms` — under a name
> that says otherwise, and this proposal adds substantially more. Rename to
> `Parquet.SourceGenerator.Runtime` at the `0.1.0` freeze, where the break is already being taken.

---

## Related

- #235 / `docs/17` — the signature baselines that made this surface measurable in the first place
- #236 / `docs/18` — the API change contract; every type specified here enters through its ledger
- #216 / `docs/19` — the surface audit this continues
- #217 — the builder whose type-state rules §3.6 preserves
- #220, #221, #222 — prerequisites, re-scoped by §7
- #223 — the LINQ layer, which lowers expression trees onto §3.2 rather than inventing its own
- #225, #226 — profiles and the omitted-member diagnostic, which §3.5 makes tractable
- #237 — the spike
