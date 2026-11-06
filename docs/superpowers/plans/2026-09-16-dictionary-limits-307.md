# Dictionary and String Read Limits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add configurable dictionary-entry and UTF-8 string-length limits that reject hostile Parquet input deterministically before generated readers materialize unbounded strings, across modern and legacy generated read paths.

**Architecture:** Keep the public policy in `ParquetSerializerOptions`, emit the checks at the generated row-group/column boundary immediately before Parquet.Net read calls, and centralize modern string materialization through the existing raw UTF-16 path so byte length is checked before `ToString()`. The classic emitter will use its metadata/read boundary and preserve the same `InvalidDataException` contract. Refresh generated goldens and metrics through the repository’s existing deterministic test/tooling gates.

**Tech Stack:** C# source generators, Parquet.Net 6.1.0 and legacy 4.25.0 generated APIs, xUnit/Shouldly, CSharpier, Roslyn API analyzers, checked-in generated goldens and metrics.

**Spec:** GitHub issue #307 and `docs/18-API-CHANGE-CONTRACT.md`.

## Global Constraints

- `MaxDictionaryEntries` defaults to `1_000_000`.
- `MaxStringLengthBytes` defaults to `1_048_576` and measures UTF-8 encoded bytes.
- Limit failures are deterministic `System.IO.InvalidDataException` failures with stable messages naming the offending column, observed value, and configured maximum.
- Checks must execute before generated code materializes dictionary-backed or string values.
- Both modern and legacy emitters, including their generated read overloads that share the read emitters, must enforce the contract.
- Every governed API catalogue addition requires a matching `docs/api/LEDGER.md` semver entry; no unapproved-by-design escape hatch may remain.
- Generated `.g.cs` files, `.api.txt` files, and metrics baselines must be refreshed only through the repository’s existing golden/metrics commands.
- Do not modify other worktrees, `main`, or push/create a PR.

---

### Task 1: Add the public safety options and API-contract records

**Files:**
- Modify: `src/Parquet.SourceGenerator.Attributes/ParquetSerializerOptions.cs`
- Modify: `src/Parquet.SourceGenerator.Attributes/PublicAPI.Unshipped.txt`
- Modify: `docs/api/LEDGER.md`

**Interfaces:**
- Produces `ParquetSerializerOptions.MaxDictionaryEntries` (`int`, get/set, default `1_000_000`).
- Produces `ParquetSerializerOptions.MaxStringLengthBytes` (`int`, get/set, default `1_048_576`).

- [x] **Step 1: Write the failing option-default test** in `SerializerOptionsTests.cs` asserting `Default` returns both exact defaults and that object initializers can override them.
- [x] **Step 2: Run the focused test** with `dotnet test test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj --filter FullyQualifiedName~SerializerOptionsTests` and observe the missing members/default failure.
- [x] **Step 3: Add documented get/set properties** beside `MaxAllocationValues`, preserving the existing mutable-options/netstandard object-initializer pattern.
- [x] **Step 4: Add the two canonical signatures** to `src/Parquet.SourceGenerator.Attributes/PublicAPI.Unshipped.txt`, ordinal-sorted with the existing options signatures.
- [x] **Step 5: Add a newest-first `#307` additive-minor ledger entry** explaining that the options cap dictionary-backed reads and UTF-8 string materialization for untrusted input.
- [x] **Step 6: Re-run the focused option test** and confirm it passes.

### Task 2: Enforce limits in modern generated readers

**Files:**
- Modify: `src/Parquet.SourceGenerator/Emitter/CodeEmitter.cs`
- Modify: `src/Parquet.SourceGenerator/Emitter/Components/StringDeduplicatorComponent.cs`
- Modify: `src/Parquet.SourceGenerator/Emitter/Compound/CompoundMapping.cs` if the shared packed-string materialization site needs the guard

**Interfaces:**
- The modern emitter’s shared read code emits checks for every modern overload: stream, memory, sequential, parallel, pruned, batch, compound, and list paths.
- The shared string helper accepts the configured options and validates UTF-8 byte count before calling `ReadOnlyMemory<char>.ToString()` or returning a decoded string.

- [x] **Step 1: Add hostile modern tests** for dictionary and oversized UTF-8 strings, covering normal, array, stream, memory, parallel, and deduplication-enabled paths where applicable; assert exact exception type and stable message fragments.
- [x] **Step 2: Run those tests before implementation** and confirm the hostile input currently succeeds or fails with a non-contract exception.
- [x] **Step 3: Emit a shared pre-read dictionary guard** using the row-group column metadata available to both direct and batch readers; reject invalid/excess dictionary metadata before `ReadRawAsync`/`ReadAsync`.
- [x] **Step 4: Route modern string decoding through one bounded raw-string materializer** that computes `Encoding.UTF8.GetByteCount(span)` and throws before string allocation; preserve nullable definition-level placement and the existing deduplicator identity behavior.
- [x] **Step 5: Apply the same bounded check to packed string lanes** used by compound and list reconstruction before `ToString()` is emitted.
- [x] **Step 6: Re-run the hostile tests**, including with `DeduplicateStrings = true`, and confirm deterministic `InvalidDataException` results.
- [x] **Step 7: Run the existing modern string, compound/list, memory, parallel, and golden regression filters** to detect behavior or generated-shape regressions.

### Task 3: Enforce limits in legacy generated readers

**Files:**
- Modify: `src/Parquet.SourceGenerator.Legacy/Emitter/LegacyCodeEmitter.cs`

**Interfaces:**
- Legacy generated `ReadParquetAsync` and `ReadParquetArrayAsync` enforce the same public option names and exception message contract; list overloads do not exist in the classic emitter and must not be invented.

- [x] **Step 1: Add a legacy emitter hostile-shape test** using the existing `LegacyRecord` model and Parquet.Net 4.25.0 generated API shape.
- [x] **Step 2: Run the legacy-focused tests before implementation** and confirm the current generated path does not honor the new limits.
- [x] **Step 3: Emit the legacy pre-read dictionary metadata guard and bounded string validation at the earliest API boundary supported by Parquet.Net 4.25.0.** Ensure the validation runs before the generated result array and before the legacy column read/materialization call where metadata permits it.
- [x] **Step 4: Re-run legacy hostile and round-trip tests and confirm no regression in nullable strings, binary data, or API shape.

### Task 4: Refresh generated artifacts and quality gates

**Files:**
- Modify: `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.g.cs`
- Modify: `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.api.txt` only if the public emitted surface changes
- Modify: `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.metrics.txt`
- Modify: `metrics/*.metrics.txt` and/or `metrics/duplication.txt` only when the repository gates report deterministic drift

**Interfaces:**
- Checked-in generated output and baselines exactly match the current emitters; catalogue changes are accompanied by the ledger entry from Task 1.

- [x] **Step 1: Refresh goldens** with `UPDATE_GOLDEN_FILES=true dotnet test test/Parquet.SourceGenerator.Tests/Parquet.SourceGenerator.Tests.csproj --filter FullyQualifiedName~GoldenCodeGenRegressionTests`.
- [x] **Step 2: Refresh generated metrics** with `UPDATE_GOLDEN_FILES=true dotnet run scripts/CodeMetrics.cs`.
- [x] **Step 3: Run CSharpier formatting/checking** using the repository tool invocation and inspect the diff for unrelated changes.
- [x] **Step 4: Run focused hostile, options, string, legacy, and golden tests.
- [x] **Step 5: Run the full relevant solution tests and `dotnet build Parquet.SourceGenerator.slnx --configuration Release -warnaserror`.
- [x] **Step 6: Run `dotnet run scripts/CheckApiLedger.cs -- --base main` plus the repository duplication/metrics gates required by the changed files.
- [x] **Step 7: Review `git diff`, verify only the isolated worktree changed, and commit with an issue-specific message such as `feat(safety): bound dictionary and string reads (#307)`.
