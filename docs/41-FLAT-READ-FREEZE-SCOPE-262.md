# Flat-read API freeze (#262)

## Decision

The flat read methods remain in the current compatibility window. Removing them before the
builder and its replacement seams have had a release would make the first frozen release the first
real-world migration point, and would leave the write side intentionally asymmetric.

The `0.1.0` freeze does not remove these methods. Update D3 and the release records when a later
compatibility window has provided a replacement and migration path.

## Removal gate

Treat removal as later compatibility-window work. #244 must prove parameter-level shrinkage, and #219
must settle write symmetry or its explicit deferral. The final freeze must record the breaking surface
in the ledger. Then remove the forwarders from the modern emitter, update D3 in the API contract, and
publish migration guidance. The classic emitter has no builder replacement, so retain its flat methods
until a legacy replacement exists. An `[Obsolete]` release is not useful unless callers have a release in
which they can move.

Keep the forwarders until the removal gate is met.
