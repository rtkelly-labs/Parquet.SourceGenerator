# Upstream Dependency Limitations

## Upstream Fork & Tracking Pipeline (`rtkelly-labs/parquet-dotnet`)

To prototype, verify, and stage enhancements addressing the architectural limitations below, an official fork is maintained at:
**[rtkelly-labs/parquet-dotnet](https://github.com/rtkelly-labs/parquet-dotnet)** (forked from `aloneguid/parquet-dotnet`).

### Policy & Contribution Guidelines
Upstream `aloneguid/parquet-dotnet` maintains an explicit policy against LLM-generated pull requests, issues, and discussions, welcoming users to maintain independent forks. Consequently:
- `rtkelly-labs/parquet-dotnet` serves as the primary repository for staging, validating, and testing fixes against `Parquet.SourceGenerator`.
- Any pull requests submitted upstream to `aloneguid/parquet-dotnet` must be authored, reviewed, and submitted manually by human maintainers.

### Targeted Workstreams
1. **Security & Bounded Allocations**: Add bounded list/binary reading to `ThriftCompactProtocolReader` and `ParquetReaderOptions.MaxDecompressedPageSize` to eliminate untrusted allocation amplification at the source.
2. **Nullable Read Optimization**: Add zero-definition-level fast path to `ParquetRowGroupReader.ReadRawAsync<T>` when column chunk `NullCount == 0`.
3. **CRC32 Checksum Validation**: Implement optional page header CRC32 validation during page decompression.
4. **UTF-8 Byte Spans**: Expose raw byte-span read surfaces (`ReadOnlyMemory<byte>`) for UTF-8 columns to eliminate intermediate `char[]` / `string` allocations.
5. **Exact Integer-Backed Decimal Decode**: Replace `(decimal)Math.Pow(10, -scale)` in `ParquetPlainEncoder` (decode and statistics) with an exact power of ten, for example `new decimal(1, 0, 0, false, (byte)scale)`, so decimals are exact on .NET 11. See "Parquet.Net Integer-Backed Decimals Are Inexact On .NET 11" below.

---

## Parquet.Net 6.1.0 Nullable Reads

`ParquetRowGroupReader.ReadRawAsync<T>` requires definition-level memory whenever `DataField.MaxDefinitionLevel > 0`. This prevents the generator from implementing the `NullCount == 0` definition-level bypass for nullable fields.

Track the limitation and future upstream fixes in [issue #150](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/150). Scan Parquet.Net for a safe non-nullable read path or an API that permits omitting definition-level output before revisiting the optimization.

## Parquet.Net Integer-Backed Decimals Are Inexact On .NET 11

Parquet.Net 6.x (and 4.25) decodes an INT32- or INT64-backed decimal as
`unscaled * (decimal)Math.Pow(10, -scale)` (`ParquetPlainEncoder.Decode` for columns,
`TryDecodeDecimal` for statistics). That was exact only because, up to .NET 10, the `double` to
`decimal` conversion rounded to 15 significant digits, turning the `double` nearest 10^-scale back
into the exact power of ten. .NET 11 keeps the `double`'s value:

```
.NET 10.0.12:  (decimal)Math.Pow(10,-2) = 0.01                            1999 * it = 19.99
.NET 11 rc3:   (decimal)Math.Pow(10,-2) = 0.0100000000000000002081668171  1999 * it = 19.990000000000000416125467383
```

Found by CI's .NET 11 job (#743), which failed 38 decimal tests. The error is not only in far digits:
at scale 10, an 18-digit unscaled value decodes 36 units of the column's own scale away, a
well-formed `decimal(18,10)` that cannot be told from a correct one. At scales of about 14 and above
the factor is numerically exact at decimal's 28 places, but values come back holding 28 places
(`19.9900000000000000000000000000`) instead of the column's scale. Fixed-length and byte-array
decimals decode through `BigInteger` and are unaffected; scale 0 is unaffected.

**Workaround here.** Generated readers check each decimal chunk's physical type and, for INT32 and
INT64, pass the decoded values through `ParquetDecimalScale` (Runtime package), which divides by
the same factor, rounds to the unscaled integer and rescales exactly, at the column's scale. It is
a no-op where the factor is exact, so before .NET 11 it costs one lookup per column read. The fuzz
oracle (`IndependentParquetEngine.DecimalCodec`) applies the same correction, because it reads
through Parquet.Net too. Decimal statistics are not corrected, because decimals are excluded from
row-group pruning. Any other Parquet.Net caller on .NET 11, including a consumer reading with
`ParquetSerializer`, still gets the inexact values.

**Upstream fix.** Use an exact power of ten (workstream 5). Tests: `DecimalScaleRoundTripTests`
(matrix and seeded properties over INT32, INT64 and fixed-length shapes, through the POCO and
batch readers) and `ParquetDecimalScaleTests` (the arithmetic, driven with the .NET 11 factor on
any runtime).

## Parquet.Net 4.25.0 Legacy String Reads

The legacy `ParquetRowGroupReader.ReadColumnAsync` API materializes a complete `DataColumn` before
returning it. It exposes no bounded string read, raw page buffer, physical byte-length metadata, or
callback that the generated reader can use before materialization. Consequently,
`ParquetSerializerOptions.MaxStringLengthBytes` is enforced immediately after the materialized
column is returned on the classic backend; it is a deterministic content-validation limit, not an
allocation-prevention guarantee for legacy string columns.

The modern Parquet.Net 6 backend can enforce the corresponding limit at its lower-level read
boundary. Closing the legacy gap requires an upstream bounded/raw column-read API (or a complete
page reader in this repository), neither of which is appropriate to add as part of the 0.1 release.
The generated legacy contract and tests deliberately preserve this ordering so the limitation is
visible rather than implying that the option protects against pre-read string allocation.

## Parquet.Net 4.25.0 Column I/O Allocates Whole Columns

Parquet.Net 4.25.0 is the last line that supports .NET Framework (`net472`). Its column API is
`ParquetRowGroupReader.ReadColumnAsync(DataField) → DataColumn` and
`ParquetRowGroupWriter.WriteColumnAsync(DataColumn)`. Every read allocates a complete array for the
column and row group, and every write requires one. There is no caller-buffer equivalent of the
v6 `ReadAsync<T>(DataField, Memory<T>)` / `WriteAsync<T>(DataField, ReadOnlyMemory<T>)`.

Consequences for the classic backend, which reaches API parity with the modern backend under
[docs/49](docs/49-LEGACY-PARITY-490.md) (#490):

- The generated API can match the modern backend, but allocation cannot. The ArrayPool and
  caller-buffer strategy does not carry over, so every column read allocates its own array.
- Columnar input (`<Model>ColumnarBatch`) is copied once per column into the array `DataColumn`
  requires.
- `DataColumn` has no `ReadOnlyMemory<byte>` / `ReadOnlyMemory<char>` representation, so those
  member types (PARQ011) need a `byte[]` / `string` conversion at one copy per value.
- Row-group statistics (`GetStatistics`) return `object` min/max values, costing one unbox per
  column per row group.

The upstream change that would remove the allocation gap is a caller-buffer read and write API
(`Memory<T>` / `ReadOnlyMemory<T>` overloads) on the 4.x line. That is unlikely, since 4.x is not
actively developed. Revisit if a net472-capable Parquet.Net release ever adds one, or if 6.x
regains a `netstandard2.0` target.

## Parquet.Net 6.1.0 And 4.25.0 Size Allocations From Untrusted Footer Counts

`ParquetSerializerOptions.MaxAllocationValues` is a per-column **count** of values (default
10,000,000), not a memory budget (#361). The modern generated reader sizes its pooled buffers from `RowGroup.num_rows` (flat columns) and
`ColumnChunk.num_values` (list and struct leaves) in the footer before it reads a page, so a file of
about 1 KB whose footer declares ten million rows for the nine-column `OrderEvent` model asks for
roughly 1 GB of nominal element storage (before `ArrayPool` rounding), and the parallel read path can
rent that per worker. `MaxAllocationBytes` (below) now rejects that read before anything is rented. The legacy reader validates `ColumnChunk.num_values` and then delegates the
whole-column allocation to Parquet.Net through `ReadColumnAsync`. The count is the same for a
`bool` and a `Guid` and is applied per column, so wider models multiply it.

**Why the file's own bytes cannot close it.** The issue proposes rejecting a column whose
`num_values` could not be encoded in its `total_uncompressed_size`. No such bound exists in the
format: RLE and dictionary-index encodings, all-null and constant columns, and delta encodings
legitimately carry millions of values in a few bytes, so the check would reject valid files. The only
sound bound is the one the caller chooses.

**What is bounded here.** The decompression guard (#374) now refuses footer list counts and byte
lengths larger than the footer itself, and a row group count above `MaxRowGroupCount`, before
Parquet.Net allocates for the footer. The legacy read path bounds row counts and column value counts
with `MaxAllocationValues` (#362). Neither relates the count to a byte size.

**The byte budget (#361).** `ParquetSerializerOptions.MaxAllocationBytes` (default 256 MiB per row group)
multiplies a row group's declared row count by the model's per-row buffer size and refuses before
renting, in the modern readers and the classic one; list columns are added to the running total as their chunks are read. It is per
row group and, in a parallel read, per worker, so a host that reads untrusted files in parallel should
lower it by its worker count. The result array a read returns is bounded by `MaxAllocationValues`
only. In the classic backend the budget is checked per row group and again per column against its
declared value count, because Parquet.Net allocates the whole column from that count.

**Upstream.** Parquet.Net sizes `List<T>` and `byte[]` from counts it has not checked against the
bytes that remain (`ThriftCompactProtocolReader.ReadListHeader`, `ReadBinary`), and its `SkipField`
for a struct reads field headers without skipping their values. A bounds check in those two readers
would remove the footer-side amplification at its source and make the footer walk in the guard
unnecessary; the walk follows declared compact types, so a footer that mis-declares the type of a field
it does not know can still hide a count from it.

## Parquet.Net 6.1.0 Page Checksums Are Not Verified

Parquet's `PageHeader.crc` is optional, and Parquet.Net neither writes nor verifies it. A single
flipped bit inside an encoded data page therefore decodes to a different but perfectly well-formed
value, and a flipped byte in a column chunk's metadata can send the reader at a different — still
valid — page offset. The property-based corruption suite
(`test/Parquet.SourceGenerator.Tests/PropertyBased/CorruptedParquetTests.cs`) found both, so its
random-bit-flip cases assert only that the read terminates without exhausting memory; structural
damage (truncation, bad magic, bad footer length) is still required to be rejected outright.

Nothing in the generated code can close this gap: detection has to happen where the page is decoded.
Revisit if Parquet.Net gains CRC emission and verification.

## Absent Optional Columns Are Rejected By The Generated Reader — FIXED

Recorded here for history; this was never an upstream limitation and is no longer true. A valid
Parquet file omitting an optional column used to be rejected with
`ParquetException: '<column>' does not exist in this file`, because `ResolveSchemaField` handled
the fallback but the read path still called `GetStatistics`/`ReadAsync` with a field the file did
not contain. Fixed in #168: a missing optional column now materialises as all-null on every
emitted read path. Covered by
`SupportedSchemaPropertyTests.AbsentNullableColumnMaterialisesAsNulls`.
## Parquet.Net 6.1.0 Has No UTF-8 Byte Surface For String Columns

Issue [#143](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/143) proposed keying the
L1 string cache on raw UTF-8 spans (`ReadOnlySpan<byte>`) so a cache hit skips both the string
allocation *and* the UTF-8 decode. Parquet.Net 6.1.0 exposes no such surface. Probed directly
against the packaged assembly:

- `ParquetRowGroupReader.ReadRawAsync<T>` is constrained to `where T : struct`, so `byte[]` cannot
  be requested through it at all.
- For a UTF8-annotated `BYTE_ARRAY` column the reader's resolved `DataField.ClrType` is
  `System.ReadOnlyMemory<char>` — the library has already decoded UTF-8 to UTF-16 before any
  caller-visible buffer exists. `ReadRawAsync<ReadOnlyMemory<char>>` is therefore the lowest-level
  access available.
- `ParquetOptions.PreferUntypedByteArray` (a static property) does **not** change this. With it set,
  the string field's `ClrType` stays `ReadOnlyMemory<char>` and
  `ReadAsync(field, Memory<byte[]>, …)` throws
  `Field "…" (System.ReadOnlyMemory\`1[System.Char]) is not compatible with type
  System.ReadOnlyMemory\`1[System.Byte]`. The flag only applies to `BYTE_ARRAY` columns that carry
  no UTF8 logical annotation.
- Even the raw path is not allocation free upstream: over 32,561 rows holding six distinct values,
  `ReadRawAsync<ReadOnlyMemory<char>>` produced 32,561 **distinct** backing `char[]` arrays. The
  library allocates one array per row regardless of the caller's API choice, matching the finding
  in PR #208.
- `ReadRawAsync` still demands a definition-levels buffer whenever `MaxDefinitionLevel > 0`
  (see the nullable-reads section above), and returns the value lane **packed** — nulls are absent
  from the value array and must be spread back over the row lane using the definition levels.

What *is* reachable, and what this repository now does, is to read string columns through
`ReadRawAsync<ReadOnlyMemory<char>>` and intern each value with a span-keyed table. That removes
the per-row `string` instantiation (roughly half of the managed read allocation on categorical
data) but cannot remove the per-row `char[]` that Parquet.Net allocates underneath.

The upstream API that would close the gap is a `ReadRawAsync<ReadOnlyMemory<byte>>` (or a
`ReadUtf8Async(DataField, Memory<ReadOnlyMemory<byte>>, …)`) that hands out slices of a single
pooled page buffer. A byte-keyed `Utf8StringDeduplicator` prototype is preserved in
[Git history](https://github.com/rtkelly-labs/Parquet.SourceGenerator/blob/898a9ed0d31eedba7262b83d05530bc19432b07b/test/Parquet.SourceGenerator.Tests/Utf8StringDeduplicatorPrototypeTests.cs).
Its tests were removed from the required suite in #614 because the generated reader never used
it: feeding it would require round-tripping through `string` and defeat the purpose. An upstream
byte-level API would enable integration and behavioural tests of the emitted reader.
## Parquet.Net 6.1.0 Column Decode Always Allocates

`ParquetRowGroupReader.ReadAsync` and `ReadRawAsync<T>` both decode a column chunk into an
internally allocated array and then copy into the caller's `Memory<T>`. Measured on a single
`double` column, 20,000 rows, Apple M1 / .NET 9: ~7.8 bytes per row allocated on both entry
points, i.e. one array the size of the decoded data per column per row group — identical with and
without a definition-levels buffer supplied.

The consequence for the struct-of-arrays batch API ([issue #147](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/147))
is that "zero allocation" can only mean zero *domain object* allocation. Genuinely allocation-free
reads need an upstream decode-into-caller-buffer entry point.
## Parquet.Net 6.1.0 Write-Side Column Shapes

Verified against the decompiled `Parquet.ParquetRowGroupWriter` from the 6.1.0 package while
designing the direct columnar handoff (issue #137). These constrain what a zero-copy columnar write
API can accept.

1. **`WriteAsync<T>` is constrained `where T : struct`.** There is no generic overload accepting a
   reference-typed column, so a `string` column's zero-copy shape is
   `ReadOnlyMemory<ReadOnlyMemory<char>?>` and a binary column's is
   `ReadOnlyMemory<ReadOnlyMemory<byte>?>`, not `ReadOnlyMemory<string?>` /
   `ReadOnlyMemory<byte[]?>`. The generated columnar batch struct exposes the memory-of-memory shape
   for that reason, which is less obvious than the issue's original sketch but is the only form that
   copies nothing.

2. **The convenience overloads rent and copy.** `WriteAsync(DataField, IReadOnlyCollection<string?>)`
   and `WriteAsync(DataField, IReadOnlyCollection<byte[]?>)` each rent a
   `ReadOnlyMemory<...>?[]` from `ArrayPool.Shared`, convert every element into it, and delegate to
   the generic overload. A columnar API routed through them would reintroduce exactly the per-write
   rental it exists to remove, so the generator does not use them.

3. **`WriteAllPartsAsync<T>` is the only way to supply definition levels explicitly**, and it is
   likewise `where T : struct`. A nullable value column therefore has to be presented as packed
   non-null payloads plus a separate `ReadOnlyMemory<int>` of definition levels — there is no
   overload that takes a `ReadOnlyMemory<T?>` and derives the levels without an intermediate buffer.
   This is why the generated batch exposes two members per nullable value column rather than one.

None of these are bugs; they are the shape of the low-level API. They are recorded here because each
one closed off a friendlier API design during #137, and a future Parquet.Net version relaxing any of
them would let the generated surface get simpler.
## Apache.Arrow 23.0.0 — Native AOT verification

The conditionally emitted Arrow ingestion bridge ([issue #177](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/177))
is exercised by `test/Parquet.SourceGenerator.AotTest`, which references Apache.Arrow, publishes
with `PublishAot`, and executes the resulting native binary. The check writes an Arrow
`RecordBatch` through the generated bridge and reads it back through the generated POCO reader.
The CI gate rejects new ILCompiler warnings outside the existing Parquet.Net warnings, so this is
evidence for the shipped bridge and its current Apache.Arrow 23.0.0 dependency surface.

This is a verification result, not a promise that every Apache.Arrow API is AOT-safe. The bridge
continues to support only the explicitly mapped flat-array types below; unsupported Arrow array
families are rejected before writing.

## Apache.Arrow 23.0.0 — no zero-copy `ReadOnlyMemory<T>` view over a value buffer

`ArrowBuffer` exposes `Memory` (`ReadOnlyMemory<byte>`) and `Span`, and `PrimitiveArray<T>` exposes
`Values` (`ReadOnlySpan<T>`), but there is no typed `ReadOnlyMemory<T>` accessor — and the BCL has no
`MemoryMarshal.Cast` for `Memory<T>`. Parquet.Net's `WriteAsync<T>` takes `ReadOnlyMemory<T>`, so the
generated bridge carries its own `MemoryManager<T>` that reinterprets the byte buffer
(`MemoryMarshal.CreateSpan` + `MemoryMarshal.Cast`), guarded to `NET6_0_OR_GREATER` with a copying
fallback below that. A typed `ArrowBuffer.Memory<T>()` upstream would delete that helper.

## Apache.Arrow 23.0.0 — `LargeBinary` / `LargeUtf8` have no 64-bit offset array types in scope

The bridge rejects `LargeUtf8` (as #177 requires) and also `LargeBinary`, because consuming 64-bit
offsets would need a separate materialization path. Revisit if Apache.Arrow grows a uniform
offset accessor across `BinaryArray` and `LargeBinaryArray`.
