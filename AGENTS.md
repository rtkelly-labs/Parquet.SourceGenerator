# PARQUET.SOURCEGENERATOR

## 🛑 Repository Conventions & Workflow Policy

1. **Squash Merge Only**: All pull requests must be merged into `main` using **Squash and Merge** exclusively.
2. **Delete Branch on Merge**: Feature branches must be automatically deleted immediately upon merge into `main`.
3. **Linear History**: Maintain a strictly linear history. Rebase feature branches onto `main` before merging.
4. **Direct Push Protection**: Direct pushes to `main` are blocked; PR mechanism required.
5. **Local Temp & Worktree Directory**: All temporary files, databases, and worktrees go in `/temp/` (gitignored).
6. **Gitignored Local TODO File**: A root `TODO.md` file MUST exist for local task tracking and be gitignored.
7. **Auto-Merge Enabled**: PRs may enable auto-merge (squash) so they merge automatically once required checks pass.
8. **Pinned Action SHAs**: All GitHub Actions workflows MUST use 40-character commit SHAs instead of mutable tags.
9. **Tool Restoration via Manifest**: All CLI diagnostic, formatting, and analysis tools are tracked in `.config/dotnet-tools.json` (`csharpier`, `ilspycmd`, `dotnet-inspect`, `dotnet-dump`). Always restore local tools using:
   ```bash
   dotnet tool restore --disable-parallel
   ```
   *(Or `~/.dotnet/dotnet tool restore --disable-parallel` when targeting local .NET 10 preview runtimes).*
   The `--disable-parallel` flag is strictly required to prevent package extraction race conditions between CLI tools sharing package name prefixes.
10. **Diagnostics Invocation**: Always execute installed tools via `dotnet tool run <command>` (e.g. `dotnet tool run ilspycmd`, `dotnet tool run dotnet-inspect`, `dotnet tool run dotnet-dump`) or via dedicated repository runners (`scripts/InterrogateIL.cs`, `scripts/TriageMemoryDump.cs`).

11. **API Change Contract**: Three API surfaces are governed — the emitted consumer API
    (`*.api.txt` beside each golden file, gated by `PARQAPI001`), the shipped package API
    (`PublicAPI.Unshipped.txt`, gated by `RS0016`) and internal seams (`src/api/seams.txt`, gated
    by `PARQAPI002`). Nothing enters any of them without **both** a catalogue line and a
    `docs/api/LEDGER.md` entry recording its semver bucket. All three gates are **build errors**,
    not test failures. Body, performance and comment changes alter the golden `.g.cs` but not the
    `.api.txt`, and are not API changes. A ledger entry marked `**Unapproved-by-design:**`
    suppresses the build error for spikes and cannot merge to `main`. Full rule:
    `docs/18-API-CHANGE-CONTRACT.md`.

12. **Upstream Dependency Limitations**: Track package limitations and candidate upstream improvements in `UPSTREAM_DEPENDENCY_LIMITATIONS.md`, not in this file.
