<p align="center">
  <img src="./assets/logo.svg" alt="Parquet.SourceGenerator" width="104" height="104">
</p>

# Parquet.SourceGenerator Documentation Hub

Welcome to the documentation portal for **Parquet.SourceGenerator** — a high-performance, zero-reflection Roslyn source generator emitting strongly-typed serializers and deserializers for [Parquet.Net](https://github.com/aloneguid/parquet-dotnet).

---

## 📚 Documentation Portal

The documentation is organized into four distinct sections:
- **[Getting Started](#-getting-started)** — Installation, quickstart, and configuration.
- **[Guides](#-guides)** — Task-oriented walkthroughs for annotating models, reading, streaming, and Native AOT.
- **[Reference](#-reference)** — Technical specifications, compiler diagnostics, compatibility matrix, and benchmarks.
- **[Architecture & Internals](#-architecture--internals)** — Deep-dives into compiler pipeline design, memory pooling, testing machinery, and governance.

---

### 🚀 Getting Started

1. **[Installation & Quickstart](./getting-started/installation.md)**
   - Package installation for modern .NET and .NET Framework.
   - First annotated model and basic read/write examples.

2. **[Configuration & Feature Levels](./getting-started/configuration.md)**
   - Setting `ParquetGeneratorFeatureLevel` via MSBuild or assembly attributes.
   - Compatibility policies and compiler output verification.

---

### 📖 Guides

1. **[Model Attributes & Serialization API](./guides/model-attributes.md)**
   - Annotating target classes, records, and structs with `[ParquetSerializable]`.
   - Column naming, ordering, custom decimal precision/scale, and timestamp units.

2. **[Reading Parquet Guide](./guides/reading-parquet.md)**
   - Modern fluent reader: `<Model>Parquet.From(stream)...`.
   - Materializing arrays (`ToArrayAsync`), memory-bounded streaming (`AsAsyncEnumerable`), and borrowed batch iteration (`AsBatches`).
   - Predicate pushdown filtering (`Where`) and in-memory buffer reads.

3. **[Native AOT & Trimming Guide](./guides/native-aot.md)**
   - Zero-reflection ahead-of-time compilation for CoreCLR and Linux x64 containers.
   - Trimming analysis, linker safety, and Native Directives (`rd.xml`).

4. **[Nested Types & Compound Models](./guides/nested-types.md)**
   - Serializing nested POCOs, struct fields, and collections (`List<T>`).
   - Definition and repetition level ladder mechanics and backend support boundaries.

5. **[Legacy Backend & .NET Framework Support](./guides/legacy-support.md)**
   - Targeting .NET Framework 4.7.2+, .NET Standard 2.0, and Parquet.Net 4.x/5.x.
   - Single-calling-contract parity between modern and classic emitters.

---

### 📋 Reference

1. **[Compiler Diagnostics Reference](./reference/compiler-diagnostics.md)**
   - Complete catalog of compiler diagnostics (`PARQ001` through `PARQ016`).
   - Diagnostic severities, root causes, and remediation code samples.

2. **[Parquet Compatibility & Types Matrix](./reference/compatibility-matrix.md)**
   - Interoperability guarantees with PyArrow, DuckDB, and Apache Parquet CLI.
   - Comprehensive 23-type support matrix across execution paths.

3. **[Schema Evolution & Versioning Contract](./reference/schema-evolution.md)**
   - Backwards and forwards compatibility across evolving schemas and producer engines.
   - Column reordering, missing optional fields, and strict required-field validation.

4. **[Known Limitations & Upstream Constraints](./reference/known-limitations.md)**
   - Audited gaps, unverified edge-case types, and tracked upstream Parquet.Net issues.

5. **[Benchmarks & Performance Baselines](./reference/benchmarks.md)**
   - BenchmarkDotNet multi-scale sweeps (1K, 10K, 100K, 1M rows).
   - Provenanced public datasets (TPC-H LineItem, Adult Census Income, Diamonds).

---

### 🏛️ Architecture & Internals

1. **[Architectural Vision & Design Tenets](./architecture/overview.md)**
   - Zero-reflection philosophy, high-throughput columnar transposition, and the Member Test.
   - The Three Visibility Tiers: Public Contract, Internal Capability, and Repository Implementation.

2. **[Roslyn Incremental Generator Pipeline](./architecture/roslyn-pipeline.md)**
   - Roslyn 4.0 `IIncrementalGenerator` caching pipeline and value-equatable syntax models.
   - Incrementality proofs under unrelated file, whitespace, and per-model edits.

3. **[Memory Architecture & Buffer Management](./architecture/memory-and-performance.md)**
   - `ArrayPool.Shared` recycling, eager progressive buffer returns, and single-pass traversal.
   - Empirical analysis of branchless null extraction and SIMD vectorization.

4. **[Testing Strategy & Quality Machinery](./architecture/testing-strategy.md)**
   - Multi-tier testing pyramid: package consumption, Roslyn driver tests, property-based fuzzing, and corrupted-file assertions.
   - Deterministic fixture corpus and zero-boxing IL bytecode verification.

5. **[API Governance & Change Contract](./architecture/api-governance.md)**
   - Governing public API evolution: the catalogue rule, semver buckets, and the API Change Ledger.
   - Derived `.api.txt` signature baselines and PR review diffs.

6. **[CI Gate Matrix & Branch Protection](./internals/ci-gate-matrix.md)**
   - Exhaustive audit of all GitHub Actions workflows, branch protection rulesets, and aggregate `build` job wiring.

7. **[Code Quality & Complexity Ratchets](./internals/code-quality-and-metrics.md)**
   - Roslyn complexity gates (`CA1502`, `CA1505`, `CA1506`) and emitted code static analysis.

8. **[Coverage Envelope Map](./internals/coverage-envelope.md)**
   - Source-derived property, nullability, and shape matrix generated by `scripts/CoverageMap.cs`.

9. **[Memory Triage Runbook](./internals/runbooks/memory-triage.md)**
   - Runbook for SOS memory inspection, Large Object Heap analysis, and `dotnet-dump` triage.

10. **Architectural Decision Records (ADRs)**
    - [ADR 0001: Roslyn Generator Tooling Evaluation](./architecture/adrs/0001-generator-tooling-evaluation.md)
    - [ADR 0002: Architectural Peer Study: Protobuf & Serialization Engines](./architecture/adrs/0002-peer-study-protobuf.md)
    - [ADR 0003: SonarAnalyzer.CSharp Removal & Permissive Replacement](./architecture/adrs/0003-sonar-removal.md)

---

## 🎯 Release Status & Governance

- **Live Release Status**: **[Milestone.md](../Milestone.md)** (Tracks 0.1 gates, blocking issues, and critical path).
- **Public API Change Ledger**: **[docs/api/LEDGER.md](./api/LEDGER.md)** (Governed by `PARQAPI002`).
