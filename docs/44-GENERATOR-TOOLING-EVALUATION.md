# 44 — Roslyn Generator Tooling Evaluation

An assessment of the commonly recommended Roslyn incremental-generator tooling stack — polyfills,
dependency bundling, test frameworks, syntax builders, author analyzers — against what this
repository already does.

**Nearly all of it is already solved here, and two of the recommendations would be regressions.**
Where a recommendation does land on something real, the 2026-09-18 full-repo review had already
filed it, usually in more depth; this document defers to those issues rather than restating them.
Two items were not in the backlog and are now #471 and #472.

---

## Verdicts

| Tool | Verdict | Deciding evidence |
|:---|:---|:---|
| PolySharp | Skip | Only `IsExternalInit` is needed; already present in 8 lines |
| Nullable (package) | Skip | No nullability attributes used anywhere in the generator |
| ILRepack / Costura.Fody / Paket | Not applicable, and harmful if adopted | Zero third-party runtime dependencies; `DebugType=embedded` |
| Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing | Skip | Duplicates existing suites; package last shipped at 1.1.4 |
| Verify.SourceGenerators | Skip | `GoldenCodeGenRegressionTests` is a strict superset |
| SyntaxFactory / `Microsoft.CodeAnalysis.CSharp.Workspaces` | Reject | Workspaces must not be referenced from an analyzer (RS1038) |
| Scriban | Reject | Reintroduces the bundling problem this repo does not have |
| `IndentedTextWriter` / an indentation abstraction | Adopt — **already #439** | This doc adds only: no package reference needed |
| Microsoft.CodeAnalysis.Analyzers (upgrade) | Adopt — **filed as #471** | Pinned at 3.3.3; every generator-author rule postdates it |
| Roslynator.Analyzers | Optional | Overlaps Meziantou.Analyzer, already repo-wide |

---

## 1. Polyfills — skip

`src/Parquet.SourceGenerator/Parquet.SourceGenerator.csproj` sets `LangVersion=latest` on
`netstandard2.0`, and the single polyfill this needs already exists:
`src/Parquet.SourceGenerator/IsExternalInit.cs`, 8 lines, guarded by `#if NETSTANDARD2_0`.

A search across `src/Parquet.SourceGenerator` finds no use of `[NotNullWhen]`, `[MaybeNull]`,
`[MemberNotNull]`, `required` members, `Index`/`Range`, or `[CallerArgumentExpression]`. PolySharp
and Nullable are both source-only and low-risk, but they would be added to supply features the
code does not use.

**Revisit when:** the generator first wants `required` members — that needs both
`RequiredMemberAttribute` and `CompilerFeatureRequired`, so three hand-rolled files rather than
one, which is where PolySharp starts winning.

## 2. Dependency bundling — not applicable

The premise of ILRepack/Costura/Paket advice is that the generator references third-party runtime
assemblies the compiler will not resolve. This generator references only:

- `Microsoft.CodeAnalysis.CSharp` (`PrivateAssets="all"`, compiler-provided at runtime)
- `Microsoft.CodeAnalysis.Analyzers`, `Microsoft.CodeAnalysis.PublicApiAnalyzers` (analyzers)
- a `ProjectReference` to `Parquet.SourceGenerator.Attributes` that is deliberately *not*
  `PrivateAssets="all"`, because it must flow to consumers as a package dependency — and which the
  generator never binds to (`TargetParser` matches attributes by fully-qualified name)

There is nothing to merge. Packaging into `analyzers/dotnet/cs` is already handled by the
`PackBuildOutputs` target with `IncludeBuildOutput=false` and the resulting NU5128 suppressed.

Adopting ILRepack would be a regression rather than a no-op: `Directory.Build.props` sets
`DebugType=embedded` with SourceLink and `EmbedUntrackedSources`, and IL merging rewrites the
assembly after those are embedded. The repo's own IL interrogation tooling
(`docs/08-IL-INTERROGATION.md`, `scripts/InterrogateIL.cs`) would also be reading a rewritten
assembly rather than the one the compiler produced.

This is why Scriban is rejected in §4 rather than merely declined: adopting a templating engine is
what would *create* the problem this section says does not exist.

## 3. Testing — already ahead of both suggestions

`Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing` offers declarative expected-diagnostics
and expected-generated-source assertions. Both are covered — `DiagnosticTests.cs` for the former,
`GoldenCodeGenRegressionTests.cs` for the latter. The package's latest published version is
**1.1.4**; it would pin Roslyn test dependencies against versions this repo deliberately controls.

`Verify.SourceGenerators` is better maintained (latest 2.5.0) but would be a lateral move. The
golden harness asserts more per model than a Verify snapshot does — byte-identical text, a syntax
parse, and the `.api.txt` / `.api.shape.txt` / `.metrics.txt` companions that carry the API change
contract from `docs/18-API-CHANGE-CONTRACT.md` — and five of the eight goldens additionally run
`RunGeneratorsAndUpdateCompilation` and assert the bound compilation has no errors. It also already
has the acceptance workflow Verify is usually adopted for (`UPDATE_GOLDEN_FILES=true`).

**On the gaps in that harness, see the backlog, not this document.** #413 covers the three goldens
built from hand-typed models that are parsed but never bound — which also means they never exercise
`TargetParser → emitter` end to end. #407 covers the self-healing baseline. Neither suggested
package addresses either, and adopting Verify would not have caught them: a snapshot framework
records what the emitter produced, it does not type-check it.

## 4. Emission — real, and already filed as #439

The recommendation to replace hand-threaded indentation with `IndentedTextWriter` is correct, and
#439 has it with better evidence (89 whitespace literals, 337 `{indent}` splices, 6 string-arithmetic
sites, 16-space literal default parameters) and the right conclusion, including the same rejection
of a template engine and the same note that the golden files are the safety net for a
byte-identical refactor. #439 is also the prerequisite for #434.

**The one thing this evaluation adds:** `System.CodeDom.Compiler.IndentedTextWriter` is present in
the `netstandard2.0` reference assembly — verified by inspecting `build/netstandard2.0/ref/netstandard.dll`
from `NETStandard.Library` 2.0.3. So #439's `IndentedWriter` can be built on it with **no package
reference**, which matters given §2: an indentation abstraction that needed a NuGet dependency
would drag the bundling problem in with it.

On the alternatives the generic advice offers in place of #439's approach:

- *SyntaxFactory / Workspaces.* `Microsoft.CodeAnalysis.CSharp.Workspaces` must not be referenced
  from an analyzer assembly at all — RS1038 in §5 exists to catch exactly that. Bare `SyntaxFactory`
  cannot normalize whitespace usefully, and re-expressing the emitters as AST construction would
  rewrite every golden file and every `.api.txt` baseline.
- *Scriban.* See §2. It would also move emitted text out of C# and into embedded resources, where
  `docs/22-GENERATED-CODE-METRICS.md` and the duplication gate in `docs/23-DUPLICATION.md` cannot
  see it.

## 5. Author analyzers — filed as #471, plus a correction worth keeping

`Microsoft.CodeAnalysis.Analyzers` is pinned at **3.3.3** in the generator project against 3.3.4
centrally and 5.9.0 published, which predates RS1035/RS1036/RS1038/RS1041 — every rule written for
generator authors. Separately, `EnforceExtendedAnalyzerRules` is set on
`tools/Parquet.SourceGenerator.ApiGates/` and not on the shipping generator. Details, and why this
is distinct from #432, are in **#471**.

### The correction

The frequently repeated claim that `Microsoft.CodeAnalysis.Analyzers` warns "against holding
`ISymbol` or `Compilation` references inside incremental state pipelines" is **not accurate at any
version**. No shipped Roslyn analyzer inspects incremental-pipeline lambda return types for
retained symbols. RS1035/1036/1038/1041 are about banned APIs, references and target framework.

This is worth recording because the repository has two live instances of exactly that failure mode
— #395 (`GeneratorSyntaxContext` as a cached pipeline value) and #398 (`Location` inside
`DiagnosticInfo`) — and it would be easy to assume the #471 upgrade closes them. It does not. Both
were found by review, and nothing automated will find the next one.

Roslynator.Analyzers is optional: its value here overlaps `Meziantou.Analyzer`, which
`Directory.Build.props` already applies to every non-test project.

## 6. The Roslyn floor — filed as #472

Both #395 and #368 name `ForAttributeWithMetadataName` as the preferred fix, and #368 says it
resolves the two together. Neither can take that path at the current 4.0.1 pin.

`docs/28-BUILD-INCREMENTALITY-258.md` records that `WithTrackingName` fails to compile at both
4.0.1 and 4.8.0. Inspecting `lib/netstandard2.0/Microsoft.CodeAnalysis.dll` from the published
`Microsoft.CodeAnalysis.Common` packages:

| Microsoft.CodeAnalysis.Common | `WithTrackingName` | `ForAttributeWithMetadataName` |
|:---|:---:|:---:|
| 4.0.1 | absent | absent |
| 4.3.0 | **present** | **present** |
| 4.8.0 | **present** | **present** |

Both arrive in 4.3.0. Only the generator project's own `VersionOverride="4.0.1"` blocks them.
`docs/28`'s conclusion stands — raising the reference is still a consumer-compatibility decision —
but the price is one bump for three payoffs, not an unavailable API. Closed #258 records the
opposite version fact. See **#472**.

---

## Summary for the backlog

Nothing in the recommended tooling stack should be adopted as a package. What survives evaluation
maps onto work already tracked:

| Item | Where it lives |
|:---|:---|
| Indentation abstraction | #439 (prerequisite for #434) |
| Analyzer package upgrade + `EnforceExtendedAnalyzerRules` | #471 |
| Roslyn floor decision; `docs/28` correction | #472 (gates #395, #368) |
| Pipeline retention of Roslyn objects | #395, #398 |
| Golden coverage gaps | #413, #407 |
| `docs/03` drift, `docs/INDEX.md` completeness | #464 |

Of these, #471 is the only one with no consumer impact and no dependency on another decision.
