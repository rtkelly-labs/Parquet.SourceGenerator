<p align="center">
  <img src="./assets/logo.svg" alt="Parquet.SourceGenerator" width="104" height="104">
</p>

# Parquet.SourceGenerator Documentation Hub

Welcome to the **Parquet.SourceGenerator** documentation repository. This folder contains the architectural specifications, API designs, implementation details, and project roadmap for building a production-grade, high-throughput C# Source Generator for the [Parquet.Net](https://github.com/aloneguid/parquet-dotnet) library.

---

## 📚 Documentation Portal

The documentation is organized into three distinct tiers based on audience and intent:
- **[Guides](#-guides)** — Task-oriented, consumer-facing documentation for adopting and using the generator.
- **[Reference](#-reference)** — Technical specifications, API surface contracts, compiler diagnostics, and compatibility matrices.
- **[Internals & Engineering Spikes](#-internals--engineering-spikes)** — Contributor deep-dives, compiler pipeline architecture, benchmarking machinery, and deterministic quality gates.

---

### 🚀 Guides

1. **[02 - API Design & Attributes](./02-API-DESIGN-AND-ATTRIBUTES.md)**
   - Annotating target classes with `[ParquetSerializable]`, `[ParquetColumn]`, and `[ParquetIgnore]`.
   - Data annotations, custom decimal precision/scale, and timestamp units.

2. **[19 - Public API Surface & Fluent Builder](./19-PUBLIC-API-SURFACE.md)**
   - Modern fluent read builder semantics: `PersonParquet.From(stream)...`.
   - Current collection-based write entry points; a symmetric write builder remains a post-freeze proposal (#219).
   - Structural explanation of the source, shape, execution, and pushdown axes.

3. **[10 - Native AOT & Type System Guide](./10-NATIVE-AOT-GUIDE.md)**
   - First-class Native AOT compatibility, zero reflection, and trim analysis.
   - CoreCLR runtime mechanics and Native Directives (`rd.xml`).

4. **[15 - Nested Types & Compound Models](./15-NESTED-TYPES-SPIKE-FINDINGS.md)**
   - Serializing nested POCOs, struct fields, and collections (`List<T>`, arrays) without runtime reflection.
   - Backend-specific compatibility boundary and remaining parity work in [document 42](./42-NESTED-BACKEND-SCOPE-176.md).

5. **[44 - Type Adapters](./44-TYPE-ADAPTERS.md)** and **[45 - NodaTime Adapter Package](./45-NODATIME.md)**
   - Serializing domain types the generator does not map itself through compile-time adapters.
   - `Parquet.SourceGenerator.NodaTime`: lossless NodaTime storage and explicit native interop.

6. **[Performance Benchmarks & Baselines](./BENCHMARKS.md)**
   - BenchmarkDotNet performance numbers, zero-boxing verification, and memory savings over reflection serializers.

---

### 📖 Reference

1. **[18 - API Change Contract & Governance](./18-API-CHANGE-CONTRACT.md)**
   - The API change contract governing emitted, package, and internal seam surfaces.
   - Reviewing change rationales in the [API Change Ledger](./api/LEDGER.md).

2. **[13 - Compiler Diagnostics Reference](./13-COMPILER-DIAGNOSTICS.md)**
   - Complete index of compiler diagnostics (`PARQ001` through `PARQ019`).
   - Descriptions, error explanations, and remediation steps.

3. **[14 - Parquet Compatibility Matrix](./14-COMPATIBILITY-MATRIX.md)**
   - Interoperability guarantees with PyArrow, DuckDB, and Apache Parquet CLI.
   - Encodings, compression codecs, and logical type mappings.

4. **[16 - Version & Schema Evolution](./16-VERSION-AND-SCHEMA-EVOLUTION.md)**
   - Backwards/forwards compatibility across Parquet.Net versions and evolving consumer schemas.

5. **[07 - Known Limitations & Upstream Dependencies](./07-KNOWN-LIMITATIONS.md)**
   - Audited gaps, edge-case type limitations, and tracked upstream Parquet.Net issues.

6. **[29 - Generator Feature Levels](./29-FEATURE-LEVELS.md)**
   - Named compatibility levels, MSBuild and assembly configuration, and generated-output stamps.

---

### 🔬 Internals & Engineering Spikes

1. **[01 - Vision & Architecture](./01-VISION-AND-ARCHITECTURE.md)**
   - High-throughput zero-reflection design and project philosophy.

2. **[03 - Incremental Generator Pipeline](./03-INCREMENTAL-GENERATOR-PIPELINE.md)**
   - Roslyn 4.0 `IIncrementalGenerator` caching pipeline and syntax provider architecture.

3. **[04 - Roadmap & Contributing](./04-ROADMAP-AND-CONTRIBUTING.md)**
   - Project lifecycle, release cadence, and contributing guidelines.

4. **[05 - Testing Machinery & Strategy](./05-TESTING-STRATEGY-AND-BENCHMARKS.md)**
   - Multi-tier testing suite: golden regression testing, IL bytecode verification, and interop oracles.

5. **[06 - Test Data Specification](./06-TEST-DATA-SPECIFICATION.md)**
   - Deterministic test fixture corpus and SHA-256 provenance manifests.

6. **[08 - IL Interrogation & Bytecode Verification](./08-IL-INTERROGATION.md)**
   - Automated zero-boxing assertions and disassembly triage using `ilspycmd` and `dotnet-inspect`.

7. **[09 - Memory Triage with dotnet-dump](./09-PERFORMANCE-TRIAGE-DOTNET-DUMP.md)**
   - SOS memory triage, Large Object Heap analysis, and memory leak triage.

8. **[11 - Performance Optimization Findings](./11-PERFORMANCE-OPTIMIZATION-FINDINGS.md)**
   - Empirical analysis of branchless null extraction, SIMD vectorization, and L1 span caches.

9. **[12 - Buffer Reuse & Extraction Strategies](./12-BUFFER-REUSE-AND-EXTRACTION-STRATEGIES.md)**
   - ArrayPool buffer recycling, progressive buffer returns, and cold-pool vs warm-pool heap dynamics.

10. **[16 - Version And Schema-Evolution Matrix](./16-VERSION-AND-SCHEMA-EVOLUTION.md)**
     - Schema-evolution contract: reordering, extra columns, absent optional and required columns.
     - Producer/consumer/version matrix across Parquet.Net, PyArrow and DuckDB, and the recorded report.
     - Cross-version interoperability between the modern and classic packages.

11. **[17 - Generated Public API Baselines](./17-GENERATED-API-BASELINES.md)**
     - The `.api.txt` signature-only baseline emitted next to every golden file, and its grammar.
     - Why it borrows the `PublicAPI.Shipped.txt` grammar, and the two deliberate deviations.
     - Deterministic ordinal ordering, the `UPDATE_GOLDEN_FILES` refresh path, and the CI gate.

12. **[18 - The API Change Contract](./18-API-CHANGE-CONTRACT.md)**
     - The rule: nothing enters a governed surface without a catalogue line *and* a ledger entry.
     - The three surfaces (emitted / shipped package / internal seams) and their build gates
       `PARQAPI001`, `RS0016` and `PARQAPI002`.
     - Semver buckets, the pre-1.0 stance, the `**Unapproved-by-design:**` escape hatch, and the
       four-step author process.

13. **[19 - Public API Surface & Naming Grammar](./19-PUBLIC-API-SURFACE.md)**
     - The four-axis read grid — source × shape × execution × pushdown — and which cells exist.
     - Eight catalogued surface defects, each checkable against the `*.api.txt` baselines.
     - The options-vs-parameters rule (D1), the naming grammar, and the fate of the flat methods.

14. **[20 - Unified Pushdown & the Generated/Shipped Boundary](./20-UNIFIED-PUSHDOWN-API.md)**
     - One inspectable filter replacing four pushdown mechanisms; capability declared by attribute, not at the call site.
     - What stays generated, what ships, and the measurements that decide it.
     - Its current-release boundary decision is recorded in [31 - Generated/Shipped Boundary Decision](./31-GENERATED-SHIPPED-BOUNDARY-DECISION.md).

15. **[21 - Code Metrics Baselines & The Complexity Ratchet](./21-CODE-METRICS.md)**
     - Checked-in Roslyn metrics baselines under `metrics/`, gated on drift rather than on absolute values.
     - What the Maintainability Index does *not* tell you, and why `CA1502` is calibrated below the
       cited guidance, with the history of its two named exceptions (#263 deleted both).
     - The measured evidence: `CodeEmitter` at 2,536 lines / complexity 144 (a size problem with
       well-decomposed methods), and the former `TargetParser.CollectMembers` at cyclomatic
       complexity 105 — decomposed in #263, worst method now 25.

16. **[22 - Generated Code Metrics](./22-GENERATED-CODE-METRICS.md)**
     - The same mechanism turned on the *emitted* code: a `*.metrics.txt` beside every `*.api.txt`.
     - Which metrics carry signal for generated code and which do not — the Maintainability Index
       is reported but not gated, with the measurement that says why.
     - `ELOC_PER_MEMBER`, the size-per-capability ratio: 10 executable lines per emitted member for
       flat models, 33 for row-level lists.

17. **[23 - Duplication Measurement & The Drift Gate](./23-DUPLICATION.md)**
     - Layer 3 of #251: token-level duplication across `src/`, checked in as `metrics/duplication.txt`
       and gated on drift — the `*.api.txt` grammar again.
     - Calibrated against the repo's demonstrated failure: the tool names the historical
       `ResolveSchemaField` copies and the three read paths that all broke on #196 before it was
       adopted.
     - Emitted code is out of scope by design — generated output repeating itself is the design,
       not a defect.

18. **[24 - The Metrics Oracle](./24-METRICS-ORACLE.md)**
     - Nightly `windows-latest` job running Microsoft's own `Metrics.exe` against the layer-1
       baselines — the independent check on the bespoke computation that gates everything else.
     - Type-level agreement gated (MI ±2, the rest exact); assembly totals reported but never
       gated, because the two tools differ in enumeration scope before they could differ in
       arithmetic.
     - Disagreement opens a GitHub issue rather than leaving a red schedule: failing nightlies
       get muted; issues get acted on.

19. **[25 - The Call Graph](./25-CALL-GRAPH.md)**
     - The #251 family's structural view: connectivity as a gated artifact — drift on a
       checked-in method-level edge list, catalogued cycles, a fan-out ratchet, and a
       layering rule (components must not call the emitter hub) that would have caught the
       `ResolveSchemaField` divergence as it happened.
     - The honest half: what the static approximation cannot see (delegates, virtuals), and
       why the unresolved count is a headline number rather than a footnote.

20. **[26 - Mutation Testing the Behavioural Suite](./26-MUTATION-TESTING.md)**
     - Layer 4 of #251: would a test notice if a line were *wrong*, not just executed? Stryker,
       run nightly. The design is the exclusion — golden-file / API-baseline / metrics / IL-shape
       suites are left out because they kill every mutation trivially and report a flattering lie.
     - Reports through one refreshed PR, never a red build; no threshold until the baseline exists.

21. **[27 - Architectural Peer Study: Protobuf & Serialization Engines](./27-ARCHITECTURAL-PEER-STUDY-PROTOBUF.md)**
     - Comparative analysis of Google.Protobuf, protobuf-net, and Parquet.SourceGenerator.
     - Architectural trade-offs, where PSG leads, peer mechanisms worth stealing (ABI matrix, corpus sweeps, defensive DoS limits), and why runtime engine seams are rejected.

22. **[30 - Schema Descriptor Evaluation](./30-SCHEMA-DESCRIPTOR-EVALUATION.md)**
     - Measured decision on compact generated schema metadata for issue #291.
23. **[28 - Coverage Envelope Map](./28-COVERAGE-MAP.md)**
     - Source-derived accepted property kinds, nesting and nullability shapes, backend evidence, and risk-ranked gaps.
     - Refresh with `dotnet run scripts/CoverageMap.cs -- --update`; CI rejects drift from the parser and generator dials.
24. **[28 - Build Incrementality Spike (#258)](./28-BUILD-INCREMENTALITY-258.md)**
     - CSharpGeneratorDriver throughput and managed allocation measurements for initial, unrelated-file, and per-model edits.
     - Public Roslyn tracked-output evidence, model value-equality proof, and the resolved `WithTrackingName` compatibility limitation.

23. **[36 - Feature Profiles and Per-Type Overrides](./36-FEATURE-PROFILES-SCOPE-225.md)**
     - Scope decision for named profiles and per-type configuration overrides.

---

## ⚡ Quick Summary of Intent

`Parquet.SourceGenerator` is designed to eliminate the reliance on runtime reflection when serializing and deserializing C# domain models (classes, records, structs) to and from Apache Parquet files using `Parquet.Net`. 

By emitting specialized, strongly-typed column readers and writers at compile time, the intended
outcome is:
- **Zero Reflection & Maximum Throughput**: Direct array transfers between C# memory and Parquet columns.
- **Native AOT & Trimming Compatibility**: reflection-free generated code is a precondition for
  AOT and trimming. CI publishes the AOT test project with `-r linux-x64` and runs the resulting
  native binary on every run — `linux-x64` only, and Parquet.Net itself still emits trim and
  AOT-analysis warnings.
- **Compile-Time Safety**: Catches schema mismatches and unsupported data types before running code.

> These documents describe intended design. For what is actually implemented today, see the
> [Known limitations](../README.md#known-limitations) table in the README and the full audit in
> [07 - Known Limitations](./07-KNOWN-LIMITATIONS.md).
