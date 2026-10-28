# Contributing to Parquet.SourceGenerator

Thank you for your interest in contributing to **Parquet.SourceGenerator**! This guide outlines development setup, building, testing, benchmarking, and submitting pull requests.

---

## 🛠️ Development Setup

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher, plus the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or SDK): the Paket local tool targets `net10.0`, and restoring the solution runs it for the Paket-managed benchmark project ([ADR 0004](docs/architecture/adrs/0004-paket-pilot.md))
- Python 3.11+ with [`uv`](https://github.com/astral-sh/uv) (for PyArrow cross-engine test dataset generation)

### Clone
```bash
git clone https://github.com/rtkelly13/Parquet.SourceGenerator.git
cd Parquet.SourceGenerator
```

---

## 🧪 Building & Testing

### 1. Build Solution
```bash
dotnet build Parquet.SourceGenerator.slnx --configuration Release
```

### Changing a dependency

Every project in the solution except the two Native AOT ones and the Paket-managed one (below) carries a committed `packages.lock.json`, and CI restores in locked mode, so a changed dependency graph fails the build instead of resolving silently (#394). After editing `Directory.Packages.props` or a `PackageReference`, run `dotnet restore Parquet.SourceGenerator.slnx --force-evaluate` and commit the changed lock files with the change. Dependabot's NuGet pull requests need the same step: its updater writes only the `netstandard2.0` section of the multi-targeted `src/Parquet.SourceGenerator.Runtime/packages.lock.json`, so they fail the locked restore with `NU1004`. Check out the Dependabot branch, run the command above, and push the regenerated lock files to it:

```bash
git fetch origin <dependabot-branch> && git switch <dependabot-branch>
dotnet restore Parquet.SourceGenerator.slnx --force-evaluate
dotnet restore Parquet.SourceGenerator.slnx --locked-mode   # must now pass
git commit -am "build(deps): regenerate NuGet lock files" && git push
```

Pushing to the branch stops Dependabot rebasing that pull request on its own; comment `@dependabot rebase` (then regenerate again) if it falls behind `main`.

#### Paket-managed projects (pilot)

`benchmarks/Parquet.SourceGenerator.Benchmarks` takes its packages from [Paket](https://fsprojects.github.io/Paket/), not from `Directory.Packages.props`: its `paket.references` names the packages, the root `paket.dependencies` holds their version ranges, and the root `paket.lock` holds the exact versions ([ADR 0004](docs/architecture/adrs/0004-paket-pilot.md)). Paket is a local tool that targets `net10.0` (see Prerequisites), so run `dotnet tool restore --disable-parallel` first. Then:

```bash
dotnet tool run paket outdated                  # what an update would take, inside the ranges
dotnet tool run paket update <package>          # move one package (and what it needs) within its range
dotnet tool run paket install                   # after editing paket.dependencies or a paket.references
dotnet tool run paket restore --fail-on-checks --force  # CI's check: fails if paket.dependencies and paket.lock disagree
```

CI runs `dotnet tool run paket restore --fail-on-checks` on a fresh checkout. Locally add `--force`: once a restore has succeeded, the cache in the ignored `paket-files/` short-circuits the check and it exits 0 even when the two files disagree.

Commit `paket.lock` (and `paket.dependencies` or `paket.references` if you changed them) with the change. Widening a range, for example taking a new major version, is an edit to `paket.dependencies` followed by `dotnet tool run paket update <package>`. Dependabot does not read Paket, so it never opens pull requests for these packages; the weekly `paket-outdated` workflow (`scripts/PaketOutdated.cs`) keeps one issue labelled `paket-outdated` listing what is out of date. Run `dotnet run scripts/PaketOutdated.cs -- --root . --out artifacts/paket-outdated.md --verdict artifacts/paket-outdated.txt` for the same report locally; like every `scripts/*.cs` file-based app, it needs the .NET 10 SDK, not only the runtime.

### 2. Run Test Suite
```bash
dotnet test Parquet.SourceGenerator.slnx --configuration Release
```

### 3. Run Test Suite with Code Coverage
```bash
dotnet test --collect:"XPlat Code Coverage" Parquet.SourceGenerator.slnx
```

### 4. Run the AOT sample
```bash
dotnet run --project samples/Parquet.SourceGenerator.SampleAot/Parquet.SourceGenerator.SampleAot.csproj --configuration Release
```

This exercises the generated code, but it does **not** verify Native AOT: `PublishAot` has no
effect on `dotnet run`, which executes under CoreCLR.

### 5. Verify Native AOT properly

Putting the AOT compiler through it needs a publish with a runtime identifier, plus the native
toolchain (`clang`, `zlib`) on your machine:

```bash
dotnet publish test/Parquet.SourceGenerator.AotTest/Parquet.SourceGenerator.AotTest.csproj \
  --configuration Release -r linux-x64 -o ./aot-out
./aot-out/Parquet.SourceGenerator.AotTest
```

`AotTest` round-trips a Parquet stream through the generated serializer and throws on mismatch, so
the native binary exiting `0` is the actual result. CI runs exactly this on every pull request, for
`linux-x64`; other runtime identifiers are untested.

Expect `IL2104` and `IL3053` warnings against `Parquet.dll` during the publish. They come from
Parquet.Net, not from generated code, and are not currently treated as errors — the generated code
is reflection-free but the library beneath it is not. If you add a step that trips new IL warnings
attributed to *this* repo's assemblies, that is a real regression worth chasing.

---

## 📊 Running Benchmarks

Wall-clock benchmarks are not gated in CI (BenchmarkDotNet timings on shared runners are too noisy
to gate merges on). A small, allocation-only set is: the weekly `benchmarks.yml` regression gate
compares allocated bytes against `benchmarks/baseline.json` (see [docs/BENCHMARKS.md](docs/BENCHMARKS.md)).
It is not a PR check. Run the suite locally when working on performance:

```bash
dotnet run -c Release --project benchmarks/Parquet.SourceGenerator.Benchmarks/Parquet.SourceGenerator.Benchmarks.csproj -- --filter "*"
```

If you publish numbers anywhere, include the machine and runtime they came from.

---

## 📥 Submitting Pull Requests

1. **Create a Feature Branch**: `git checkout -b feat/your-feature-name`
2. **Code Style**: CSharpier is the only C# formatter (`dotnet csharpier format .`, checked in CI
   via `dotnet csharpier check .`). Do not run `dotnet format` on C# — its Roslyn formatter
   conflicts with CSharpier and `IDE0055` is disabled in `.editorconfig` by design.
3. **Tests**: The suite must pass cleanly (`dotnet test`).
4. **Derived outputs**: emitted golden source and API, code metrics, duplication and call-graph
   edges are generated, not committed (`dotnet run scripts/DerivedOutputs.cs` writes them to the
   gitignored `artifacts/`). CI posts their diff against the merge base as one comment on the pull
   request; check its "Emitted public API" section shows only the changes you intend
   ([docs/17](docs/17-GENERATED-API-BASELINES.md), [docs/18](docs/18-API-CHANGE-CONTRACT.md)).
5. **PR Title**: Must follow Conventional Commits — CI validates it (`feat:`, `fix:`, `perf:`,
   `docs:`, `style:`, `refactor:`, `test:`, `chore:`, `ci:`, `build:`).
6. **Commit Message**: Use Conventional Commit messages too.

### Stacked pull requests

Dependent work lands as a GitHub native stack: the bottom pull request targets `main`, each one
above targets the branch of the one below, and every layer has its own green CI.

- **Link** an existing chain by labelling its bottom pull request `stack:link`. The `stack`
  workflow walks the chain upward and runs `gh stack link` (or dispatch it with explicit numbers).
  Locally: `gh stack link <bottom> ... <top>`.
- **Keep layers linear.** Edit the layer that owns a change, then `gh stack rebase --upstack`.
- **Merge** with `gh stack merge <top> --squash --yes`. `gh pr merge` and auto-merge are refused
  for stacked pull requests.

---

## 🚀 Releasing

`CHANGELOG.md` is the release authority. There is deliberately no version input on the release
workflow: a version that is not cut in the changelog cannot be published.

To release:

1. **Cut the release in a PR**: move everything under `[Unreleased]` into a new heading directly
   below it — `## [x.y.z] - YYYY-MM-DD` (valid SemVer 2.0.0, ISO date required) — and leave a
   fresh, empty `## [Unreleased]` on top. CI validates this structure via
   `dotnet run scripts/ParseChangelog.cs`.
2. **Merge** the PR to `main`, then trigger **Actions → Release & Publish NuGet → Run workflow**.
   Optionally tick **dry run** to build, pack and verify without publishing anything.
3. The workflow then, in order:
   - parses the first cut version section — the source of both the version and the release
     notes — and refuses to continue if its `v<version>` tag already exists;
   - builds with that version and runs the full verification battery (tests, samples, native
     AOT, package layout, package consumption on .NET 8/9/472);
   - publishes to NuGet.org via Trusted Publishing (the `publish` job deploys to the `release`
     environment; required reviewers are an org-only feature and GitHub forbids approving your
     own deployment anyway, so on a solo project the real brake is the manual dispatch plus the
     full verification battery in the `build` job — nothing publishes that was not built and
     consumed from these packages);
   - for **full releases**, creates the GitHub release `v<version>` with the changelog section
     as its body and the `.nupkg` files attached; **prereleases** (e.g. `0.1.0-rc.1`) go to
     NuGet.org only, with no tag and no GitHub release.

```bash
gh workflow run release.yml            # real release, version read from CHANGELOG.md
gh workflow run release.yml -f dry_run=true   # rehearsal
```

---

## 📄 License
By contributing to **Parquet.SourceGenerator**, you agree that your contributions will be licensed under the [MIT License](LICENSE).
