# 14 - Parquet Compatibility Matrix

This document defines the compatibility promise for `Parquet.SourceGenerator`.

The promise is not "every Parquet file". Apache Parquet is a broad file format with nested schemas,
repeated fields, maps, optional logical types, page indexes, bloom filters, encryption, and producer-
specific metadata. The generator currently emits strongly typed readers and writers for a deliberate
flat-schema envelope. Files and features outside that envelope must be classified explicitly rather
than being treated as accidentally supported.

## Status Vocabulary

| Status | Meaning |
|:---|:---|
| **Supported** | The generated model or file shape is implemented and belongs in the required regression matrix. |
| **Read-supported** | Generated readers can consume the shape, but generated writers do not emit it as a first-class feature. |
| **Write-supported** | Generated writers can emit the shape, but compatibility with every external consumer is not implied. |
| **Unverified** | The implementation may support the shape, but an independent compatibility test is still required. |
| **Unsupported** | The generator rejects the model or the generated API cannot safely consume the file. |
| **Unknown/future** | No compatibility promise is made until the feature is investigated. |

## Product Variants

| Variant | Underlying API | Current package baseline | Supported consumer targets |
|:---|:---|:---|:---|
| Modern generator | Parquet.Net v6 `DataField` and `Memory<T>` APIs | Parquet.Net 6.1.0 | Consumer projects using the modern package and a compatible .NET runtime |
| Classic generator | Parquet.Net v4/v5 `DataColumn` APIs | Parquet.Net 4.25.0 in package-consumption tests | .NET 8, .NET 9, and .NET Framework 4.7.2 in the current test setup |
| Generator analyzer | Roslyn incremental generator | `netstandard2.0` analyzer assembly | Roslyn hosts compatible with the package's compiler dependency range |

The classic backend is one API family for Parquet.Net 4.x and 5.x. The modern backend targets the
v6 API family. They are separate emitters and do not have identical generated API surfaces or
performance characteristics.

## Generated Model Type Envelope

The following types are accepted by the modern parser allowlist and are the starting point for the
required generated read/write matrix.

| Type family | Status | Notes |
|:---|:---|:---|
| `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` | Supported | Primitive Parquet.Net mappings |
| `float`, `double` | Supported | Floating-point physical types |
| `string` | Supported | UTF-8 string column |
| `byte[]` | Supported | Binary column |
| `System.DateOnly` | Supported | DateTimeDataField at midnight with generated conversion |
| `System.DateTime` | Supported | Millisecond or microsecond configuration |
| `System.TimeSpan` | Supported | Generator-specific conversion to an integer duration representation |
| `System.TimeOnly` | Supported | Generator-specific conversion to an integer time representation |
| `System.Guid` | Supported | Binary Guid representation |
| `decimal` | Supported | Precision and scale are controlled by `[ParquetDecimal]` |
| Enums | Supported | Encoded through the enum underlying integral type |
| `System.ReadOnlyMemory<byte>` | Supported | Modern backend type |
| `System.ReadOnlyMemory<char>` | Supported | Modern backend type |
| `Parquet.File.Values.Primitives.Interval` | Supported | Parquet.Net passthrough mapping |
| Nullable value types and annotated nullable references | Supported | Encoded as optional columns with definition levels |

Nullable reference handling depends on the compilation's nullable context. An annotated reference
(`string?`) is optional; a non-annotated reference (`string`) is required when nullable analysis is
enabled. In an oblivious context, reference types remain conservatively optional.

## Generated Model Shapes Outside The Envelope

| Shape | Status | Current behavior |
|:---|:---|:---|
| `char` | Unsupported | Rejected by `PARQ006` |
| `DateTimeOffset` | Unsupported | Rejected by `PARQ006`; callers should model the instant and offset explicitly |
| `System.Numerics.BigInteger` | Unsupported | Parquet.Net accepts the schema type but its plain encoder cannot safely encode the managed struct; rejected by `PARQ006` |
| `BigDecimal` | Unsupported | The current Parquet.Net public type is not in the generator's supported model envelope |
| Arrays other than `byte[]` | Unsupported | Rejected by `PARQ006` |
| `List<T>`, `Dictionary<TKey,TValue>`, and other collections | Unsupported | Rejected by `PARQ006` |
| Nested user-defined objects | Unsupported | Rejected by `PARQ006` |
| Nested target types | Unsupported | Rejected by `PARQ009` |
| Generic target types | Unsupported | Rejected by `PARQ010` |
| Members without an accessible setter or initializer | Unsupported | Rejected by `PARQ007` |
| Reference types without an accessible parameterless constructor | Unsupported | Rejected by `PARQ008` |
| Positional records | Unsupported | Rejected when no accessible parameterless construction is available |

These restrictions describe generated model support. A valid external Parquet file containing nested
or repeated columns may still be inspected by an external tool, but it is not a supported input for a
generated model unless its required fields fit the supported flat envelope.

## Schema And File Features

| Feature | Status | Contract |
|:---|:---|:---|
| Flat required columns | Supported | Required model members map to required fields |
| Flat optional columns | Supported | Nullable model members map to optional fields |
| Multiple row groups | Supported | Generated readers process row groups independently |
| Column order changed in the file | Supported | Generated readers resolve fields by name after the fast positional check |
| Additional file columns | Supported for reading | Generated readers resolve the fields they know; additional columns are not materialized |
| Missing required generated column | Supported failure | The reader throws a descriptive `InvalidDataException` |
| Missing optional generated column | Unverified | Must be covered by the schema-evolution issue before being promised |
| Nested and repeated fields | Unsupported for generated models | No generated collection or repetition-level model exists |
| Lists and maps | Unsupported for generated models | External files are fixture/conformance inputs, not generated-model inputs |
| Encrypted Parquet files | Unknown/future | No compatibility promise |
| Page indexes and bloom filters | Unknown/future | Metadata features are not currently required for ordinary generated reads |
| Arbitrary producer metadata | Supported if semantically irrelevant | Metadata must not alter supported schema/value interpretation |

## Timestamp And Decimal Contract

| Feature | Status | Contract |
|:---|:---|:---|
| Millisecond timestamps | Supported | `[ParquetTimestamp(ParquetTimestampUnit.Milliseconds)]` |
| Microsecond timestamps | Supported | `[ParquetTimestamp(ParquetTimestampUnit.Microseconds)]` |
| Nanosecond timestamps | Unsupported | Parquet.Net's supported DateTime representation does not provide this path |
| Decimal precision up to 38 | Supported | Precision must be greater than or equal to scale |
| Decimal scale | Supported | Must be declared consistently between model and file |
| Decimal precision/scale mismatch | Supported failure | The reader/writer must fail rather than silently rescale values |

## Writer Options And Encodings

The modern writer exposes these compression choices through `ParquetSerializerOptions`:

| Option family | Current values | Compatibility status |
|:---|:---|:---|
| Compression | None, Snappy, Gzip, Lz4, Brotli, Zstd | Required generated matrix; independent consumer verification pending |
| Column encoding hint | Default, Dictionary, DeltaBinaryPacked, ByteSplitStream | Required where the type and Parquet.Net support the hint |
| Dictionary threshold | Runtime configurable | Writer behavior; not a schema compatibility guarantee |
| Dictionary sample size | Runtime configurable | Writer behavior; not a schema compatibility guarantee |
| String deduplication | Runtime read option | In-memory identity/allocation behavior only; file values must not change |
| Row-group size | Runtime configurable | Required to preserve row-group ordering and value semantics |

The encoding enum is an implementation hint, not a promise that every Parquet consumer supports
every encoding for every physical type. Interoperability tests must validate the actual combinations
that the underlying writer accepts.

## Format And Producer Matrix

| Producer or consumer | Current status | Required follow-up |
|:---|:---|:---|
| Modern generated code with Parquet.Net 6.1.0 | Supported baseline | Generated round-trip and type matrix |
| Classic generated code with Parquet.Net 4.25.0 | Supported baseline | Legacy package-consumption matrix |
| PyArrow format setting 1.0 | Fixture coverage exists | Pin exact PyArrow version and validate both directions |
| PyArrow format setting 2.6 | Fixture coverage exists | Pin exact PyArrow version and validate both directions |
| PyArrow reading generated output | Verified in ExternalInterop CI | Pinned PyArrow canonical fixture and generated C# output |
| DuckDB reading and writing supported flat schemas | Verified in ExternalInterop CI | Pinned DuckDB CLI and generated canonical fixture |
| Apache Parquet tooling | Unverified | Required conformance issue |
| Older Parquet.Net output read by current generated code | Partially verified | Required version matrix |
| Current generated output read by legacy consumers | Partially verified | Required version matrix |
| Public LFS benchmark datasets | Provenance and runtime coverage | Treat hashes as provenance, not semantic compatibility |

The `test/data_csharp/v3` directory is a fixture-directory name, not evidence that the files were
written by Parquet.Net v3. The committed files record Parquet.Net 6.0.3 in their footer metadata;
the current C# fixture generator references Parquet.Net 6.1.0 for CI regeneration. Exact committed
fixture provenance is recorded in [`test/data/fixture-manifest.json`](../test/data/fixture-manifest.json).
There is currently no generated `test/data_csharp/v4` directory.

## Compatibility Definitions

### Backward compatibility

The current generated reader can read files written by an older producer when the file schema is
inside this document's supported envelope and the older producer's encoding and compression are
supported by the current Parquet.Net dependency.

### Forward compatibility

The current generated reader can read files written by a newer producer only when the newer file uses
features already inside the documented envelope. Unknown future features are not silently accepted as
forward-compatible.

### Schema evolution

Column reordering and additional file columns are supported. Missing required generated columns fail
descriptively. Missing optional generated columns are not part of the compatibility promise until they
have an explicit test and behavior decision.

### Semantic compatibility

Compatibility means equivalent schema meaning and logical values. It does not require byte-identical
files, identical page boundaries, identical statistics encoding, or identical metadata ordering across
independent writers.

## Evidence And Test Ownership

| Claim | Evidence required |
|:---|:---|
| Generated code round-trips | Deterministic C# runtime tests |
| External producer can be read | Fixture plus semantic comparison through generated reader |
| Generated output is externally readable | External engine reads generated output and validates semantics |
| File is structurally valid | Independent Apache Parquet tooling |
| Fixture bytes are unchanged | SHA-256 provenance test |
| Version compatibility | Producer/consumer matrix with exact versions |
| Large/random input safety | Property-based and corruption tests |

Hash regression tests protect provenance and intentional byte-level determinism. They are not a
substitute for semantic interoperability tests.

## Related Work

- [Issue #161](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/161) tracks this contract.
- [Issue #162](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/162) builds the semantic harness.
- [Issue #163](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/163) establishes the fixture corpus.
- [Issue #164](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/164) adds the generated type matrix.
- [Issue #165](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/165) adds PyArrow interoperability.
- [Issue #166](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/166) adds DuckDB interoperability.
- [Issue #167](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/167) adds Apache conformance validation.
- [Issue #168](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/168) adds version and schema-evolution testing.
- [Issue #169](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/169) adds property-based and negative testing.
- [Issue #170](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/170) provides the `/regression` execution modes.
