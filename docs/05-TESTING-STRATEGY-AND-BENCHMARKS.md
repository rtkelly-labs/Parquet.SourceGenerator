# 05 - Testing Machinery & Benchmarking Strategy

## Overview

Testing a C# Roslyn Source Generator requires specialized machinery compared to standard application development. Because the generator executes *inside* the compiler host against arbitrary user syntax trees, testing must validate:
1. **Source Generation Correctness**: Emitted C# code structure and syntax.
2. **Roslyn Incremental Performance & Caching**: Cache retention across syntax tree mutations.
3. **Binary Serialization Roundtrips**: End-to-end reading/writing with `Parquet.Net`.
4. **Native AOT & Trimming**: Zero reflection and zero trim warnings.
5. **Runtime Benchmarks**: Execution speed and allocation comparisons against reflection.

---

## 1. Generator Unit & Snapshot Testing (`Verify.SourceGenerators`)

Unit tests inspect the source generator output directly using Roslyn's `CSharpGeneratorDriver` combined with `Verify.Xunit` snapshot testing.

### Test Harness Setup
```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator;
using VerifyXunit;

public static class ModuleInitializer
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void Init() => VerifySourceGenerators.Initialize();
}

public class GeneratorSnapshotTests
{
    [Fact]
    public Task GeneratesCorrectSerializerForPoco()
    {
        string source = """
            using System;
            using Parquet.SourceGenerator;

            namespace TestApp;

            [ParquetSerializable]
            public partial record Customer
            {
                [ParquetColumn("customer_id")]
                public Guid Id { get; init; }

                public string Name { get; init; } = string.Empty;
            }
            """;

        Compilation compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ParquetSerializableAttribute).Assembly.Location)
            });

        var generator = new ParquetIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

        driver = driver.RunGenerators(compilation);

        return Verifier.Verify(driver);
    }
}
```

---

## 2. Incremental Cache Testing (`TrackIncrementalSteps`)

To ensure Roslyn doesn't re-run expensive generator transforms on unrelated edits (e.g., adding a comment or editing a method body), we test incremental step caching:

```csharp
[Fact]
public void GeneratorCachesOutputsOnUnrelatedChanges()
{
    string initialCode = "/* initial code */";
    string modifiedCode = "/* modified code with added method */";

    var driver = CSharpGeneratorDriver.Create(
        generators: new[] { new ParquetIncrementalGenerator().AsSourceGenerator() },
        driverOptions: new GeneratorDriverOptions(
            disabledOutputs: IncrementalGeneratorOutputKind.None,
            trackIncrementalSteps: true));

    // Run 1
    driver = driver.RunGenerators(compilation1);
    
    // Run 2 with modified compilation
    driver = driver.RunGenerators(compilation2);

    GeneratorDriverRunResult result = driver.GetRunResult();
    
    // Assert generator steps were cached rather than recalculated
    var stepState = result.Results[0].TrackedSteps["TransformModelStep"];
    Assert.All(stepState, step => Assert.Equal(IncrementalStepRunReason.Cached, step.Outputs[0].Reason));
}
```

---

## 3. End-to-End Binary Data Roundtrip Testing

Integration tests verify that data written with generated code can be parsed seamlessly by standard `ParquetReader` and vice versa.

```csharp
[Fact]
public async Task Roundtrip_Poco_Matches_ParquetNet()
{
    var records = new List<TestRecord>
    {
        new(Guid.NewGuid(), "Alice", 100.50m, DateTime.UtcNow),
        new(Guid.NewGuid(), "Bob", 250.00m, DateTime.UtcNow)
    };

    using var stream = new MemoryStream();

    // 1. Write using generated serializer
    await records.WriteParquetAsync(stream);

    // 2. Read using standard ParquetReader (Parquet.Net)
    stream.Position = 0;
    using var reader = await ParquetReader.CreateAsync(stream);
    Assert.Equal(1, reader.RowGroupCount);
    Assert.Equal(4, reader.Schema.DataFields.Length);

    // 3. Read back using generated deserializer
    stream.Position = 0;
    List<TestRecord> readRecords = await TestRecordParquetExtensions.ReadParquetAsync(stream);

    Assert.Equal(records.Count, readRecords.Count);
    Assert.Equal(records[0].Name, readRecords[0].Name);
}
```

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

A dedicated project target (`test/Parquet.SourceGenerator.AotTest`) tests compilation with `<PublishAot>true</PublishAot>`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Testing Command:
```bash
dotnet publish test/Parquet.SourceGenerator.AotTest/Parquet.SourceGenerator.AotTest.csproj -c Release
```
*Result*: Asserts that `dotnet publish` completes without emitting any trimming warnings (`IL2026`, `IL3050`) or reflection errors.

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
