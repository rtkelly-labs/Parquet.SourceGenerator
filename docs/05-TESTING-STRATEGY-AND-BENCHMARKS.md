# 05 - Testing Machinery & Benchmarking Strategy

## Overview

Prioritize evidence at consumer and data boundaries:

1. **End-to-end tests:** pack the actual NuGet packages, compile consumer projects, execute their
   read/write scenarios, and publish and execute a native AOT consumer.
2. **Integration tests:** run the real Roslyn driver, compile emitted source, inspect Parquet file
   metadata, and compare data across Parquet.Net, PyArrow, DuckDB and Apache Parquet.
3. **Golden and regression fixtures:** retain deterministic external files and minimized fuzz
   failures with independent expected values. Generated-source artifacts support review.

Small tests remain useful when they protect a distinct contract: diagnostics, option defaults,
incremental cache invalidation, buffer lifetimes, or a gate that must reject a seeded failure.
Avoid tests that merely instantiate an empty attribute, assign and read a property, list existing
members of an enum, or pin private helper names in generated text. Before deleting a check, identify
its behavioral replacement; test count and coverage percentages alone do not establish correctness.

## 1. End-to-End Package Consumption

`test/PackageConsumption` and `test/PackageConsumptionLegacy` restore the packed packages from a
local feed rather than referencing the shipping projects. CI executes both on .NET 8 and .NET 9,
then hands files between the modern and legacy consumers in both directions. These checks exercise
package layout, analyzer loading, attribute dependencies, compilation and serialization together.

The Windows `net472-consumer` job in `ci.yml` executes the Legacy package on .NET Framework 4.7.2
and hands files to and from .NET 8. It is part of the required `build` aggregate, with no path filter
or failure bypass. The native AOT publish-and-execute check is described in section 4.

## 2. Generator and File Integration

`DiagnosticTests` runs real annotated C# through Roslyn and checks consumer-visible diagnostics.
`IncrementalityTests` reuses a driver across unrelated edits, formatting changes and model changes;
column-name, nullability and encoding edits must change only the affected model's output. Retain
focused internal equality checks where they cover nested state not exercised by these edits.
See [28 - Build Incrementality](./28-BUILD-INCREMENTALITY-258.md) for the tracked-step evidence.

`ColumnEncodingTests` checks the encodings actually recorded in Parquet metadata and reads the
values back, including runtime overrides of compile-time hints. This is the contract that emitter
assignment-string assertions only approximated.

CI validates generated files with PyArrow, DuckDB and Apache Parquet, and reads external-engine
fixtures with the generated reader. A generated writer/reader round trip alone can conceal a shared
mistake; preserve these external checks alongside the independent Parquet.Net client used by fuzzing.

## 3. Golden Fixtures and Derived Review Outputs

`TestDataIntegrationTests` compares every row and every column of the primitive, nullable and
100,000-row fixtures for PyArrow format settings 1.0 and 2.6 and the C# Parquet.Net producer.
Expected values come from the deterministic fixture specification, independently of the generated
reader. A positive control rewrites an interior-row value while preserving the count and endpoints
and requires the full comparison to reject the result.

Minimized fuzz cases are committed inputs replayed as permanent behavioral regressions. Preserve
nulls, empty strings and buffers, boundary values, and schema evolution in these fixtures and the
external interoperability oracle.

`GoldenCodeGenRegressionTests` checks canonical models and publishes emitted source and public API
artifacts. CI semantically compiles every corpus model and runs emitted-code analyzers. The source
and API artifacts are derived, uncommitted outputs diffed against the merge base for review; they
are not automatic snapshot-equality gates. See
[17 - Generated Public API Baselines](./17-GENERATED-API-BASELINES.md).

---

## 3a. Property-Based Fuzzing & Corrupted-File Coverage

`test/Parquet.SourceGenerator.Tests/PropertyBased/` generates random cases inside the documented
supported flat envelope and checks them against a hand-written reference implementation.

### What a case is

A case is a seed. Everything else — row count, which columns carry fuzzed values, the value profile
per column, row group size, compression, string deduplication and the runtime encoding hints — is
derived from it, so printing a seed is enough to replay a failure anywhere:

```bash
PARQUET_FUZZ_SEED=4354685581836845355 PARQUET_FUZZ_CASES=1 \
  dotnet test Parquet.SourceGenerator.slnx -c Release --filter FullyQualifiedName~PropertyBased
```

Randomness comes from `FuzzRandom` (SplitMix64), not `System.Random`, because a reproducible seed is
only worth something if it reproduces on every runtime and every future framework version.

Each column draws from its own seeded stream. That is what makes shrinking sound: dropping a column
or truncating the row count leaves every remaining value byte-for-byte identical.

### The properties

| Property | What it compares |
| --- | --- |
| `generated-write/engine-read` | Generated writer's file, read by the hand-written reference reader, value for value, plus the row-group layout the options asked for |
| `engine-write/generated-read` | Reference writer's file — with the columns in a seed-shuffled order — read by the generated reader |
| `generated-round-trip` | The generated writer and reader against each other |

The reference engine (`IndependentParquetEngine`) is a second, independently written client of
Parquet.Net that takes the opposite branch wherever the library offers one — strings and binary go
through the collection-based `WriteAsync` helpers rather than the `ReadOnlyMemory` path the generator
emits. It re-decides, by hand, everything the generator decides: column ordering and index
resolution, definition levels and nulls, buffer slicing across row groups, physical type per logical
type, timestamp and decimal units. What it does *not* independently verify is Parquet.Net's own
encoders; that is what the pinned PyArrow fixtures are for.

### Value profiles

Uniform randomness rarely visits the interesting corners, so each column is assigned a profile:
`Typical`, `Boundary` (type extremes, NaN, infinities, empty strings and buffers), `Extreme`
(Unicode-heavy strings and large payloads), `Sparse` (half nulls), `AllNull`, and `Constant` (which
pushes the writer towards dictionary encoding).

### Seeds in CI

- **Every push:** the default seed set (`FuzzConfig.DefaultBaseSeed`, 24 cases) runs as part of the
  normal test job. It is fixed, so a green run means the same thing on every machine, and
  `DefaultSeedsAreDeterministicAndStable` guards that contract.
- **Scheduled and manual:** `.github/workflows/fuzz.yml` runs weekly and on dispatch with a wider
  seed range, and uploads any minimised failing cases as an artifact.

### Minimisation and permanent fixtures

A failing case is shrunk — fewer rows, then fewer columns, then simpler knobs — while the failure
still reproduces, and the minimised case is written to `temp/fuzz-failures/*.json`. The failure
message names the file. Copying it into `test/Parquet.SourceGenerator.Tests/FuzzFixtures/` makes the
regression permanent: `CommittedRegressionFixtureStillPasses` replays every fixture in that directory
on every run.

### Corrupted files

`CorruptedParquetTests` takes a valid file and damages it: truncation at several points, clobbered
header and footer magic, zero/huge/negative/overshooting footer lengths, zeroed and shifted footer
metadata, a hole punched in the data region, columns written with the wrong physical type or without
their logical annotation, and random bit flips.

The contract is deliberately weak enough to be true:

- **Structural damage must be rejected.** Truncation, bad magic and a bad footer length always throw.
- **Nothing may hang or exhaust memory.** Every read is run under a timeout and an allocation
  ceiling, and `OutOfMemoryException` counts as a failure, not as a rejection.
- **Nothing may come back silently wrong.** Where a read succeeds, every value must match the
  original.

The one place that last rule cannot be enforced is a bit flip deep inside a data page or inside the
column-chunk metadata: Parquet's page CRCs are optional and Parquet.Net does not verify them, so a
flipped byte can decode to a different but perfectly well-formed value, and a flipped page offset can
send the reader at a different, valid page. The fuzzer found both. Those cases therefore assert only
termination and bounded allocation. Detecting them needs CRC verification in Parquet.Net, not
anything the generator can do — see `UPSTREAM_DEPENDENCY_LIMITATIONS.md`.

---

## 4. Native AOT & Trimming Verification

CI publishes `test/Parquet.SourceGenerator.AotTest` for `linux-x64` with Native AOT enabled,
then executes the published native binary. A CoreCLR `dotnet run` does not prove AOT support.
The native consumer checks real serialization behavior and fails on a mismatch.

The workflow also checks the publish log for trimming and dynamic-code warnings. Warnings attributed
to Parquet.Net are currently tolerated; this is not a claim of zero warnings across all dependencies.
See the Native AOT row in [51 - CI Gate Matrix](./51-CI-GATE-MATRIX.md) for the exact gate boundary.

---

## 5. Performance Benchmarks (`BenchmarkDotNet`)

Located in `benchmarks/Parquet.SourceGenerator.Benchmarks`:

```csharp
[MemoryDiagnoser]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class SerializationBenchmark
{
    private List<TransactionLog> _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = Enumerable.Range(0, 100_000)
            .Select(i => new TransactionLog(Guid.NewGuid(), $"User_{i}", i * 1.5m, DateTime.UtcNow))
            .ToList();
    }

    [Benchmark(Baseline = true)]
    public async Task Reflection_ParquetConvert()
    {
        using var stream = new MemoryStream();
        await ParquetConvert.SerializeAsync(_data, stream);
    }

    [Benchmark]
    public async Task SourceGenerator_WriteParquetAsync()
    {
        using var stream = new MemoryStream();
        await _data.WriteParquetAsync(stream);
    }
}
```
