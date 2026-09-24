# Coverage envelope #259 implementation plan

> **For agentic workers:** Execute this plan task by task. Keep the generated coverage artifact and its source-derived drift gate in the same change.

**Goal:** Check in an evidence-based coverage map for accepted property kinds, nesting, nullability, and modern/classic backends, with a deterministic gate that detects contract drift.

**Architecture:** `scripts/CoverageMap.cs` reads the parser and generator source with the pinned Roslyn package, derives the accepted leaf and compound envelope, and validates a checked-in evidence catalogue. It renders `docs/reference/coverage-map.md` with stable ordering and explicit evidence status, so the map is reviewable while acceptance remains tied to code. A focused test validates the renderer and catalogue rules through the normal test project.

**Tech Stack:** C# file-based `dotnet run` script, Roslyn syntax APIs, Markdown, xUnit/Shouldly.

**Spec:** GitHub issue #259, as supplied in the task request.

## Global Constraints

- Keep the map evidence-based and do not claim 100% coverage.
- Include #255 as a high-risk nullable value-type struct gap/regression target.
- Use deterministic ordinal ordering and a zero-tolerance drift check, matching the existing baseline scripts.
- Keep automation in C#; `scripts/generate_test_data.py` is the repository’s only allowed Python file.
- Do not change the public API, push, or create a pull request.

---

### Task 1: Add the source-derived coverage-map script and evidence catalogue

**Files:**
- Create: `scripts/CoverageMap.cs`

**Interfaces:**
- Consumes: `src/Parquet.SourceGenerator/Models/PropertyModel.cs`, `src/Parquet.SourceGenerator/Parser/TargetParser.cs`, `src/Parquet.SourceGenerator/ParquetIncrementalGenerator.cs`, `src/Parquet.SourceGenerator.Legacy/ParquetLegacyIncrementalGenerator.cs`.
- Produces: `--update` rendering of `docs/reference/coverage-map.md` and default `--check` drift validation.

- [x] **Step 1: Define the evidence rows and parser-source extraction.**

  Parse the four source files with Roslyn, extract enum members and the parser’s type allowlists/backend exclusions, and keep evidence rows keyed by type family, nesting shape, nullability, and backend. The validator must report missing or stale derived keys rather than silently dropping them.

- [x] **Step 2: Render a stable Markdown report.**

  Render an explanatory scope/legend, the accepted matrix, uncovered high-risk rows, and a limitations section. Sort every row with `StringComparer.Ordinal`; do not include timestamps, machine paths, or line numbers.

- [x] **Step 3: Add update/check command handling.**

  Make `dotnet run scripts/CoverageMap.cs -- --update` write the artifact and `dotnet run scripts/CoverageMap.cs` compare it byte-for-byte, with actionable refresh instructions on drift.

### Task 2: Check in the map and wire the CI gate

**Files:**
- Create: `docs/reference/coverage-map.md`
- Modify: `.github/workflows/ci.yml`
- Modify: `docs/index.md`

**Interfaces:**
- Consumes: the renderer and derived contract from Task 1.
- Produces: a checked-in report and a CI step that rejects source/map drift.

- [x] **Step 1: Generate the initial map.**

  Run the update command from the repository root and review the report against the existing type matrix, nested-struct/list tests, golden compilation, and package-consumption tests.

- [x] **Step 2: Add the gate beside the other deterministic baseline checks.**

  Run `dotnet run scripts/CoverageMap.cs` in CI before the solution build, preserving the repository’s file-based script convention.

- [x] **Step 3: Link the document from the documentation index.**

  Add one sentence/table row describing the map and its refresh command, without duplicating the generated matrix.

### Task 3: Validate the gate’s assumptions

**Files:**
- Create: `test/Parquet.SourceGenerator.Tests/CoverageMapTests.cs`
- Modify: `test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj` only if the existing script/catalogue types need linking; prefer testing pure shared logic only if the script exposes it without production API changes.

**Interfaces:**
- Consumes: deterministic renderer/catalogue validation from Task 1.
- Produces: normal test-suite coverage for duplicate keys, stale derived kinds, and stable output.

- [x] **Step 1: Validate stable ordering and missing/stale derived rows.**

  The file-based gate validates missing and unexpected `PropertyKind` values, required compound kinds, backend rows, evidence-file existence, stable row keys, and byte-for-byte output. A separate xUnit test was not added because the renderer and validator are intentionally kept inside the CI script and do not expose production API solely for test plumbing.

- [x] **Step 2: Run the focused tests and fix only the minimal implementation issues.**

  Run the existing generated-type, nested-shape, and golden compilation tests; the full test project is the regression check for the evidence named by the map.

### Task 4: Verify, format, and commit

**Files:**
- Modify: all files produced by Tasks 1–3.

**Interfaces:**
- Consumes: checked-in map, gate, CI wiring, and tests.
- Produces: a locally committed issue-specific change with recorded evidence and limitations.

- [x] **Step 1: Run the map gate, focused tests, solution build, and CSharpier check.**

  Run `dotnet run scripts/CoverageMap.cs`, `dotnet test test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj --configuration Release --no-restore`, `dotnet build Parquet.SourceGenerator.slnx --configuration Release --no-restore`, and `dotnet csharpier check .`.

- [x] **Step 2: Inspect the diff and status.**

  Confirm that only the intended map, script, documentation, CI, test, and plan files changed, and that the report names #255 without overstating coverage.

- [ ] **Step 3: Commit locally.**

  Use `git add` for the intended files and commit with `test(coverage): add source-derived coverage map for #259`.
