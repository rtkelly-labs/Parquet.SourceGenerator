# Lake: the third pattern

| Pattern | Generator package | Parquet library | Consumer targets |
|---|---|---|---|
| Modern | `Parquet.SourceGenerator` | Parquet.Net 6.x | .NET 8 and later |
| Classic | `Parquet.SourceGenerator.Legacy` | Parquet.Net 4.x / 5.x | .NET Framework 4.7.2, .NET 8, .NET 9 |
| **Lake** | `Lake.Parquet.SourceGenerator` (planned) | **Lake.Parquet** | **.NET 10** |

**Lake.Parquet** is the [rtkelly-labs/parquet-dotnet](https://github.com/rtkelly-labs/parquet-dotnet) fork of
Parquet.Net, built and released from here. The fork itself stays upstream Parquet.Net plus its review units (so
upstream merges stay clean and every change can be proposed upstream as is); the Lake identity is applied at build
time, from this folder.

## What is here

| File | Role |
|---|---|
| `fork.json` | the fork commit Lake.Parquet is built from (`commit`), and the branch nightly builds follow (`branch`) |
| `overlay/Lake.Parquet.targets` | package and assembly name, `net10.0`, metadata, provenance check |
| `overlay/README.md` | the package's README |
| `release-notes.md` | Lake.Parquet's versions; the first heading is the version built |
| `package-layout/Lake.Parquet.txt` | the exact contents of the package (checked by `scripts/VerifyPackageLayout.cs`) |

## How the build works

`scripts/BuildLakeParquet.cs` does the whole thing, the same way every time:

1. fetches exactly the pinned fork commit (or `--ref <branch>`, or a local `--source <checkout>`) into
   `temp/lake/parquet-dotnet`, a fresh directory;
2. reads the upstream base the fork records (`docs/lake/upstream-base`: Parquet.Net commit and version);
3. applies the overlay, each step checked to match exactly once, so a fork change that moved the code fails here
   rather than producing a different package:
   - copies `Lake.Parquet.targets` to `src/Parquet/` and imports it as the last line of `Parquet.csproj`;
   - `created_by` in `ThriftFooter.cs` says `Lake.Parquet version …` (two lines);
   - `Globals.cs` gets the version and the fork commit;
   - the package README goes beside the project;
4. optionally (`--test`) runs the fork's test suite on the renamed build;
5. builds the package into `artifacts/lake/` (version from `release-notes.md`; the fork commit, upstream commit and
   upstream version as assembly metadata and in the release notes);
6. checks the package: id, version, `lib/net10.0/Lake.Parquet.dll` and its assembly name.

```bash
dotnet run scripts/BuildLakeParquet.cs -- --test                          # the pinned commit
dotnet run scripts/BuildLakeParquet.cs -- --source ../parquet-dotnet      # a local checkout's HEAD
```

`test/PackageConsumptionLake` then builds and runs the generator's consumption test on .NET 10 against the
packages just built, with Lake.Parquet in place of Parquet.Net.

## CI: `.github/workflows/lake.yml`

Its own workflow, outside `ci.yml`'s required checks:

- on changes to `lake/`, the script, the consumption test or the generator (`src/`): build Lake.Parquet at the pin
  with its tests, pack the generator, run the Lake consumption test, check the package layout;
- nightly: the same against the fork's `branch` head, so a fork change that breaks the Lake build or the generated
  code shows up before it is pinned;
- on demand with "Publish": the above at the pin, then Lake.Parquet to NuGet (`LAKE_NUGET_API_KEY` in the
  `lake-release` environment) and a GitHub release `lake-parquet-v<version>`.

## Releasing a new Lake.Parquet

1. In the fork: merge upstream (its `scripts/lake/sync-upstream.sh`, weekly in its CI) or new work into the branch.
2. Here: set `commit` in `fork.json` to that fork commit and add a section at the top of `release-notes.md` (the
   heading is the version; name the upstream base). Lake.Parquet follows semantic versioning of its own API:
   patch for fixes, minor for new API or an upstream minor release, major for anything breaking.
3. Merge, then run the "Lake" workflow with "Publish".

How the fork follows upstream and retires units as upstream accepts them is in the fork's
[docs/lake/maintaining.md](https://github.com/rtkelly-labs/parquet-dotnet/blob/ccr-87fadcf9-bxi9wr/docs/lake/maintaining.md).
