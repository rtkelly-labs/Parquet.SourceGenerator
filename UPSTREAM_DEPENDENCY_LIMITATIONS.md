# Upstream Dependency Limitations

## Parquet.Net 6.1.0 Nullable Reads

`ParquetRowGroupReader.ReadRawAsync<T>` requires definition-level memory whenever `DataField.MaxDefinitionLevel > 0`. This prevents the generator from implementing the `NullCount == 0` definition-level bypass for nullable fields.

Track the limitation and future upstream fixes in [issue #150](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/150). Scan Parquet.Net for a safe non-nullable read path or an API that permits omitting definition-level output before revisiting the optimization.

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
pooled page buffer. `Utf8StringDeduplicator` in
`test/Parquet.SourceGenerator.Tests/Utf8StringDeduplicatorPrototypeTests.cs` is a tested prototype
of the byte-keyed table that would sit behind it; it is deliberately not wired into the generated
reader, because feeding it would require round-tripping through `string` and defeat the purpose.
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
## Apache.Arrow 23.0.0 — Native AOT readiness unproven

The conditionally emitted Arrow ingestion bridge ([issue #177](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/177))
has **not** been exercised through the `AotTest` harness. `Parquet.SourceGenerator.AotTest` deliberately
does not reference Apache.Arrow, so the native publish CI job proves nothing about it either way.

The emitted bridge itself is reflection-free — it is ordinary generated text calling Apache.Arrow's
public API — but Apache.Arrow's own AOT/trimming posture is unverified here. Until someone runs a
`PublishAot` harness that references Apache.Arrow and reports the ILCompiler warnings, treat the
bridge as **AOT-unsupported**.

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
