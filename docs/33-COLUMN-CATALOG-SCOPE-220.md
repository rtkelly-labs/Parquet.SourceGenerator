# Ordinal-stable column catalog (#220)

## Decision

The catalog is deferred. A useful catalog must model nested leaf paths, logical types,
nullability, encoding hints, and backend-neutral access without exposing Parquet.Net implementation
types. Designing it before the nested-shape and accessor contracts settle would lock in a second
public surface.

The follow-up contract is a compile-time, reflection-free, allocation-free descriptor with stable
source-order ordinals, full nested paths, CLR type metadata, nullability, decimal/timestamp metadata,
and encoding hints. It must be implemented once for both emitters and demonstrated under Native AOT.

## Follow-up

Revisit #220 after the nested-shape and accessor contracts settle. Implement the descriptor once for
both emitters and verify it under Native AOT.
