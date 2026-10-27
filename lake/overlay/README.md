# Lake.Parquet

Apache Parquet for .NET, for table formats. Lake.Parquet is a fork of [Parquet.Net](https://github.com/aloneguid/parquet-dotnet)
by Ivan Gavryliuk, with what Apache Iceberg and DuckLake need from their data files, and correctness and speed
fixes found on the way.

```bash
dotnet add package Lake.Parquet
```

For .NET 10. Pair it with `Lake.Parquet.SourceGenerator` for compile-time serializers (planned; until then the
`Parquet.SourceGenerator` package's generated code builds against Lake.Parquet too).

**Lake.Parquet replaces Parquet.Net.** It keeps Parquet.Net's API and its `Parquet` namespace, so existing code
builds unchanged after swapping the package. A project can reference one or the other, not both.

## What it adds to Parquet.Net

- **Field IDs** on every field (lists, maps and structs too), written and read back.
- **Modular encryption**: AES_GCM_V1 and AES_GCM_CTR_V1, encrypted or signed plaintext footers, per-column keys.
  Interoperates with parquet-cpp in both directions.
- **Statistics engines can trust**: min/max in each type's sort order (strings by code point, unsigned integers,
  UUID bytes, NaN and signed zeros), byte-array and boolean bounds, `column_orders` and `nan_count` in the footer,
  so pyarrow and DuckDB prune on string and binary columns. Timestamp bounds in the column's unit.
- **Typed statistics**: sort order, raw and physical bounds, exactness, decoded as the column's type.
- **Physical values** (`DataField.PhysicalClrType`) and **`RawDataField`**: columns read and written as stored,
  e.g. `TIMESTAMP(NANOS)` as `long` nanoseconds.
- **`FIXED_LEN_BYTE_ARRAY(n)`** byte arrays, **`ParquetReader.Columns`**, options to skip statistics or the header
  check (one request fewer over ranged storage).
- **Fixes**: `List<T?>` keeping nulls and empty lists, `DATA_PAGE_V2` with repetition levels, required maps,
  legacy list shapes, `dictionary_page_offset`.
- **Faster reads**: text and binary columns decode about 2× faster; Zstd pages allocate a third less.

Every change is also proposed upstream, one at a time, by hand. When Parquet.Net has them all, Lake.Parquet has
no reason to exist.

## Versions

Lake.Parquet has its own version numbers. Each release says which Parquet.Net commit it is based on and which
commit of the fork ([rtkelly-labs/parquet-dotnet](https://github.com/rtkelly-labs/parquet-dotnet)) it was built
from, in its release notes and in the assembly's `LakeUpstreamVersion`, `LakeUpstreamCommit` and `LakeForkCommit`
metadata. It is built and released from [Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator/tree/main/lake).

## License

MIT, as Parquet.Net. Copyright (c) 2017 Ivan Gavryliuk; the fork's changes are under the same license.
