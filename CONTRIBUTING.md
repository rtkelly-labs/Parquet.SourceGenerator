# Contributing to Parquet.SourceGenerator

Thank you for your interest in contributing to **Parquet.SourceGenerator**! This guide outlines development setup, building, testing, benchmarking, and submitting pull requests.

---

## 🛠️ Development Setup

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher
- Python 3.11+ with [`uv`](https://github.com/astral-sh/uv) (for PyArrow cross-engine test dataset generation)

### Clone
```bash
git clone https://github.com/rtkelly13/Parquet.SourceGenerator.git
cd Parquet.SourceGenerator
```

> The solution file is named `Parquet.SourceGenertor.sln` — the typo is historical and kept so
> existing clones keep working.

---

## 🧪 Building & Testing

### 1. Build Solution
```bash
dotnet build Parquet.SourceGenertor.sln --configuration Release
```

### 2. Run Test Suite
```bash
dotnet test Parquet.SourceGenertor.sln --configuration Release
```

### 3. Run Test Suite with Code Coverage
```bash
dotnet test --collect:"XPlat Code Coverage" Parquet.SourceGenertor.sln
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

Benchmarks are not run in CI (BenchmarkDotNet on shared runners is too noisy to gate merges on)
and no baseline results have been committed, so there is nothing to compare against yet. Run them
locally when working on performance:

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
4. **PR Title**: Must follow Conventional Commits — CI validates it (`feat:`, `fix:`, `perf:`,
   `docs:`, `style:`, `refactor:`, `test:`, `chore:`, `ci:`, `build:`).
5. **Commit Message**: Use Conventional Commit messages too.

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
