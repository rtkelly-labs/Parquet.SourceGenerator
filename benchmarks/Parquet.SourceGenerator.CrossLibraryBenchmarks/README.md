# Cross-library read benchmarks

Parquet.SourceGenerator against the other .NET Parquet readers, on the three provenanced datasets in
`benchmarks/data` (TPC-H LineItem 60k rows, Adult Census 33k, Diamonds 54k).

| Comparand | Package | Engine underneath |
|---|---|---|
| `ParquetNet` | Parquet.Net 6.1.0 | pure managed (the generator's decoder) |
| `SourceGenerator` | this repository | pure managed, on Parquet.Net |
| `ParquetSharp` | ParquetSharp 25.0.1.1 | Arrow C++ `parquet` (P/Invoke) |
| `DuckDb` | DuckDB.NET.Data.Full 1.5.6.1 | DuckDB (C++) `read_parquet` |
| `Polars` | Polars.NET 1.0.0 | Polars (Rust) `polars-parquet` |

The comparands come from NuGet; their source and that of the non-.NET engines is worth reading
for their decode paths (Arrow C++ `cpp/src/parquet`, arrow-rs `parquet`, Polars
`crates/polars-parquet`, DuckDB `extension/parquet`).

## Where the fixes go: Lake.Parquet

Gaps this suite finds in Parquet.Net's decoder are fixed in our fork,
[rtkelly-labs/parquet-dotnet](https://github.com/rtkelly-labs/parquet-dotnet), published as
**`Lake.Parquet`**: upstream unchanged plus only the changes evaluated against it, each with a record
in the fork's [`docs/changes/`](https://github.com/rtkelly-labs/parquet-dotnet/tree/master/docs/changes)
that carries its numbers from this suite and its upstream status. Because it is renamed into the
`Lake.Parquet` namespace and assembly, it can be referenced beside Parquet.Net in this one project, so
it joins the table as a `LakeParquet` comparand, measured against `ParquetNet` in the same run, once
the first version is published. See UPSTREAM_DEPENDENCY_LIMITATIONS.md for the fork's rules.

## Two tiers

- **Typed rows** (`TpchTypedRead`, `CensusTypedRead`, `DiamondsTypedRead`): materialise the whole
  file into the same record type, single threaded. ParquetSharp uses its logical column readers
  and DuckDB its `DbDataReader`, each hand-mapped; Polars uses its `Rows<T>()` mapper. This is the
  question this library answers: file → my objects.
- **Columnar** (`ColumnarReadBenchmark`): decode every column into each library's own columnar
  form and stop (generator `AsBatches`, Parquet.Net `RawColumnData`, Arrow record batches for
  ParquetSharp and DuckDB, a native DataFrame for Polars). Closest to comparing decoders. `_1T`
  pins the native engines to one thread; `_AllCores` leaves them alone.

Every comparand reads from the file path (page cache warm), because DuckDB has no in-memory buffer
source. The existing suite's `MemoryStream` numbers are therefore not directly comparable.

## Caveats

- `Allocated` is managed memory only. ParquetSharp, DuckDB and Polars decode into native memory
  that BenchmarkDotNet does not see, so their allocation column understates them.
- The columnar end states differ: DuckDB pays an extra vector → Arrow conversion, and Polars
  exports nothing to .NET.
- `_1T` means different things per engine. ParquetSharp's Arrow reader defaults to one thread.
  DuckDB's `SET threads TO 1` is per instance (checked: 1 versus 8). Polars' `_1T` is
  `ParallelStrategy.None` on the scan only; its thread pool is still machine-sized. Pinning
  it fully needs `POLARS_MAX_THREADS=1`, which is process-wide and would also pin `_AllCores`
  in an in-process run.
- The datasets are small and differently chunked: TPC-H is a single row group, which leaves no
  room for row-group parallelism. Census has 33 row groups and Diamonds 54, about 1k rows each.
- Polars.NET 1.0.0 needed two workarounds, both in the lazy plan (see `TypedReadBenchmark.Polars`):
  after an eager `DataFrame.Rename`, column lookup by the new name fails; and `Rows<T>` cannot map a
  `Date` column to `DateTime?`.
- Local timings come from a busy dev machine. Read them as within-run ratios, as the repository's
  other local re-baselines do.

## First run (2026-10-24, dev host)

Apple M1 (8 cores), macOS 15.7.7, .NET 10.0.12 (the project rolls forward from net8.0, unlike the
regression gate, which pins .NET 8), BenchmarkDotNet 0.14.0 InProcess. Load average was about 2.5
throughout. `--validate` passed for all 36 benchmarks beforehand. Ratios are against `ParquetNet`
within the same run; lower is faster. BenchmarkDotNet flagged one row as multimodal:
Census/ParquetNet, the baseline.

**Typed rows** (time ratio; managed allocation ratio in brackets):

| | TPC-H 60k | Census 33k | Diamonds 54k |
|---|---:|---:|---:|
| ParquetNet (70.9 / 32.9 / 22.7 ms) | 1.00 | 1.00 | 1.00 |
| SourceGenerator | 0.73 (0.70) | 0.81 (0.70) | **0.62** (0.65) |
| ParquetSharp | 0.56 (0.61) | **0.76** (1.40) | 0.72 (2.00) |
| DuckDb | **0.52** (0.50) | 0.90 (0.60) | 0.65 (0.48) |
| Polars | 14.2 (8.8) | 16.3 (9.1) | 31.7 (20.0) |

**Columnar** (time ratio):

| | TPC-H | Census | Diamonds |
|---|---:|---:|---:|
| ParquetNet_RawColumns (13.2 / 4.4 / 4.4 ms) | 1.00 | 1.00 | 1.00 |
| SourceGenerator_Batches | 2.60 | 3.02 | 1.52 |
| ParquetSharp_Arrow_1T | 0.62 | 1.95 | 1.13 |
| DuckDb_Arrow_1T | 1.19 | 3.47 | 2.24 |
| DuckDb_Arrow_AllCores | 1.18 | 2.77 | 1.82 |
| Polars_DataFrame_1T | **0.40** | **0.28** | **0.29** |
| Polars_DataFrame_AllCores | 0.41 | 0.28 | 0.29 |

What it says:

1. **Typed rows: the generator is in the native wrappers' range, and fastest on the numeric
   file.** On Diamonds (all doubles and longs) it beats every native engine. On TPC-H (decimals,
   dates, a long string column) DuckDB and ParquetSharp are 25–30% ahead of it.
2. **Polars.NET's `Rows<T>` is the bottleneck, not Polars.** It makes a per-cell call into the
   native library, so it runs 14–32× slower than the baseline, while the same engine decodes the
   file 2.5–3.5× faster than Parquet.Net in the columnar tier.
3. **`AsBatches` costs 1.5–3× Parquet.Net's raw decode.** The end states differ:
   `RawColumnData` leaves decimals as unscaled longs, dates as ints and strings as
   `ReadOnlyMemory<char>`, while batches hold `decimal`, `DateTime` and `string`. Even so, that
   gap is the measured cost of conversion and materialisation on top of decode, and on the
   string-heavy files it is the largest single target. Polars and ParquetSharp show how much
   further decode itself can go; their decoders are listed above.
4. Parallelism barely matters at this scale. Polars' `None` and `Auto` match, and DuckDB gains
   only on the many-row-group files.

## Running

Run it in CI rather than locally: dispatch **Cross-Library Benchmarks**
(`.github/workflows/cross-library-benchmarks.yml`) from the Actions tab, or

```bash
gh workflow run cross-library-benchmarks.yml --ref <branch> -f filter='*'
```

The job restores in locked mode on .NET 10, runs `--validate`, then the benchmarks. The tables go
to the run's summary and the full reports to the `cross-library-benchmark-results` artifact. It is
dispatch-only and writes nothing back. A shared runner's absolute times are noisy, so read the
ratios within one table. Locally:

```bash
# Correctness first: every typed comparand must return rows equal to the generator's,
# every columnar one the file's row count. Exits 1 otherwise.
dotnet run -c Release --project benchmarks/Parquet.SourceGenerator.CrossLibraryBenchmarks -- --validate

# Everything (about 12 minutes on an M1):
dotnet run -c Release --project benchmarks/Parquet.SourceGenerator.CrossLibraryBenchmarks -- \
  --filter '*' --artifacts temp/cross-library --exporters github

# One tier or dataset:
dotnet run -c Release --project benchmarks/Parquet.SourceGenerator.CrossLibraryBenchmarks -- --filter '*TpchTypedRead*'
```

The project is not in the solution, so the PR build never restores its native runtimes; only the
dispatched workflow does. It is separate from the allocation regression gate
(`Parquet.SourceGenerator.Benchmarks`). Changing a package means re-running `dotnet restore` with
`--force-evaluate` and committing `packages.lock.json`, as for every other locked project.
