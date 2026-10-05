# 0.1 Milestone & Release Status Index

> **Release Tracker:** [#477](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/477) • 
> **Design Contract:** [47 - 0.1 Contract & Design Goals](./docs/47-0.1-CONTRACT-AND-DESIGN-GOALS.md) • 
> **CI Verification:** [51 - CI Gate Matrix](./docs/51-CI-GATE-MATRIX.md)

| Attribute | Current Status |
|:---|:---|
| **Main Commit** | `c11d901` |
| **Last Verified** | 2026-10-05 |
| **Release Decision** | 🛑 **Blocked** (3 blocking gates remaining on 0.1 critical path) |

---

## 1. Release Gates Status (Audited Against docs/47 §8)

| Gate / Area | Status | Tracking Issue / PR | Primary Owner / Evidence |
|:---|:---:|:---|:---|
| **1. Non-Vacuous Test & CI Gates** | 🟢 Green | [#416](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/416), [#422](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/422), [#407](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/407), [#404](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/404), [#412](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/412), [#413](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/413) | `CiGateIntegrityTests` passing; `build` aggregate enforces all legs on `main` at `c11d901` |
| **2. Security & Decompression Limits** | 🟢 Green | [#414](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/414), [#415](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/415), [#437](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/437) | Zip-bomb & corrupted-file limits enforced in `CorruptedParquetTests` & `DecompressionBombSecurityTests` |
| **3. Public API Contraction** | 🛑 Blocked | [#459](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/459), [#461](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/461), [#478](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/478)–[#481](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/481) | Blocked on single reader type (#489) and unified batch (#576) |
| **4. Stability Classification** | 🟡 Pending | [#482](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/482) | Explicit stability attributes for feature levels & serializer options |
| **5. Legacy Backend Parity** | 🛑 Blocked | [#490](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/490), [#600](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/600), [#494](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/494), [#495](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/495), [#601](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/601) | Blocked on legacy test execution across primitive types and documented subset |
| **6. Arrow Status Alignment** | 🟢 Green | [#178](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/178), [#267](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/267) | Arrow ingestion verified; export documented as unreleased / non-goal |
| **7. Nested Support Scope** | 🟢 Green | [#176](https://github.com/rtkelly-labs/Parquet.SourceGenerator/issues/176), [doc 42](./docs/42-NESTED-BACKEND-SCOPE-176.md) | Formally scoped to documented, tested backend matrix |
| **8. Package & Release Mechanics** | 🟢 Green | [#652](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/652), [#670](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/670), [#671](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/671), [#667](https://github.com/rtkelly-labs/Parquet.SourceGenerator/pull/667) | Verified: NuGet packaging, AOT (`linux-x64`), `net472-consumer`, deterministic symbols |

---

## 2. Top 3 Release Blockers

### 1. #489 — Single Generated Reader Type & Drop `ToListAsync`
- **Impact:** Eliminates dual reader state explosion and accidental allocations. Directs callers to `ToArrayAsync()` or `AsAsyncEnumerable()`.
- **Next Action / Acceptance Check:** Consolidate emitted reader into a single entry-point reader type, remove `ToListAsync` from the public contract, and verify `PublicAPI.Unshipped.txt` and `*.api.txt` diffs pass CI.

### 2. #576 — Unified `<Model>Batch` & Buffer Lease Lifetime (#369)
- **Impact:** Replaces fragmented batch shapes with a single value-type `<Model>Batch` struct. Enforces safe buffer pooling without lifetime leaks.
- **Next Action / Acceptance Check:** Implement unified batch representation with explicit buffer return semantics (`#369`); verify zero leaks under memory-constrained tests.

### 3. #600 / #494 / #495 — Legacy Backend Parity & Executed Test Suite
- **Impact:** Guarantees Parquet.Net v4/v5 users have an explicit, green, executed subset rather than untested compile-only parity.
- **Next Action / Acceptance Check:** Execute integration test suite against `PackageConsumptionLegacy` across the 23 supported types; compile-time reject unsupported feature combinations with clean diagnostics.

---

## 3. Critical Path Dependency Graph

```text
#489 (Single Reader Type & Drop ToListAsync)
  │
  ▼
#576 (Unified <Model>Batch) + #369 (Buffer Lease Ownership)
  │
  ▼
Modern Trims (#584, #512, #585)
  │
  ▼
#600 (Legacy Parity Target)
  │
  ▼
#494 (Legacy Integration Tests) ──► #495 + #601 (Legacy Parity Suite)
                                            │
                                            ▼
                                  #619 + #620 (Doc & Readme Finalization)
                                            │
                                            ▼
                                     0.1 Release Cut
```

---

## 4. Since Last Update (Merged PRs)

- **#671**: Updated CI gate integrity tests and scoped modern v3 fixtures.
- **#670**: Enforced strict CI build aggregate dependencies and gate protection.
- **#667**: Hardened deterministic symbol packaging and compiler artifacts.
- **#652**: Standardized multi-target packaging and release distribution mechanics.
- **#662, #661, #660, #644**: Decompression bomb security bounds, test fixture isolation, and compiler diagnostic cleanup.

---

> ⚠️ **Governance Rule:** Every 0.1 PR must update the relevant row in this index or explicitly state in the PR description why release status is unchanged.
