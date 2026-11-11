# Pull Request

## Summary

<!-- Briefly describe the changes introduced in this PR and their rationale. -->

## Linked Issues

<!-- Link any relevant issues below (e.g. Closes #123, Fixes #456). -->
- Resolves:

## Type of Change

- [ ] `type: bug` (non-breaking bugfix)
- [ ] `type: feature` (non-breaking enhancement)
- [ ] `breaking-change` (fix or feature that breaks existing behavior or API)
- [ ] `type: docs` (documentation only)
- [ ] `type: maintenance` (refactor, dependency updates, CI improvements)

## Breaking Change Impact & Migration (if applicable)

<!-- If breaking-change is selected above, describe the impact and migration path. -->

## AI Utilisation Disclosure (if applicable)

<!-- If a substantial portion of this PR was generated with AI assistance, disclose it here. -->
- [ ] AI-assisted contribution (disclosed with relevant git trailers or notes).

## 🧪 Verification Checklist

- [ ] My branch is rebased onto latest `main` with a clean linear history.
- [ ] All required CI checks pass (`build`, `pr-title`, `protected-paths`).
- [ ] All unit tests pass (`dotnet test`).
- [ ] Code is CSharpier-formatted (`dotnet csharpier format .`; no other formatter on C#).
- [ ] Native AOT sample builds cleanly (`dotnet run --project samples/Parquet.SourceGenerator.SampleAot/Parquet.SourceGenerator.SampleAot.csproj`).
- [ ] Performance benchmarks run without allocation or execution speed regressions.
- [ ] Documentation (`README.md` / `CHANGELOG.md`) updated if applicable.
- [ ] **API change contract**: `git diff -- src/api/seams.txt '**/PublicAPI.Unshipped.txt'`
  is empty, **or** every added signature has a `docs/api/LEDGER.md` entry with a semver bucket,
  rationale and alternatives considered (`docs/18-API-CHANGE-CONTRACT.md`). No
  `**Unapproved-by-design:**` marker remains — a PR carrying one cannot merge.
- [ ] **Emitted API**: the "Emitted public API" section of the derived-output comment on this PR
  shows only the changes this PR intends to make to the generated consumer surface.
