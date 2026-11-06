# Backend-neutral accessor (#221)

## Decision

The generic accessor is deferred. Its shape depends on the column catalog (#220), the final
builder (#217), and row-group metadata selection (#222). Shipping an interface now would either expose
unstable generated methods or require a breaking reshaping at the first API freeze.

The follow-up must provide a single backend-neutral interface in the Attributes package, a generated
zero-allocation singleton implementation for both emitters, and a generic consumer test covering
modern and classic Parquet.Net. The interface must remain reflection-free and Native AOT safe.

## Follow-up

Revisit #221 after #220, #217, and #222 settle their contracts. Keep the interface reflection-free
and Native AOT safe, then test the generated implementation against both Parquet.Net backends.
