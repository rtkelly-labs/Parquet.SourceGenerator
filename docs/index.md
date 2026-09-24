---
title: Parquet.SourceGenerator
description: "Compile-time Parquet serializers for C#: no reflection, Native AOT friendly, checked at build time."
# Navigation contract for any docs site that renders this folder (docs.ryankelly.dev does).
# Each section is a folder; pages inside are ordered by their `order` frontmatter. A page's title
# is its H1. Folders not listed here (api/, assets/, superpowers/) are not pages.
sections:
  - dir: guide
    label: Guides
  - dir: reference
    label: Reference
  - dir: design
    label: Design & Decisions
  - dir: internals
    label: Internals
  - dir: quality
    label: Testing & Quality Gates
---

<p align="center">
  <img src="./assets/logo.svg" alt="Parquet.SourceGenerator" width="104" height="104">
</p>

# Parquet.SourceGenerator

`Parquet.SourceGenerator` generates strongly typed Parquet column readers and writers for C# models
at compile time, so Parquet.Net never needs runtime reflection. Because the generated code uses no
reflection, it works with Native AOT and trimming. Unsupported shapes are reported as build
diagnostics before any code runs.

New here? Start with [Getting Started](./guide/getting-started.md). To see what doesn't work yet,
read [Known Limitations](./guide/limitations.md).

## Guides

| Page | What it covers |
|:---|:---|
| [Getting Started](./guide/getting-started.md) | Install, annotate a model, write and read |
| [Attributes & Configuration](./guide/attributes.md) | Every attribute, nullability rules, feature levels |
| [Reading & Writing](./guide/reading-and-writing.md) | Batched and async writes, parallel and streaming reads, columnar batches, pruning, options |
| [Native AOT](./guide/native-aot.md) | AOT mechanics, the `Nullable<T>` pitfall in Parquet.Net 6.1, `rd.xml` fixes |
| [Schema Evolution](./guide/schema-evolution.md) | Reordered, extra and missing columns; cross-version results |
| [Known Limitations](./guide/limitations.md) | Current limitations and the closed audit |
| [Benchmarks](./guide/benchmarks.md) | Headline numbers, real-dataset runs, the regression gate |

## Reference

| Page | What it covers |
|:---|:---|
| [Compiler Diagnostics](./reference/diagnostics.md) | `PARQ001`–`PARQ015`: cause, reason, fix |
| [Compatibility Matrix](./reference/compatibility.md) | Type envelope, encodings, PyArrow/DuckDB interop, Arrow `RecordBatch` ingestion |
| [Coverage Map](./reference/coverage-map.md) | *Generated.* Accepted kinds × shapes × backend evidence |
| [Public API Surface](./reference/api-surface.md) | The read builder grid, naming grammar, decisions D1–D5 |
| [API Change Ledger](./api/LEDGER.md) | Every governed API change and its semver bucket |

## Design & Decisions

| Page | What it covers |
|:---|:---|
| [Architecture](./design/architecture.md) | Why compile-time generation; the three components |
| [0.1 Contract & Design Goals](./design/release-contract.md) | What `0.1` means, visibility tiers, the release gate |
| [Decisions & Deferred Scope](./design/decisions.md) | Every scoping and deferral decision and its revisit gate |
| [Flat-Read Removal](./design/flat-read-removal.md) | Why the flat reads were removed, measured shrinkage, migration table |
| [Unified Pushdown](./design/unified-pushdown.md) | Future pushdown design (superseded for now by decision #237) |
| [Peer Study: Protobuf](./design/protobuf-peer-study.md) | What to adopt from Google.Protobuf / protobuf-net, and what not to |

## Internals

| Page | What it covers |
|:---|:---|
| [Incremental Generator Pipeline](./internals/incremental-pipeline.md) | Roslyn `IIncrementalGenerator` stages and value-equatable models |
| [Nested Types: Level Encoding](./internals/nested-types.md) | Dremel definition/repetition levels on both Parquet.Net engines |
| [Performance Findings](./internals/performance-findings.md) | String boxing, branchless null bitmaps, a negative SIMD result |
| [Buffer Reuse & Column Extraction](./internals/buffer-reuse.md) | `ArrayPool` return strategy, direct columnar handoff |
| [Build Incrementality](./internals/build-incrementality.md) | Generator caching measurements (#258) |
| [Call graph](./internals/callgraph.md) | *Generated.* Type-level view of the generator ([emitted code](./internals/callgraph-generated.md)) |

## Testing & Quality Gates

| Page | What it covers |
|:---|:---|
| [Testing Strategy](./quality/testing.md) | Snapshot, incremental-cache, round-trip, fuzzing, AOT and benchmark tiers |
| [Test Data](./quality/test-data.md) | Fixture corpus and SHA-256 provenance |
| [Generated API Baselines](./quality/api-baselines.md) | The `.api.txt` grammar beside every golden file |
| [API Change Contract](./quality/api-change-contract.md) | The three governed surfaces, their build gates, semver buckets |
| [IL Interrogation](./quality/il-interrogation.md) | Zero-boxing checks with `ilspycmd` / `dotnet-inspect` |
| [Memory Triage](./quality/memory-triage.md) | `dotnet-dump` / SOS playbook |
| [Code Metrics](./quality/code-metrics.md) | Roslyn metrics baselines and the complexity ratchet |
| [Generated Code Metrics](./quality/generated-code-metrics.md) | Metrics for emitted code; why method size is watched, not fixed |
| [Duplication Gate](./quality/duplication.md) | Token-level duplication drift gate |
| [Metrics Oracle](./quality/metrics-oracle.md) | Nightly cross-check against Microsoft's `Metrics.exe` |
| [Call Graph Gates](./quality/call-graph.md) | Drift, cycle, fan-out and layering gates |
| [Mutation Testing](./quality/mutation-testing.md) | Stryker over the behavioural suite only |

Contributor workflow, conventions and releasing: [`CONTRIBUTING.md`](../CONTRIBUTING.md). Upstream
Parquet.Net / Apache.Arrow gaps: [`UPSTREAM_DEPENDENCY_LIMITATIONS.md`](../UPSTREAM_DEPENDENCY_LIMITATIONS.md).
