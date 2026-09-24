# Decompression Limits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add configurable compressed-page decompression size and expansion-ratio limits, enforce them before Parquet.Net page allocation in every generated reader, and prove deterministic hostile-file failures.

**Architecture:** Add the two settable limits to `ParquetSerializerOptions`, with defaults of 67,108,864 bytes and 500. Add one reusable emitter component that generates a private seekable stream guard into each modern and legacy extension; after Parquet.Net has initialized its footer, the guard parses page-header size fields on page seeks and throws `InvalidDataException` before Parquet.Net reads/decompresses page data. Route every generated modern and legacy read entry through the guard, then refresh generated goldens and all repository API/metrics artefacts required by their gates.

**Tech Stack:** C# source generators, generated C# targeting Parquet.Net 6.1.0 and 4/5-compatible APIs, xUnit/Shouldly, CSharpier, .NET SDK.

**Spec:** GitHub issue #315, with repository API contract in `docs/quality/api-change-contract.md`.

## Global Constraints

- Work only in `/Users/ryankelly/code/personal/Parquet.SourceGenerator/temp/parquet-source-generator-0.1-decompression` on `feat/decompression-limits-315`.
- Defaults are exactly `67_108_864` bytes (64 MiB) and `500`.
- Guards must run before page decompression/allocation in modern and legacy generated read paths.
- Valid compressed reads, including multi-page columns, must continue to work.
- Hostile violations must deterministically throw `InvalidDataException`.
- Every governed API catalogue addition requires a matching semver entry in `docs/api/LEDGER.md`.
- Do not push or create a PR; commit locally with an issue-specific message.

---

### Task 1: Add and catalogue the public options

**Files:**
- Modify: `src/Parquet.SourceGenerator.Attributes/ParquetSerializerOptions.cs`
- Modify: `src/Parquet.SourceGenerator.Attributes/PublicAPI.Unshipped.txt`
- Modify: `docs/api/LEDGER.md`
- Test: `test/Parquet.SourceGenerator.Tests/SerializerOptionsTests.cs`

- [ ] Add `int MaxDecompressedPageSize { get; set; } = 67_108_864` and `int MaxDecompressionExpansionRatio { get; set; } = 500` with XML docs describing their read-side scope.
- [ ] Add exact API signatures to `PublicAPI.Unshipped.txt` and one newest-first additive-minor ledger entry for issue #315, including alternatives considered.
- [ ] Add focused default/property tests and run the focused test filter.

### Task 2: Build the generated page guard

**Files:**
- Create: `src/Parquet.SourceGenerator/Emitter/Components/DecompressionGuardComponent.cs`
- Modify: `src/Parquet.SourceGenerator/Emitter/CodeEmitter.cs`
- Modify: `src/Parquet.SourceGenerator.Legacy/Emitter/LegacyCodeEmitter.cs`

- [ ] Emit a private `Stream` wrapper and compact-protocol page-header parser shared by both emitters.
- [ ] Make the guard inactive while `ParquetReader.CreateAsync` reads footer/schema metadata; activate it immediately after reader creation and before generated validation/read loops.
- [ ] On page seeks, parse type, compressed size, and uncompressed size without materializing page data; reject invalid sizes, `uncompressed > MaxDecompressedPageSize`, and `uncompressed / compressed > MaxDecompressionExpansionRatio` with stable `InvalidDataException` messages.
- [ ] Route all modern stream, memory, filtered, sorted, sequential, and parallel reader creations plus the legacy array reader through the wrapper, preserving caller-owned stream lifetime.
- [ ] Add emitter tests that assert guard emission and both option references for modern and legacy output.

### Task 3: Add hostile and valid compressed-path coverage

**Files:**
- Modify: `test/Parquet.SourceGenerator.Tests/Security/HostileParquetTests.cs`
- Modify: `test/Parquet.SourceGenerator.Tests/SerializerOptionsTests.cs`
- Add or modify generated test models only if a legacy/modern path needs a dedicated fixture.

- [ ] Produce a valid compressed file, patch a page header’s uncompressed size above the configured limit, and assert deterministic `InvalidDataException` before decompression across modern sequential stream, array, streaming, memory, filtered/sorted where applicable, parallel, and legacy generated reads.
- [ ] Patch compressed and uncompressed header sizes to exceed the configured expansion ratio and assert the stable ratio violation.
- [ ] Assert valid compressed data still round-trips with default limits.
- [ ] Run focused hostile, serializer, generated-code, and legacy tests.

### Task 4: Refresh generated artefacts and repository gates

**Files:**
- Modify: `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.g.cs`
- Modify: `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.metrics.txt`
- Modify: any generated API catalogue or checked-in gate artefacts reported by repository scripts.

- [ ] Regenerate goldens through the repository’s documented `UPDATE_GOLDEN_FILES=true` path.
- [ ] Refresh generated metrics only through `dotnet run scripts/CodeMetrics.cs` if the gate reports expected drift; do not hand-edit generated baselines.
- [ ] Run CSharpier check, API ledger check, focused tests, full relevant tests, and Release build with `-warnaserror`.
- [ ] Inspect the final diff and status, verify no other worktree/main changes, and commit with an issue-specific message such as `feat(safety): bound generated page decompression (#315)`.
