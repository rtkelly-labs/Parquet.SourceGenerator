# 55 - SonarAnalyzer.CSharp removal

> Companion to [50](./50-GENERATED-CODE-ANALYSIS.md) (the emitted-code analyzer gate) and
> [51](./51-CI-GATE-MATRIX.md) (the gate matrix). This page records why the Sonar analyzer left
> the repository, which of its rules had a counterpart elsewhere, and what was dropped.

## Decision

`SonarAnalyzer.CSharp` (10.35.0.4138) is removed. The repository owner decided this.

The package is licensed under the SONAR Source-Available License v1.0, not a permissive license.
Its grant covers only a "Non-competitive Purpose", and that definition excludes, among other
things, using AI technology that is not part of the Program to ingest, interpret, analyze or
interact with the data the Program provides. This repository's workflow has AI agents read and
fix analyzer diagnostics, so the package could not be used here without breaching its terms.
The summary above is a paraphrase, not legal advice. The licence text ships in the package as
`licenses/LICENSE.txt`.

The analyzers that remain are permissively licensed or part of the .NET SDK:

| Analyzer | License | Role |
|---|---|---|
| .NET SDK analyzers (CA, IDE) | MIT | `AnalysisLevel` 9.0-recommended, code style under `EnforceCodeStyleInBuild`, metric gates |
| `Meziantou.Analyzer` | MIT | `MA` rules |
| `Roslynator.Analyzers` | Apache-2.0 | `RCS` rules |
| `Microsoft.CodeAnalysis.Analyzers` and the API gates | MIT / repo | generator-author and API-contract rules |

Not covered by this change: DeepSource and CodeRabbit are separate services that run outside the
build, on pull requests. They are neither packages nor part of this repository's dependency set,
and their terms are a separate question.

## Nothing licensed by Sonar ships

The analyzer was referenced from `Directory.Build.props` with `PrivateAssets="All"`, so it never
flowed to a consumer as a dependency. The generator package sets `IncludeBuildOutput` to false
and packs only the generator assembly into `analyzers/dotnet/cs`
(`src/Parquet.SourceGenerator/Parquet.SourceGenerator.csproj`), so no analyzer assembly from a
build-time package is in a `.nupkg`. This was confirmed by reading the packaging configuration,
not by opening a produced package.

## What was removed

- the `PackageReference` (`Directory.Build.props`) and `PackageVersion` (`Directory.Packages.props`);
- the "SonarAnalyzer.CSharp configuration" section of `.editorconfig`, six `S` severity lines;
- `analysis/SonarLint.xml`, its `AdditionalFiles` entry, and the explanation of Sonar's own
  generated-code recogniser in `analysis/Directory.Build.props`. The harness now needs two
  switches, not three, to make the remaining analyzers report on emitted code;
- every mention of Sonar as part of the analyzer set in docs 44, 50, 51 and the index.

There were no `#pragma warning disable S...`, `[SuppressMessage]` or `NoWarn` entries for an `S`
rule anywhere in source or tests, so zero suppressions were removed. Three source comments and one
test summary named `S` rule ids; they now name the replacing rule. Historical records (the
changelog, `docs/api/LEDGER.md`, the first-run table in 50) keep the `S` ids they were written with.

## Rule map

Sonar's default rule set runs when the package is present, which is several hundred checks. The
map covers every rule the repository configured, every rule that fired or was fixed in its
history (docs 50, the git log and the changelog), and the rules named in the removal brief.
For every other default rule, `main` was green under `-warnaserror` with the package still present
(the commit this branch started from, `8eb8dd5`, built clean in CI), so there were no open findings
and removal opens no backlog. Their checks are simply no longer made.

Replacement severity is the one the removed rule had in practice: a warning, so a finding fails
CI under `-warnaserror`. The new entries are in the `src/` and emitted-code section of
`.editorconfig`, so they apply to emitted code through the `generated-analysis` gate as well.

| Rule | What it checked | Replacement | Note |
|---|---|---|---|
| S1186 | A method with an empty body and no comment saying why | dropped | No analyzer here has this check. The emitted `DecompressionGuardStream` contract is covered by its `Stream` tests |
| S1172 | A private method that never uses one of its parameters | RCS1163 (warning) | |
| S4581 | `new Guid()` where `Guid.Empty` says the same thing | MA0067 (warning) | |
| S2681 | A multi-line body under `if` or a loop with no braces, so only the first line is controlled | RCS1001, RCS1003 | RCS1001: warning for emitted code (0 findings), default `suggestion` in `src/` (34 findings, see below). RCS1003: warning in `src/` (1 fixed), `suggestion` for emitted code (22 findings) |
| S8969 | A null-forgiving `!` on a value the compiler already knows is non-null | RCS1249 (warning) | 4 findings fixed, see below |
| S6966 | A synchronous call (`Cancel`) where an awaitable form exists inside an async method | CA1849 (warning) | A probe confirmed it reports `CancellationTokenSource.Cancel()` in an async method. MA0042 also does, but it reports 62 emitted `using` statements that could be `await using`, so it stays at its default |
| S4456 | Argument checks inside an iterator, which run on the first `MoveNext` instead of the call | MA0050 (warning) | |
| S3626 | A `return` or `continue` that is the last statement and does nothing | RCS1134 (warning) | |
| S1185 | An override that only forwards to the base member | RCS1132 (warning) | |
| S3398 | A private static method used only by one nested type, which should live there | dropped | Layout preference, no counterpart |
| S1854 | A value assigned to a local and never read | IDE0059 (warning) | CS0219 already covered the never-read local |
| S1481 | A local variable that is never used | CS0219, IDE0059 | Compiler warning, already on |
| S1104 | A public mutable field | CA1051 | Already a warning |
| S3923 | Every branch of a conditional doing the same thing | dropped | No counterpart |
| S108 | An empty block | RCS1075 (warning, already on) | Partial: only an empty `catch` of `System.Exception` is a warning. MA0090 (empty `else` or `finally`) is `suggestion` by default; other empty blocks are no longer flagged |
| S3267 | A `foreach` that could be a LINQ query | dropped | Was `none` (LINQ allocates; contradicts the generator's allocation rules). Nothing to carry |
| S1643 | String concatenation in a loop | dropped | Was `suggestion`. MA0028 covers `StringBuilder` use and stays at its default |
| S4136 | Overloads not adjacent | dropped | Was `none` |
| S2094 | An empty class | dropped | Was `none` (marker types) |
| S6608 | `First()`, `Last()` and similar on an indexable list | dropped | Was `suggestion`. MA0098 and CA1826 cover it at their defaults |
| S125 | Commented-out code | dropped | Was `none` (explanatory code in comments) |

## What the replacements found

Every replacement was enabled at `warning`, then the solution and the emitted-code harness were
built to count findings. The rule from the brief: fix a replacement that reports fewer than ten
cheap findings, otherwise leave it below `warning` and file it.

| Replacement | `src/` | Emitted code | Outcome |
|---|---:|---:|---|
| RCS1163, RCS1134, RCS1132, MA0050, MA0067, CA1849, IDE0059 | 0 | 0 | Warning everywhere they apply |
| RCS1249 | 3 (`TargetParser.cs`, a redundant `!` on an `out` variable; two arrived from `main` while this change was open) | 1 (`null!` appended to a `List<string?>`) | Fixed in both, warning everywhere. The emitter now writes a plain `null` when the list element type is annotated, and keeps `null!` for a nullable array element (its list is unannotated) |
| RCS1003 | 1 (`TargetParser.cs`, an unbraced `if` arm of a braced `else`) | 22 | `src/`: fixed, warning. Emitted: `suggestion` (the emitter writes one-line `if` arms) |
| RCS1001 | 34 (`Attributes/VectorizedColumnTransforms.cs`, `NullableColumnExtractor.cs`, `TargetParser.cs` and four more files) | 0 | Emitted: warning. `src/`: left at its default `suggestion`, over the ten-finding limit |
| MA0042 | not run | 62 | Not adopted. Its extra reports (`using` that could be `await using`) are a different check from S6966 |

The `generated-analysis` gate stays at zero findings. The rules left at `suggestion` are a follow-up:
[#635](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/635).

The seeded violation in `GeneratedCodeAnalysis.cs --self-test` uses CA2007 and MA0004, not an `S`
rule, so it is unaffected. No test asserted on an `S` rule id. The new rules have no seeded
positive control of their own, except that a one-off probe showed CA1849 and MA0042 reporting
`Cancel()` in an async method.

## Verification

- `dotnet build Parquet.SourceGenerator.slnx -warnaserror`: 0 warnings, 0 errors.
- `dotnet run scripts/GeneratedCodeAnalysis.cs`: 7 emitted files examined, 0 findings in 0 rules.
- `dotnet run scripts/GeneratedCodeAnalysis.cs -- --self-test`: the seeded CA2007 violation is caught.
- `CiGateIntegrityTests`, `AsyncIteratorContractTests` and `GoldenCodeGenRegressionTests`: 40 passed.
- The full test suite and the net472 checks run in CI, not locally.
