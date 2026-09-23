# 16 - Version And Schema-Evolution Matrix

[Document 14](14-COMPATIBILITY-MATRIX.md) declares what this generator promises. This document
records how that promise behaves across *producer versions* and *schema differences*, and where the
evidence for each claim lives.

The executable form of everything below is
[`test/Parquet.SourceGenerator.Tests/VersionAndSchemaEvolutionMatrixTests.cs`](../test/Parquet.SourceGenerator.Tests/VersionAndSchemaEvolutionMatrixTests.cs).
Each cell is declared — producer, producer version, consumer, schema case, expected outcome — and
the test theory is driven off the declaration, so a cell cannot quietly stop being exercised. The
producer and format versions in the generated report are read out of each file's own footer
(`FileMetaData.CreatedBy`, `FileMetaData.Version`), not copied from a fixture manifest.

## Schema-Evolution Behaviour

This is the contract. It is what the generated readers do, in every emitted entry point
(sequential, parallel and streaming), on both the modern and the classic backend.

| Difference between file and model | Behaviour | Rationale |
|:---|:---|:---|
| Column order differs | Resolved by name | A positional fast path is tried first; a miss builds a name index once per read and reuses it |
| File carries columns the model does not declare | Ignored | The reader materialises only the columns the model asks for |
| File omits an **optional** column the model declares | Column reads as all-null | A producer dropping or not-yet-adding an optional column is ordinary schema evolution; this costs no page read, decompression or decoding |
| File omits a **required** column the model declares | `InvalidDataException` naming the column | Silently defaulting a required column would hide a genuine schema mismatch |
| Column name matches but the type is incompatible | Underlying Parquet.Net read failure | The generator does not attempt type coercion or widening |
| Model requires a column that exists only as a nested or repeated group | `InvalidDataException` naming the column | Nested shapes are outside the flat envelope; the reader rejects rather than half-reads them |
| File carries unknown footer key/value metadata | Ignored | Producer metadata must not alter interpretation of a supported schema |
| File exceeds defensive bounds (`MaxAllocationValues`, `MaxRowGroupCount`, `MaxNestingDepth`) | `InvalidDataException` naming the violated bound | Bounds are evaluated prior to allocation or recursion to prevent DoS attacks |
| Malformed or corrupted repetition/definition levels | `InvalidDataException` describing stream corruption | Prevents buffer overruns and unhandled index exceptions on untrusted streams |

Missing-optional-column handling is the behaviour this document introduced; before it, an absent
optional column reached Parquet.Net as a column lookup for a field the file did not contain.

### Outcome vocabulary

The matrix records one of three outcomes per cell. "Threw something" is deliberately not one of
them — an unexpected exception type, or a message that does not name the column at fault, fails the
cell.

| Outcome | Meaning |
|:---|:---|
| `Compatible` | Every value the model declares came back unchanged |
| `CompatibleWithNulls` | The file omits optional columns the model declares; those read as null and everything the file does carry is unchanged |
| `RejectedWithClearError` | A typed `InvalidDataException` naming the column that could not be satisfied |

## Producer / Consumer Axis

| Producer | Version | How it is exercised |
|:---|:---|:---|
| Generated writer (modern backend) | Parquet.Net 6.1.0 | Written in-process during the test run |
| Parquet.Net (older) | 6.0.3 | Committed fixtures under `test/data_producers/parquet-net-6.0.3` |
| PyArrow, format 1.0 | 25.0.0 | Committed fixtures under `test/data/v1` |
| PyArrow, format 2.6 | 25.0.0 | Committed fixtures under `test/data/v2` (parquet-format v2 footer) |
| DuckDB | 1.5.4 | `benchmarks/data/tpch_lineitem_sf001.parquet` |
| Generated writer (modern package) | Parquet.Net 6.1.0 | `test/PackageConsumption`, CI cross-version step |
| Generated writer (classic package) | Parquet.Net 4.25.0 | `test/PackageConsumptionLegacy`, CI cross-version step |

| Consumer | How it is exercised |
|:---|:---|
| `From(stream).ToArrayAsync()` (sequential) | In-solution matrix theory |
| `From(buffer).Parallel().ToArrayAsync()` | In-solution matrix theory |
| `From(stream).AsAsyncEnumerable()` | In-solution matrix theory |
| Classic backend reader on Parquet.Net 4.25 | CI cross-version interop step |
| Modern backend reader on Parquet.Net 6.1 | CI cross-version interop step |

Every reader entry point emits its own copy of the schema resolver, which is why each appears in
the matrix rather than being assumed equivalent to the sequential one.

## Cross-Version Interoperability

A single test assembly can reference exactly one Parquet.Net, so nothing in the solution can prove
that a file written against 6.x is readable by a consumer pinned to 4.25. That leg is covered by the
two package-consumption projects, which reference different Parquet.Net versions and compile the
same shared model, [`test/CrossVersionInterop/InteropModels.cs`](../test/CrossVersionInterop/InteropModels.cs):

- `PackageConsumption --write-interop <path>` → `PackageConsumptionLegacy --read-interop <path>`
- `PackageConsumptionLegacy --write-interop <path>` → `PackageConsumption --read-interop <path>`

The reader model (`InteropRowEvolved`) is deliberately one version ahead of the writer's
(`InteropRow`): the same columns in a different order, plus two optional columns the file cannot
contain. So the handshake tests name-based resolution and absent-optional-column handling *across
the version boundary*, not a bare round-trip. Both directions run in CI's
"Verify Cross-Version Interoperability" step and append their result to the matrix report.

## Forward Compatibility

Forward compatibility here means what document 14 defines: a newer file is readable when it stays
inside the documented envelope. Concretely, the following are supported forward cases and are
covered by the matrix:

- A newer producer added optional columns the consumer does not know about → ignored.
- A newer producer emits the consumer's columns in a different order → resolved by name.
- A newer producer writes parquet-format v2 footers and data page v2 → read (PyArrow 2.6 fixtures).

The following are **not** forward compatibility and fail rather than degrade:

- A newer producer dropped a column the consumer requires.
- A newer producer replaced a flat column with a nested or repeated group.
- Any Parquet feature outside the envelope in document 14.

## Recorded Results

The matrix writes `compatibility-matrix.json` and `compatibility-matrix.md` to
`PARQUET_COMPAT_MATRIX_OUTPUT` (defaulting to a directory beside the test binaries). CI sets that
variable and uploads the directory as the `compatibility-matrix` artifact, so each run leaves a
record keyed by producer, producer version, consumer, consumer version, format version and schema
case — not just a pass/fail.

## Known Gaps

- **Data page v2 on the write side.** Parquet.Net 6.1 writes data page v1; the v2 read path is
  covered by the PyArrow 2.6 fixtures only. There is no supported writer knob to emit v2 pages, so
  generated-writer → v2-page cells cannot exist yet.
- **Parquet.Net 4.x/5.x as a *producer* inside the solution.** Only the CI cross-version step covers
  it, because the in-solution test assembly is pinned to 6.1.
- **Nested and repeated shapes.** Covered only as rejection cases. Promoting them requires the
  nested-model work, not a matrix change.
- **Type widening** (`int32` file column into an `int64` model member, and similar) is not attempted
  and is not declared anywhere as supported.
