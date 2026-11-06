## 📝 Description
Briefly describe the changes introduced by this pull request.

---

## 🧪 Verification Checklist
- [ ] All unit tests pass (`dotnet test`).
- [ ] Code is CSharpier-formatted (`dotnet csharpier format .`; no other formatter on C#).
- [ ] Native AOT sample builds cleanly (`dotnet run --project samples/Parquet.SourceGenerator.SampleAot/Parquet.SourceGenerator.SampleAot.csproj`).
- [ ] Performance benchmarks run without allocation or execution speed regressions.
- [ ] Documentation (`README.md` / `CHANGELOG.md`) updated if applicable.
- [ ] **API change contract**: `git diff -- '*.api.txt' src/api/seams.txt '**/PublicAPI.Unshipped.txt'`
  is empty, **or** every added signature has a `docs/api/LEDGER.md` entry with a semver bucket,
  rationale and alternatives considered (`docs/18-API-CHANGE-CONTRACT.md`). No
  `**Unapproved-by-design:**` marker remains — a PR carrying one cannot merge.
