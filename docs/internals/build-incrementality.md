---
description: "Generator caching measurements (#258)."
order: 50
---

# Build Incrementality (#258)

## Result

The generator's incremental driver pass is sub-millisecond for this two-model workload on the
measured machine. An unrelated-file edit is the cheapest path, while a per-model edit regenerates
one model and retains the other model's generated output:

| Driver pass | Mean | Allocated |
|:---|---:|---:|
| Initial generation | 546.0 μs | 942.64 KB |
| Unrelated-file edit | 317.2 μs | 23.57 KB |
| Per-model edit | 467.9 μs | 446.59 KB |

These are generator-driver costs, not a complete `dotnet build` wall-clock measurement. They are
the actionable build-time costs for the generator portion of an edit and include Roslyn's driver
and source-output work, but exclude process startup, restore, compilation of generated code, and
other MSBuild targets.

## Measurement

The benchmark is `IncrementalityBenchmark` in
[`benchmarks/Parquet.SourceGenerator.Benchmarks/IncrementalityBenchmark.cs`](../../benchmarks/Parquet.SourceGenerator.Benchmarks/IncrementalityBenchmark.cs).
It creates one compilation containing two attributed models and one unrelated file. The initial
pass is prepared outside the measured method for edit cases; `InitialGeneration` creates a fresh
driver. All methods return the generated-source count so the driver result remains observable.

Run it manually with:

```bash
dotnet run -c Release --project benchmarks/Parquet.SourceGenerator.Benchmarks/Parquet.SourceGenerator.Benchmarks.csproj -- --filter '*IncrementalityBenchmark*'
```

Captured on 2026-09-16 with BenchmarkDotNet 0.14.0, macOS Sequoia 15.7.7, Apple M1, 8 physical /
8 logical cores, .NET SDK 9.0.315 / runtime 9.0.17 Arm64, concurrent workstation GC, in-process
toolchain, 3 warmups, and 10 measured iterations. BenchmarkDotNet warned that individual
iterations were below 100 ms; that is expected for a sub-millisecond spike and is one reason this
measurement is descriptive rather than a CI ratchet.

## Incrementality evidence

[`IncrementalityTests.cs`](../../test/Parquet.SourceGenerator.Tests/IncrementalityTests.cs) uses the
Roslyn 4.8.0 `GeneratorDriver` APIs with incremental step tracking enabled. The tests are named
after the two edit scenarios:

- `TrackedOutputStepsForAnUnrelatedFileEditAreCached` changes only the unrelated syntax tree and
  observes all four source-output records as `Cached`; generated text is byte-identical.
- `TrackedOutputStepsForAPerModelEditAreSelective` changes only `ModelA` and observes one
  `Modified`, two `Cached`, and one `Unchanged` source-output record. `ModelA`'s generated text
  changes and `ModelB`'s remains byte-identical. The separate `CSharpCompilation` input is expected
  to be `Modified` for either edit.

The four output records reflect the two registered source-output callbacks, each with two model
inputs. The `Unchanged` record is the Arrow-gated callback receiving a changed model while its
`ArrowReferenced` value remains false, so it correctly emits no Arrow source. The test reports
reasons through the public `TrackedOutputSteps` and `IncrementalStepRunReason` APIs rather than
relying on Roslyn internals.

## Value equality

The spike also corrected a real equality gap. `PropertyModel` is a record, but its compound
subtrees and flags were init-only properties outside the positional record constructor. The
compiler-generated record equality therefore ignored `Children`, `Element`, `MapValue`,
`IsSortKey`, and `CompoundIsValueType`, despite the surrounding comments claiming whole-tree value
equality. `PropertyModel.Equals(PropertyModel?)` and `GetHashCode()` now include every semantic
field. `EquatableArray<T>` compares elements by sequence and value, so independently allocated
arrays with equal models compare equal and hash equally; changing a nested child makes both the
property and containing `TargetClassModel` unequal.

This matters for incrementality: a changed nested model must not be mistaken for an equal cached
model. The equality test proves both the positive and negative cases.

## Roslyn tracking-name claim

The obvious implementation would label providers with `WithTrackingName("TargetModels")` and
similar names. That is not available on the compatibility floor used here: the shipping generator
references Microsoft.CodeAnalysis.CSharp 4.0.1, and the test project uses 4.8.0. Both fail to
compile that extension on `IncrementalValuesProvider<T>` / `IncrementalValueProvider<T>` with
`CS1061`. The claim that this branch can add stable provider labels was therefore stale and is
resolved as unsupported at the current Roslyn floor.

No provider labels were added and no Roslyn dependency was raised. The tests use the supported
named `TrackedOutputSteps` collection and public run-reason values, with descriptive test names.
Adding stable `WithTrackingName` labels remains a separate compatibility decision: it requires
raising or conditionally changing the shipping Roslyn reference and validating all supported
consumer hosts. The current evidence is sufficient to prove selective source-output caching
without making that compatibility change.

## Limits

- The fixture has two small models; it does not predict cost for a large consumer compilation.
- The benchmark measures `CSharpGeneratorDriver`, not MSBuild or IDE end-to-end latency.
- It uses the in-process BenchmarkDotNet toolchain; BenchmarkDotNet could not set high process
  priority in this environment.
- Managed allocation figures include generated-source construction and Roslyn bookkeeping. They
  are not a process RSS measurement.
- There is deliberately no checked-in numeric threshold, baseline comparison, or CI gate.
