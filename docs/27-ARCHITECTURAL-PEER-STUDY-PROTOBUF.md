# 27 — Architectural Peer Study: Protobuf & Serialization Engines

Comparative architectural evaluation of **Google.Protobuf** (C# runtime + `protoc` generator), **protobuf-net** (Marc Gravell, including AOT `BuildTools`), and **Parquet.SourceGenerator** (PSG).

This study investigates design choices across three serialization engines targeting .NET, identifies where PSG leads, extracts process and architectural mechanisms worth adopting, and records why certain peer patterns (such as runtime-engine registration and per-field OOP polymorphism) are deliberately rejected.

---

## 1. The Design Space: Three Points on the Spectrum

| Dimension | Google.Protobuf | protobuf-net | Parquet.SourceGenerator (PSG) |
|:---|:---|:---|:---|
| **Generated Code** | Hot paths fully inlined (tag-switch loop, `ref struct ParseContext` / `WriteContext`). | Thin: partial `TypeModel` subclasses bridging contracts to the runtime serializer abstractions. | Fully inlined, self-contained (~890–1,543 ELOC/type), calling Parquet.Net columnar primitives directly. |
| **Runtime Library** | Rich: codecs, unknown fields, JSON mapping, reflection descriptor trees, well-known types. | The entire engine (IL-emit / dynamic meta-model). AOT generator merely acts as a bridge. | Minimal (`~5` runtime types: marker attributes and `ParquetOptions`). |
| **Execution Model** | Streaming tag-length-value (TLV) wire protocol. | Streaming TLV wire protocol. | Columnar chunking, vector transpositions, and definition/repetition level shredding (Dremel). |
| **Primary Goal** | Cross-language protocol fidelity + robust message schemas. | Idiomatic C# POCO serialization with minimal emitted footprint. | Maximum columnar throughput (2.5x speedup / 48% memory reduction claims require direct inlining). |

### Where PSG Leads Peer Implementations

PSG is demonstrably ahead of both reference ecosystems in several engineering and governance dimensions:

1. **Emitted API Surface Governance**:
   - PSG enforces `.api.txt` baselines beside every golden model with `PARQAPI001` / `PARQAPI002` build-error gates and semver classification in [`docs/api/LEDGER.md`](./api/LEDGER.md) (see [`17 - Generated Public API Baselines`](./17-GENERATED-API-BASELINES.md) and [`18 - The API Change Contract`](./18-API-CHANGE-CONTRACT.md)). Neither protobuf repository enforces automated compile-time gates against emitted surface drift.
2. **Deterministic Triple-Golden System**:
   - Generated code is verified against a full three-way contract: exact emitted text, public API signatures (`.api.txt`), and Roslyn complexity/ELOC metrics (`.metrics.txt`), bound against a real `CSharpCompilation` in `GoldenCodeGenRegressionTests`. Google.Protobuf tests generated code by compiling and executing its test suite, with no assertion against emitted text drift.
3. **Incremental Pipeline Hygiene**:
   - Strict `EquatableArray<T>` caching discipline across generator pipeline steps, immutable domain models, and aggressive pruning of non-cacheable Roslyn semantic types (see [`03 - Incremental Generator Pipeline`](./03-INCREMENTAL-GENERATOR-PIPELINE.md)).
4. **Empirical Dremel Spike Artifacts**:
   - The findings in [`15 - Nested Types: M0 Spike Findings`](./15-NESTED-TYPES-SPIKE-FINDINGS.md) (identifying Parquet.Net's nested interior-null loss and using PyArrow as an external oracle) represent rigorous verification that exceeds peer documentation.
5. **Target Framework Fast Paths**:
   - Using `#if NET6_0_OR_GREATER` fast paths in emitted code parallels Google.Protobuf’s `GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE`, validating conditional modern framework acceleration without dropping .NET Standard 2.0 consumer compatibility.

---

## 2. Evaluation of Peer Mechanisms: What to Adopt, Modify, or Reject

### 1. Ten-Year Generated-Code / Runtime Compatibility Matrix (`Google.Protobuf`)
* **Peer Pattern**: `Google.Protobuf` maintains `csharp/compatibility_tests/v3.0.0/test.sh`, which downloads historical `protoc` compilers (from 2016 onward) and executes three mixed-mode matrix tests:
  - Old generated code + Old runtime
  - New generated code + Old runtime
  - Old generated code + New runtime
* **PSG Assessment**: **High Priority Adoption.**
  - *Current state*: PSG validates wire-format interop across versions ([`test/CrossVersionInterop`](../test/CrossVersionInterop)) and pins `Parquet.Net` per golden compilation ([`16 - Version And Schema-Evolution Matrix`](./16-VERSION-AND-SCHEMA-EVOLUTION.md)).
  - *The gap*: PSG's emitted code makes ~15 direct calls into `Parquet.Net` primitives (`WriteAllPartsAsync`, `ReadRawAsync`, `ParquetRowGroupWriter`, `DataField`, `ParquetSchema`). If a consumer compiled generated code using PSG v1.0 against Parquet.Net 6.1.0 and later upgrades `Parquet.Net` to 6.2.0 or 7.0 without regenerating, any binary signature drift will trigger runtime `MissingMethodException` or `TypeLoadException`.
  - *Action*: Introduce a matrix test compiling committed historical `.g.cs` fixtures against current and upcoming `Parquet.Net` package builds.

### 2. Comprehensive Corpus Sweep vs Reference Oracles (`protobuf-net`)
* **Peer Pattern**: Protobuf-net’s `src/AotDifferential/Program.cs` sweeps every contract declared in test assemblies and byte-diffs generated output against the reference engine:
  > *"The coverage sweep proves the generated code compiles. That is not the property that matters: every serious bug this generator has had … compiled perfectly and wrote the wrong bytes."*
* **PSG Assessment**: **High Priority Adoption (Quick Win).**
  - *Current state*: PSG tests 5 golden models with exact text diffs, combined with property-based fuzzing.
  - *The gap*: Breadth across diverse property shapes (combinations of nullables, enums, strings, primitives, and dates).
  - *Action*: Add an automated test sweep iterating across all `[ParquetSerializable]` types in `test/` and `benchmarks/`.
  - *Oracle Rule*:
    - For **flat and struct-only models**: byte-diff or round-trip against `ParquetSerializer` as the reference oracle.
    - For **nested collections/lists**: as established in [`docs/15-NESTED-TYPES-SPIKE-FINDINGS.md` §2.6](./15-NESTED-TYPES-SPIKE-FINDINGS.md), `ParquetSerializer` loses nested nulls. Use raw Dremel level assertions and PyArrow/DuckDB as external oracles.

### 3. Schema Metadata Representation: Inlining vs Data Hoisting (`Google.Protobuf`)
* **Peer Pattern**: `Google.Protobuf` avoids code bloat by embedding serialized descriptor bytes and building schema reflection once, reserving code inlining strictly for hot serialization loops.
* **PSG Assessment**: **Corrected & Nuanced Adoption.**
  - *Clarification on `ResolveSchemaField`*: Historical documentation in early drafts suggested `ResolveSchemaField` was emitted multiple times per type. In reality, PR #79 and [`23 - Duplication Measurement`](./23-DUPLICATION.md) consolidated this: `ResolveSchemaField` is emitted **exactly once per generated class** as a private static method in `{ClassName}ParquetExtensions`. Reader entry points simply call it.
  - *Opportunity for Schema Construction*: While Parquet schemas are substantially smaller than Protobuf descriptor trees, procedural emission of `new ParquetSchema(...)` and static `DataField` instantiation across many models produces non-trivial IL overhead. Emitting compact schema metadata descriptors for large compound schemas can reduce generated assembly size without degrading read/write throughput.

### 4. Per-Field-Kind Generator Polymorphism vs Columnar Coordination (`Google.Protobuf`)
* **Peer Pattern**: `protoc` decomposes its C# emitter into an OOP class hierarchy (`csharp_field_base.cc` subclassed into `csharp_primitive_field`, `csharp_repeated_message_field`, `csharp_map_field`). Adding a new field type localizes changes to a single subclass.
* **PSG Assessment**: **Deliberately Rejected / Caution.**
  - In a streaming TLV format (Protobuf), each field is read or written independently in a flat tag-dispatch loop.
  - In a columnar format (Parquet), serialization requires **global multi-pass coordination**:
    1. Schema tree generation (nested fields, logical types, rep/def max levels).
    2. Columnar memory allocation and ArrayPool management.
    3. Row-to-column transposition or direct columnar array passing.
    4. Level shredding across multi-level list and map hierarchies.
    5. String deduplication pool sharing.
  - Scattering columnar assembly logic into per-field polymorphic strategy classes fractures the layout of the columnar loop and harms Roslyn generator maintainability. PSG's modular component structure ([`Emitter/Components/`](../src/Parquet.SourceGenerator/Emitter/Components/): `SchemaComponent`, `ColumnarBatchComponent`, `StringDeduplicatorComponent`, etc.) aligns better with columnar reality.

### 5. Edition-Gated Codegen Features (`Google.Protobuf`)
* **Peer Pattern**: Protobuf "Editions" gate features (such as C# nullable reference types and UTF-8 string views) with edition defaults, decoupling language feature adoption from breaking changes.
* **PSG Assessment**: **Medium Priority Adoption (Target: M3/M4).**
  - PSG will encounter this as it introduces compound types, collection expressions (C# 12+), and optional nullability annotations.
  - *Action*: Introduce explicit generator feature levels or options (e.g. stamped `GeneratedCodeAttribute` metadata and emission level settings) so consumers on older language versions or strict nullability policies can pin generator behavior without breaking changes across updates.

### 6. Closed Model Stance & Defensive Resource Limits (`protobuf-net`)
* **Peer Pattern**: `protobuf-net` enforces that models are strictly closed: unrecognized contracts trigger compile diagnostics rather than silent runtime fallbacks. At runtime, `ParseContext.DefaultRecursionLimit` and `LimitedInputStream` cap input exposure before allocating buffers.
* **PSG Assessment**: **High Priority Adoption (Defensive Hardening).**
  - *The Trap*: As documented in [`docs/15-NESTED-TYPES-SPIKE-FINDINGS.md` §2.3](./15-NESTED-TYPES-SPIKE-FINDINGS.md), reader buffers are sized from file-header `NumValues`. Hostile or corrupted Parquet metadata claiming 2 billion rows can force immediate out-of-memory crashes before reading column chunks.
  - *Action*:
    1. Implement explicit allocation limits and max buffer chunk size constants in generated readers.
    2. Cap compound nesting depth during schema resolution.
    3. Throw deterministic `InvalidDataException` when header metadata exceeds safety boundaries.

---

## 3. Structural Boundary: Why Runtime-Engine Seams Must Not Be Imported

Protobuf-net relies on generated code registering into a heavy runtime model (`TypeModel` / `IProtoSerializer`). This trade-off suits libraries aiming to minimize generated source code volume at the cost of runtime cold-start and abstraction overhead.

For PSG, **this seam architecture is explicitly anti-goal**:
1. Zero runtime reflection and direct columnar hand-offs are necessary to achieve the measured 2.5x speedup and 48% memory reduction.
2. Direct inline calls allow CoreCLR JIT and Native AOT compilers to perform aggressive loop vectorization, dead-code elimination, and escape analysis.
3. The runtime package must remain a zero-dependency metadata marker library.

---

## 4. Action Items & Roadmap Alignment

| Action Item | Source | Target Milestone | Deliverable |
|:---|:---|:---:|:---|
| **Corpus Differential Sweep** | protobuf-net | **M2.5** | New test suite running all test/sample models against `ParquetSerializer` (flat) and PyArrow (nested). |
| **Defensive Metadata Limits (DoS Cap)** | protobuf-net | **M2.5** | Allocation bounds on `NumValues` buffer sizing; `InvalidDataException` on corrupt footers. |
| **Parquet.Net ABI Compatibility Matrix** | Google.Protobuf | **M3** | Integration test executing historical generated `.g.cs` code against multiple `Parquet.Net` versions. |
| **Feature Level & Edition Policy** | Google.Protobuf | **M4** | Explicit dialect/edition dials for emitted C# language features and compound defaults. |
