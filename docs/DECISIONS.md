# Decisions & Deferred Scope

One entry per scoping decision. Each says what was decided and what must be true before it is
revisited. Newer records supersede older ones; superseded entries stay, marked, so the history reads
in one place. The `0.1` framing that sits over all of them is
[47 - 0.1 Contract & Design Goals](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md).

| Issue | Topic | Status |
|:---|:---|:---|
| [#237](#237--generatedshipped-boundary) | Generated/shipped boundary | Decided — keep orchestration generated |
| [#291](#291--schema-descriptor-table) | Schema descriptor table | Evaluated, deferred |
| [#176](#176--nested-types-by-backend) | Nested types by backend | Scoped per backend |
| [#480](#480--flat-read-removal-supersedes-262) | Flat-read removal | Implemented — see [48](./48-FLAT-READ-REMOVAL-480.md) |
| [#262](#262--flat-read-freeze-superseded) | Flat-read freeze | Superseded by #480 |
| [#219](#219--write-builder-symmetry) | Write builder symmetry | Deferred |
| [#220](#220--ordinal-stable-column-catalog) | Column catalog | Deferred |
| [#221](#221--backend-neutral-accessor) | Backend-neutral accessor | Deferred |
| [#222](#222--row-group-metadata-and-selection) | Row-group metadata & selection | Deferred |
| [#225](#225--feature-profiles-and-per-type-overrides) | Feature profiles | Deferred |
| [#227](#227--feature-profile-golden-matrix) | Feature-profile golden matrix | Deferred |
| [#223](#223--linq-companion-package) | LINQ companion package | Deferred |
| [#177 / #178](#177--178--arrow-recordbatch-bridge) | Arrow `RecordBatch` ingestion/export | Ingestion experimental; export open |
| [#230](#230--release-readiness-2026-09-17-superseded) | Release readiness report | Superseded by #477 |

### Dependency order of the deferred seams

```
#220 catalog ─┐
#217 builder ─┼─► #221 accessor ─► #223 LINQ
#222 row-group┘         │
#225 profiles ─► #227 profile matrix
#219 write builder: designed alongside #217 / #220 / #221
```

Every deferred seam shares two acceptance conditions, not repeated below: it is implemented for
**both emitters**, and it is **reflection-free and verified under Native AOT**.

---

## #237 — Generated/shipped boundary

**Decision.** Format-shaped orchestration stays in generated code: the `ParquetReader`/`ParquetWriter`
calls, backend-specific field handling and the typed materialisation loop. The Attributes package
ships annotations, options, value types and the existing low-level helpers
(`NullableColumnExtractor`, `VectorizedColumnTransforms`). The modern and classic emitters target
materially different Parquet.Net APIs, and moving one path before the builder/catalog/accessor seams
settle would create a boundary that needs redesigning soon after it ships.

**Revisit when** the builder and backend-neutral catalog/accessor contracts are stable. The
experiment must port one sequential buffer read, compare TPC-H LineItem and Adult Census throughput,
record the Native AOT binary-size delta, and use a materialisation-focused denominator — end-to-end
throughput alone is dominated by Parquet.Net's encoding and compression.
[20 - Unified Pushdown](./20-UNIFIED-PUSHDOWN-API.md) is kept as the future design reference and is
superseded for the current release by this decision.

## #291 — Schema descriptor table

**Decision.** Keep the direct `ParquetSchema` constructor tree. A per-type descriptor table plus
one-time materialiser was built on an isolated branch; golden tests passed, but it cost more
executable code than it replaced, so it failed the issue's ELOC criterion. A static-initialisation
rewrite cannot claim a throughput win.

| Golden model | Direct ELOC | Descriptor ELOC |
|:---|---:|---:|
| `OrderEvent` | 1,020 | 1,030 |
| `NestedOrder` | 1,213 | 1,223 |
| `ListOrder` | 1,826 | 1,836 |
| `PocoOrder` | 2,679 | 2,689 |
| `ScalarMetric` | 934 | 944 |

**Revisit** only with a shared runtime schema factory (or support assembly) amortised across
generated types, measuring generated ELOC and cold-start cost.

## #176 — Nested types by backend

**Decision.** Scope is declared per backend rather than as a promise of parity:

| Backend | Supported generated shapes |
|:---|:---|
| Modern Parquet.Net v6 | `StructField` and list-of-leaf/POCO paths covered by the golden corpus |
| Classic Parquet.Net v4/v5 | Flat models only |
| Both | Maps and compound shapes outside the modern golden corpus are unsupported and report the existing diagnostic |

The AOT matrix proves flat models only; no AOT claim is made for compound shapes.

**Revisit when** legacy definition/repetition-level round trips, map semantics, AOT coverage,
property-based fuzzing and external interoperability checks exist. Full two-backend parity is also a
prerequisite for the Arrow bridge. Background: [15 - Nested Types Spike](./15-NESTED-TYPES-SPIKE-FINDINGS.md).

## #480 — Flat-read removal (supersedes #262)

**Decision.** The flat `ReadParquet*Async` methods are removed from the modern emitter before `0.1`;
the builder is the only modern read surface. The legacy emitter keeps its flat reads as its declared
subset (#246). No `[Obsolete]` release. Full record, measured shrinkage and migration table:
[48 - Flat-Read Removal](./48-FLAT-READ-REMOVAL-480.md).

## #262 — Flat-read freeze *(superseded)*

Kept flat reads through the `0.1.0` window, with a removal gate: #244 proves parameter-level
shrinkage, #219 settles write symmetry or its deferral, the ledger records the break, and migration
guidance is published. #480 answered that gate — see document 48, section "Why document 41 no
longer holds" (document 41 was this record's original file).

## #219 — Write builder symmetry

**Decision.** Deferred. The four collection-based write entry points are stable, baselined and do not
block the safety, compatibility or AOT claims. Designing a write builder separately from the final
read builder and column/accessor seams would make batching and source shape a second migration.

**Revisit** alongside #217/#220/#221. Settle whether the collection methods stay the front door,
then express collection shape, batching and async source as builder state. Keep the existing entry
points until the replacement has a tested migration path.

## #220 — Ordinal-stable column catalog

**Decision.** Deferred. A useful catalog must model nested leaf paths, logical types, nullability and
encoding hints without exposing Parquet.Net types; designing it before the nested-shape and accessor
contracts settle would lock in a second public surface.

**Target contract:** a compile-time, allocation-free descriptor with stable source-order ordinals,
full nested paths, CLR type metadata, nullability, decimal/timestamp metadata and encoding hints.

## #221 — Backend-neutral accessor

**Decision.** Deferred. Its shape depends on #220, the final builder (#217) and #222; shipping an
interface now would expose unstable generated methods or force a breaking reshape at the first freeze.

**Target contract:** one interface in the Attributes package, a generated zero-allocation singleton
per model, and a generic consumer test covering modern and classic Parquet.Net.

## #222 — Row-group metadata and selection

**Decision.** Public row-group enumeration and explicit subset reads are deferred. Existing generated
pruning stays internal and keeps providing predicate pushdown and sorted-key behaviour. For `0.1`,
row-group metadata is externally read-only ([47 §4.3](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md)).

**Target contract:** allocation-free metadata enumeration, explicit ranges/subsets, uniform predicate
availability, tests proving skipped row groups perform no I/O, and the existing pruning components
refactored onto the shared mechanism.

## #225 — Feature profiles and per-type overrides

**Decision.** Deferred. #290 supplies the configuration channel and the feature-level policy
(see [02 § Feature levels](./02-API-DESIGN-AND-ATTRIBUTES.md#4-feature-levels)); the profile matrix
still depends on the final builder and emitted-API freeze.

**Target contract:** named profiles rather than independent bits, documented MSBuild-versus-attribute
precedence, and every profile tested. `NetStandardCompat` must be demonstrated by a real
net472/netstandard2.0 consumer before it is advertised.

## #227 — Feature-profile golden matrix

**Decision.** Deferred until #225 fixes the supported profile set. The default profile is already
gated through generated source, API, metrics, call-graph, AOT and compatibility contracts; adding rows
first would multiply baselines without a stable configuration contract.

**Target contract:** profile × model × backend, compile and round-trip checks, `NetStandardCompat`
against a real consumer, reported CI wall-clock cost, and one workflow that refreshes every artifact
atomically.

## #223 — LINQ companion package

**Decision.** Outside the current release. The generator stays the compile-time physical layer; an
expression-tree provider belongs in a separate package once the builder, catalog, accessor and
row-group seams are stable, and must reach generated code only through the backend-neutral accessor.

**Target contract:** an AOT-safe plan core, residual-predicate behaviour, `ExplainAsync()`, strict
fallback behaviour, and multi-file/partition pruning.

## #177 / #178 — Arrow `RecordBatch` bridge

Ingestion (#177) ships as an experimental, conditionally emitted bridge gated on the consumer's
`Apache.Arrow` reference — no companion package. Its contract is in
[14 § Apache Arrow RecordBatch Ingestion](./14-COMPATIBILITY-MATRIX.md). Export (#178) uses the same
gate and remains open; `0.1` must not list it as complete.

## #230 — Release readiness, 2026-09-17 *(superseded)*

Measured readiness against the #230 API-freeze programme. The current gate is the
confidence/minimal-contract gate in [47 §8](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md#8-release-gate)
(#477). At the time: the API audit, builder, option ownership, ledger, API baselines, Arrow
`RecordBatch` paths, strict schema validation and MSBuild configuration (#224) were merged;
hostile-input safety (#306/#319, #307/#325, #315/#323), API compatibility (#244/#328, #246/#329),
Arrow/AOT and test evidence (#267/#326, #259/#322, #258/#327) and feature levels (#290/#332) were in
review. Release evidence — benchmark baselines, version/tag/changelog reconciliation, the full
release battery, API baseline promotion and package validation — was outstanding.
