# Write builder symmetry (#219)

The symmetric write builder is deferred. The current four write entry points are stable, covered by
generated API baselines, and do not block the current safety, compatibility, or AOT claims.

A write builder should be designed alongside the final read builder and the column/accessor seams.
Otherwise batching and source-shape decisions become a second migration. The later design must
settle whether the existing collection methods remain the front door, then express collection shape,
batching, and async source as builder state. It must preserve both emitters and Native AOT coverage.

## Follow-up

Revisit #219 alongside the read builder, column catalog, and accessor work. Keep the existing write
entry points until the replacement has a tested migration path.
