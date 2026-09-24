<p align="center">
  <img src="./assets/logo.svg" alt="Parquet.SourceGenerator" width="104" height="104">
</p>

# Parquet.SourceGenerator Documentation

`Parquet.SourceGenerator` emits strongly typed Parquet column readers and writers for C# models at
compile time, replacing Parquet.Net's runtime reflection. Reflection-free output is what makes it
Native AOT and trim friendly; diagnostics catch unsupported shapes before the code runs.

Design documents describe intent. For what works today, start with
[07 - Known Limitations](./07-KNOWN-LIMITATIONS.md).

## Using the generator

| Doc | What it covers |
|:---|:---|
| [02 - API Design & Attributes](./02-API-DESIGN-AND-ATTRIBUTES.md) | Attributes, an end-to-end example, feature levels |
| [19 - Public API Surface](./19-PUBLIC-API-SURFACE.md) | The read builder grid (source × shape × execution × pushdown), naming grammar, decisions D1–D5 |
| [13 - Compiler Diagnostics](./13-COMPILER-DIAGNOSTICS.md) | `PARQ001`–`PARQ015`: cause, reason, fix |
| [14 - Compatibility Matrix](./14-COMPATIBILITY-MATRIX.md) | Type envelope, encodings, interop with PyArrow/DuckDB, Arrow `RecordBatch` ingestion |
| [16 - Version & Schema Evolution](./16-VERSION-AND-SCHEMA-EVOLUTION.md) | Reordered, extra and missing columns; cross-version results |
| [10 - Native AOT Guide](./10-NATIVE-AOT-GUIDE.md) | AOT mechanics, the `Nullable<T>` pitfall in Parquet.Net 6.1, `rd.xml` fixes |
| [07 - Known Limitations](./07-KNOWN-LIMITATIONS.md) | Current limitations and the closed audit |
| [BENCHMARKS](./BENCHMARKS.md) | Headline numbers, real-dataset runs, the regression gate |

## Contract & decisions

| Doc | What it covers |
|:---|:---|
| [47 - 0.1 Contract & Design Goals](./47-0.1-CONTRACT-AND-DESIGN-GOALS.md) | What `0.1` means, visibility tiers, the release gate |
| [DECISIONS](./DECISIONS.md) | Every scoping/deferral decision (#176, #219–#227, #237, #262, #291, #480…) and its revisit gate |
| [48 - Flat-Read Removal](./48-FLAT-READ-REMOVAL-480.md) | Why the flat reads went, measured shrinkage, migration table |
| [18 - API Change Contract](./18-API-CHANGE-CONTRACT.md) | The three governed surfaces, their build gates, semver buckets; entries in [api/LEDGER](./api/LEDGER.md) |
| [17 - Generated API Baselines](./17-GENERATED-API-BASELINES.md) | The `.api.txt` grammar beside every golden file |
| [20 - Unified Pushdown](./20-UNIFIED-PUSHDOWN-API.md) | Future pushdown design (superseded for now by DECISIONS #237) |

## Internals

| Doc | What it covers |
|:---|:---|
| [01 - Vision & Architecture](./01-VISION-AND-ARCHITECTURE.md) | Why compile-time generation; the three components |
| [03 - Incremental Generator Pipeline](./03-INCREMENTAL-GENERATOR-PIPELINE.md) | Roslyn `IIncrementalGenerator` stages and value-equatable models |
| [15 - Nested Types Spike](./15-NESTED-TYPES-SPIKE-FINDINGS.md) | Dremel definition/repetition levels on both Parquet.Net engines |
| [11 - Performance Findings](./11-PERFORMANCE-OPTIMIZATION-FINDINGS.md) | String boxing, branchless null bitmaps, negative SIMD result |
| [12 - Buffer Reuse & Extraction](./12-BUFFER-REUSE-AND-EXTRACTION-STRATEGIES.md) | `ArrayPool` return strategy, direct columnar handoff |
| [28 - Build Incrementality](./28-BUILD-INCREMENTALITY-258.md) | Generator caching measurements (#258) |
| [27 - Protobuf Peer Study](./27-ARCHITECTURAL-PEER-STUDY-PROTOBUF.md) | What to adopt from Google.Protobuf / protobuf-net, and what not to |

## Testing & quality gates

| Doc | What it covers |
|:---|:---|
| [05 - Testing Strategy](./05-TESTING-STRATEGY-AND-BENCHMARKS.md) | Snapshot, incremental-cache, round-trip, fuzzing, AOT and benchmark tiers |
| [06 - Test Data](./06-TEST-DATA-SPECIFICATION.md) | Fixture corpus and SHA-256 provenance |
| [28 - Coverage Map](./28-COVERAGE-MAP.md) | *Generated.* Accepted kinds × shapes × backend evidence |
| [08 - IL Interrogation](./08-IL-INTERROGATION.md) | Zero-boxing checks with `ilspycmd` / `dotnet-inspect` |
| [09 - Memory Triage](./09-PERFORMANCE-TRIAGE-DOTNET-DUMP.md) | `dotnet-dump` / SOS playbook |
| [21 - Code Metrics](./21-CODE-METRICS.md) | Roslyn metrics baselines and the complexity ratchet |
| [22 - Generated Code Metrics](./22-GENERATED-CODE-METRICS.md) | Metrics for emitted code; why method size is watched, not fixed |
| [23 - Duplication](./23-DUPLICATION.md) | Token-level duplication drift gate |
| [24 - Metrics Oracle](./24-METRICS-ORACLE.md) | Nightly cross-check against Microsoft's `Metrics.exe` |
| [25 - Call Graph](./25-CALL-GRAPH.md) | Gated call-graph artifacts; generated views in [callgraph](./callgraph.md) |
| [26 - Mutation Testing](./26-MUTATION-TESTING.md) | Stryker over the behavioural suite only |

Contributor workflow, conventions and releasing: [`CONTRIBUTING.md`](../CONTRIBUTING.md). Upstream
Parquet.Net / Apache.Arrow gaps: [`UPSTREAM_DEPENDENCY_LIMITATIONS.md`](../UPSTREAM_DEPENDENCY_LIMITATIONS.md).
