# Required test count floors

The required `test` job writes distinct TRX reports for its core and external-interop
runs. `scripts/TestCountGate.cs` checks each against `test/required-test-floors.json`.
It counts executed, passed results and rejects any skipped or failed result, absent
report, invalid counter, or duplicate execution ID. Coverage thresholds remain
85% line and 70% branch.

The initial floors are 1,750 core cases and two external-interop cases. The green
[#652 run](https://github.com/rtkelly-labs/Parquet.SourceGenerator/actions/runs/37115753889)
reported 1,772 core cases and two external cases. Removing the three enum-membership
theories (14 cases) and seven unused prototype tests leaves 1,751 core cases before the new gate-wiring test (1,752 with it).
The core floor allows two cases of slack; unrelated runs are never summed.

Raising a floor needs no ledger entry. Lowering one requires an entry in
`docs/api/LEDGER.md` with the old and new count, removed behaviour, and reason.
Review actual TRX evidence before changing these values.

The gate's `--self-test` mode accepts complete reports at and above the floor, then
requires eleven invalid reports to fail (including zero tests, below-floor tests,
skips, failures, false counters, missing results, and duplicate IDs). CI runs these
controls before examining its reports. The `required-test-results` artifact retains
the reports even when the count gate fails.

The removed UTF-8 prototype was not used by emitted runtime code. Its historical
implementation remains in Git history; restoring it to the shipping path requires
behavioural tests of that path.
