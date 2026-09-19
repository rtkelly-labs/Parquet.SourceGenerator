# 44 — Roslyn Generator Tooling Evaluation

An assessment of the commonly recommended Roslyn incremental-generator tooling stack
(polyfills, dependency bundling, test frameworks, syntax builders, author analyzers) against
what this repository already does. Each verdict cites the file that decides it.

The short version: four of the five recommendation categories are already solved here, two of
them more thoroughly than the off-the-shelf tool would manage. The value is concentrated in the
two things the generic advice does *not* name — a stale author-analyzer pin and a pipeline node
that retains `SemanticModel`.

---

## Verdicts

| Tool | Verdict | Deciding evidence |
|:---|:---|:---|
| PolySharp | Skip | Only `IsExternalInit` is needed; already present in 8 lines |
| Nullable (package) | Skip | No nullability attributes used anywhere in the generator |
| ILRepack / Costura.Fody / Paket | Not applicable, and harmful if adopted | Generator has zero third-party runtime dependencies; `DebugType=embedded` |
| Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing | Skip | Duplicates existing suites; package last shipped at 1.1.4 |
| Verify.SourceGenerators | Skip | `GoldenCodeGenRegressionTests` is a strict superset |
| SyntaxFactory / `Microsoft.CodeAnalysis.CSharp.Workspaces` | Reject | Would churn every golden file; Workspaces is banned in analyzers |
| Scriban | Reject | Reintroduces the bundling problem this repo does not have |
| `IndentedTextWriter` / internal `CodeWriter` | **Adopt** | 1,348 literal-indent appends across 3,163 `AppendLine` calls |
| Microsoft.CodeAnalysis.Analyzers (upgrade) | **Adopt** | Pinned at 3.3.3; every generator-author rule postdates it |
| Roslynator.Analyzers | Optional | Overlaps Meziantou.Analyzer, already repo-wide |

---

## 1. Polyfills — skip

`src/Parquet.SourceGenerator/Parquet.SourceGenerator.csproj` sets `LangVersion=latest` on
`netstandard2.0`, and the single polyfill this needs already exists:

```
src/Parquet.SourceGenerator/IsExternalInit.cs   (8 lines, guarded by #if NETSTANDARD2_0)
```

A search across `src/Parquet.SourceGenerator` finds no use of `[NotNullWhen]`, `[MaybeNull]`,
`[MemberNotNull]`, `required` members, `Index`/`Range`, or `[CallerArgumentExpression]`. PolySharp
and Nullable are both source-only and low-risk, but they would be added to supply features the
code does not use. The existing file is the cheaper form of the same thing.

**Revisit when:** the generator first wants `required` members (which needs both
`RequiredMemberAttribute` and `CompilerFeatureRequired`, i.e. three hand-rolled files rather than
one) or starts annotating nullability contracts on internal seams. At that point PolySharp
replaces a growing `Polyfills/` folder rather than competing with one 8-line file.

## 2. Dependency bundling — not applicable

The premise of ILRepack/Costura/Paket packaging advice is that the generator references
third-party runtime assemblies the compiler will not resolve. This generator references only:

- `Microsoft.CodeAnalysis.CSharp` (`PrivateAssets="all"`, compiler-provided at runtime)
- `Microsoft.CodeAnalysis.Analyzers`, `Microsoft.CodeAnalysis.PublicApiAnalyzers` (analyzers)
- a `ProjectReference` to `Parquet.SourceGenerator.Attributes` that is deliberately *not*
  `PrivateAssets="all"`, because it must flow to consumers as a package dependency — and which
  the generator never binds to (`TargetParser` matches attributes by fully-qualified name).

There is nothing to merge. Packaging into `analyzers/dotnet/cs` is already handled correctly by
the `PackBuildOutputs` target with `IncludeBuildOutput=false` and the resulting NU5128 suppressed.

Adopting ILRepack would be a regression, not a no-op: `Directory.Build.props` sets
`DebugType=embedded` with SourceLink and `EmbedUntrackedSources`, and IL merging rewrites the
assembly after those are embedded. The repo's own IL interrogation tooling
(`docs/08-IL-INTERROGATION.md`, `scripts/InterrogateIL.cs`) would also be reading a rewritten
assembly rather than the one the compiler produced.

This is why the Scriban recommendation in §4 is rejected rather than merely declined: adopting a
templating engine is what would *create* the bundling problem this section says does not exist.

## 3. Testing — already ahead of both suggestions

`Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing` offers declarative expected-diagnostics
and expected-generated-source assertions. Both are covered — `DiagnosticTests.cs` for the former,
`GoldenCodeGenRegressionTests.cs` for the latter. The package's latest published version is
**1.1.4**; it is not a moving target and would pin Roslyn test dependencies against the versions
this repo deliberately controls.

`Verify.SourceGenerators` is the better-maintained option (latest 2.5.0) but would be a lateral
move at best. The existing golden harness asserts four things per model, where a Verify snapshot
asserts one:

1. emitted source is byte-identical to the checked-in `.g.cs`,
2. the emitted source parses with zero syntax diagnostics,
3. it compiles in-memory against Parquet.Net with zero errors,
4. its `.api.txt` / `.api.shape.txt` / `.metrics.txt` companions match — the API change contract
   gate from `docs/18-API-CHANGE-CONTRACT.md`.

It also already has the acceptance workflow Verify is usually adopted for
(`UPDATE_GOLDEN_FILES=true`). Swapping in Verify would mean either losing (2)–(4) or running
both harnesses.

**The gap the suggestions do point at, correctly:** `GoldenCodeGenRegressionTests` calls
`CodeEmitter.EmitSource` directly — it emulates the generator rather than running
`CSharpGeneratorDriver`. Hint-name construction, the Arrow reference gate and the configuration
combine are covered by separate targeted tests (`HintNameCollisionTests`,
`ArrowConditionalEmissionTests`, `GeneratorConfigurationTests`, `IncrementalityTests`) but never
by the golden baselines. Nothing in either suggested package closes that; a driver-level golden
case would.

## 4. Emission — the one clear adoption

This is where the generic advice lands on something real. Measured across
`src/Parquet.SourceGenerator/Emitter/`:

- 9,459 lines of emitter code
- 3,163 `AppendLine` calls
- **1,348** of them open with a hardcoded literal indent (`AppendLine("        ...`)
- 9 files thread an `indent` string parameter through by hand, e.g.
  `CompoundBuffers.EmitSingleWriteReturn(StringBuilder, LeafColumn, string indent, ...)`

Indentation is therefore a value carried in two incompatible ways — literal prefixes and a
threaded parameter — with no mechanism enforcing that a nested emitter receives the right one.

**Recommended:** an internal `CodeWriter` wrapping `System.CodeDom.Compiler.IndentedTextWriter`
with a `using (writer.Block())` scope. Verified available: `IndentedTextWriter` is present in the
`netstandard2.0` reference assembly, so this adds **no package reference** and does not touch §2.

**Not recommended:**

- *SyntaxFactory / Workspaces.* `Microsoft.CodeAnalysis.CSharp.Workspaces` must not be referenced
  from an analyzer assembly at all (this is what RS1038 in §5 exists to catch). Bare
  `SyntaxFactory` without Workspaces cannot normalize whitespace usefully, and re-expressing 9,459
  lines as AST construction would rewrite every golden file and every `.api.txt` baseline.
- *Scriban.* See §2 — it is a third-party runtime dependency inside the analyzer. It would also
  move emitted text out of C# and into embedded resources, where `docs/22-GENERATED-CODE-METRICS.md`
  and the duplication gate in `docs/23-DUPLICATION.md` cannot see it.

**Migration constraint:** the refactor must be byte-identical in output. Any whitespace drift
re-baselines every file in `test/Parquet.SourceGenerator.Tests/GoldenFiles/` and obscures the
one thing the golden files exist to show. The safe sequence is to introduce `CodeWriter` as a
`StringBuilder` façade first, migrate one component at a time, and let the existing golden tests
be the proof at each step.

## 5. Author analyzers — cheapest real win, plus one correction

### The pin is stale

`src/Parquet.SourceGenerator/Parquet.SourceGenerator.csproj` carries:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.Analyzers" VersionOverride="3.3.3" PrivateAssets="all" />
```

against `3.3.4` centrally and **5.9.0** currently on nuget.org. Version 3.3.3 predates the entire
set of rules written for generator authors:

| Rule | What it catches |
|:---|:---|
| RS1035 | Banned APIs inside a generator (`Environment`, file and network I/O, culture-sensitive calls) |
| RS1036 | Analyzer banned-API enforcement not configured |
| RS1038 | Compiler extension referencing Workspaces assemblies (see §4) |
| RS1041 | Compiler extension not targeting `netstandard2.0` |

Unlike the `Microsoft.CodeAnalysis.CSharp` pin, this package is build-time only and
`PrivateAssets="all"`, so raising it **does not move the consumer compiler floor**. The repo
already runs `Microsoft.CodeAnalysis.PublicApiAnalyzers` at 5.6.0, so the toolchain supports it.

### `EnforceExtendedAnalyzerRules` is set on the wrong project

It is currently set on `tools/Parquet.SourceGenerator.ApiGates/` and **not** on
`src/Parquet.SourceGenerator/` — the assembly that actually ships as an analyzer. The gate is
enabled for the internal API-contract analyzer and absent from the shipping generator.

### Correction to the common claim

The frequently repeated line that `Microsoft.CodeAnalysis.Analyzers` warns "against holding
`ISymbol` or `Compilation` references inside incremental state pipelines" is **not accurate at any
version**. No shipped Roslyn analyzer inspects incremental-pipeline lambda return types for
retained symbols. RS1035/1036/1038/1041 are about banned APIs, references and target framework.

This matters because it is the exact failure mode this repository currently has — see §6 — and
upgrading the analyzers package will not surface it. Only review and a memory measurement will.

Roslynator.Analyzers is optional: its value here overlaps `Meziantou.Analyzer`, which
`Directory.Build.props` already applies to every non-test project.

---

## 6. The finding the tooling would not have caught

`ParquetIncrementalGenerator.Initialize` registers the syntax provider's transform as an identity
function:

```csharp
IncrementalValuesProvider<GeneratorSyntaxContext> targetNodes =
    context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (s, _) => IsTargetSyntax(s),
        transform: static (ctx, _) => ctx
    );
```

`GeneratorSyntaxContext` carries a `SemanticModel`, which roots the `Compilation` it came from.
Because this is the output of a pipeline node, the driver stores those values in its state table
and reuses them on the next run. Parsing to a value-equatable model happens one node later, after
`.Combine(configuration)`.

Two consequences:

1. **Retention.** Cached entries from earlier compilations hold `SemanticModel` instances, and
   through them their `Compilation`. In an IDE host the driver is long-lived, so this accumulates
   rather than being collected between edits.
2. **No useful equality.** `GeneratorSyntaxContext` is a struct with no `IEquatable<T>`, so the
   driver falls back to `ValueType.Equals` — reflection-based field comparison in which the
   `SemanticModel` reference differs on every compilation. The node can never report as cached.

`IncrementalityTests` does not detect this, and is not wrong to pass: it asserts on
`TrackedOutputSteps`, and the *outputs* genuinely are `Cached` because `TargetParserResult` is a
correctly value-equatable record. The symptom is memory and repeated work, not wrong output.

**Fix shape:** move parsing into the `transform` so the cached node holds `TargetParserResult`
rather than `GeneratorSyntaxContext`. The obstacle is that parsing currently consumes
`configuration.FeatureLevel`, which derives from `CompilationProvider` and so is only available
after the combine. The resolution is to parse the full shape unconditionally in the transform and
apply the feature-level restriction downstream on the value-equatable model, where it costs
nothing to re-evaluate.

**Before changing anything:** measure. `docs/09-PERFORMANCE-TRIAGE-DOTNET-DUMP.md` and
`dotnet tool run dotnet-dump` are the tools already in the manifest for exactly this — a driver
held across several edits, dumped, and inspected for retained `CSharpCompilation` instances. The
mechanism above is established from the code; the magnitude is not, and this repository does not
merge performance claims without numbers.

---

## 7. Two corrections to existing documentation

### `docs/28-BUILD-INCREMENTALITY-258.md` overstates the `WithTrackingName` limitation

That document states: *"the shipping generator references Microsoft.CodeAnalysis.CSharp 4.0.1, and
the test project uses 4.8.0. Both fail to compile that extension … with `CS1061`."*

The second half is wrong. Inspecting the shipped assemblies directly:

| Microsoft.CodeAnalysis.Common | `WithTrackingName` | `ForAttributeWithMetadataName` |
|:---|:---:|:---:|
| 4.0.1 | absent | absent |
| 4.3.0 | **present** | **present** |
| 4.8.0 | **present** | **present** |

Both live on `Microsoft.CodeAnalysis.IncrementalValueProviderExtensions` and
`SyntaxValueProvider` from 4.3.0 onward. The constraint is solely the generator project's own
4.0.1 pin — nothing about 4.8.0 blocks it.

The document's *conclusion* stands unchanged: adding tracking names still requires raising the
shipping Roslyn reference, which is still a consumer-compatibility decision. What changes is the
price. One bump to 4.3.x buys both `WithTrackingName` (named pipeline stages, so
`IncrementalityTests` can assert per-stage rather than only on outputs) and
`ForAttributeWithMetadataName` (which replaces the hand-rolled `IsTargetSyntax` predicate with a
compiler-side attribute index). The cost is that consumers below the 4.3 compiler — roughly
VS 17.3 / .NET SDK 6.0.4xx — lose the generator. That belongs in
`docs/14-COMPATIBILITY-MATRIX.md` as an explicit floor decision rather than being carried as a
compile failure that is not actually a compile failure at 4.8.0.

### `docs/03-INCREMENTAL-GENERATOR-PIPELINE.md` no longer describes the pipeline

Section 1 of that document shows a two-step pipeline producing `ClassToGenerate?` with a `.Where`
filter. The implemented pipeline has five nodes, a `GeneratorConfiguration` combine off
`CompilationProvider`, a separately registered configuration-diagnostic output, and an
Arrow-reference-gated third output. Section 2 names the value-equatable model
`ClassToGenerate`; the type is `TargetClassModel`.

The doc is also the natural home for the §6 rule it already half-states — it warns that "passing
raw `ISymbol` or `SyntaxNode` references downstream breaks Roslyn's cache" while the code passes
`GeneratorSyntaxContext`, which carries both.

---

## 8. On the source of these recommendations

The recommendation list this evaluation responds to closes with a pointer to a walkthrough named
only as "Incremental Source Generators with Roslyn", with no author, publisher or URL. It is not
citable and was not consulted. Every claim checked above was verified against either this
repository or the published packages; the two that did not survive verification are the analyzer
capability in §5 and, independently, this repo's own `WithTrackingName` claim in §7.

---

## Recommended order

1. **Upgrade `Microsoft.CodeAnalysis.Analyzers`** in the generator project and set
   `EnforceExtendedAnalyzerRules`. No consumer impact, fixes an inverted gate, and any new
   diagnostics are worth seeing before the larger items. (§5)
2. **Measure the `GeneratorSyntaxContext` retention**, then restructure the transform if the dump
   confirms it. (§6)
3. **Correct `docs/28` and `docs/03`.** Cheap, and `docs/28` currently misprices the Roslyn floor
   decision. (§7)
4. **Introduce `CodeWriter`**, migrating one emitter component at a time under the existing golden
   tests. (§4)
5. **Decide the Roslyn floor** — 4.0.1 or 4.3.x — as a compatibility-matrix entry. It gates
   `ForAttributeWithMetadataName` and `WithTrackingName` together. (§7)

Nothing in items 1–5 requires a new package reference.
