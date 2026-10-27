# 0.1.0

First release of Lake.Parquet, for .NET 10. Fork commit
[380ba10](https://github.com/rtkelly-labs/parquet-dotnet/commit/380ba1099f6a0758f9024903f470644fbf920a18), based on
Parquet.Net 6.1.1-pre.2 ([658e1ca](https://github.com/aloneguid/parquet-dotnet/commit/658e1cafebdd1d80d6833c40b0f387d0769898c8)).

What it adds to Parquet.Net is in [overlay/README.md](overlay/README.md); each change, with its tests and measured
effect, in the fork's [upstream review guide](https://github.com/rtkelly-labs/parquet-dotnet/blob/ccr-87fadcf9-bxi9wr/docs/upstream-review.md).

Differences a Parquet.Net user notices:

- Package `Lake.Parquet`, assembly `Lake.Parquet.dll`, `net10.0` only; namespace `Parquet` as before.
- Files say `created_by: Lake.Parquet version …`.
- Statistics of `INT32`/`INT64` decimals come back as `decimal` and unsigned integers as their unsigned type
  (Parquet.Net returned the stored integers).
- Old-style list groups (`array`, `<name>_tuple`, several fields) decode as the format's backward-compatibility
  rules say: as lists of structs.
