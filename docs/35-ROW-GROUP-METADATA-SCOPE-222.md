# Row-group metadata and selection (#222)

## Decision

Public row-group enumeration and explicit subset reads are deferred. Existing generated
pruning remains internal and continues to provide predicate pushdown and sorted-key behavior. Exposing
metadata before the catalog, accessor, and builder contracts settle would duplicate their addressing
and predicate semantics.

The follow-up must provide allocation-free metadata enumeration, explicit ranges/subsets, uniform
predicate availability, and tests proving skipped row groups perform no I/O. It must refactor the
existing pruning components onto the shared mechanism and cover both emitters.

## Follow-up

Revisit #222 after the catalog, accessor, and builder contracts settle. Build the shared mechanism
around the existing pruning components and cover both emitters.
