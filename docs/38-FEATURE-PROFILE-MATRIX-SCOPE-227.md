# Feature-profile golden matrix (#227)

## Decision

The profile-by-model golden matrix is deferred. The repository already covers the default
profile through golden-model assertions, the derived source/API/metrics/call-graph review diff, AOT,
and compatibility contracts. Adding matrix rows before #225 settles the supported profile set would
multiply golden outputs without a stable configuration contract.

The later matrix must be profile × model × backend, include compile and round-trip checks, verify
`NetStandardCompat` against a real consumer, and report CI wall-clock cost. A single run
must produce all artifacts from one emitter state — as `scripts/DerivedOutputs.cs` does for the
default profile.

## Follow-up

Revisit #227 after #225 settles the supported profile set. The matrix must cover profile, model, and
backend combinations, with one workflow that refreshes all artifacts atomically.
