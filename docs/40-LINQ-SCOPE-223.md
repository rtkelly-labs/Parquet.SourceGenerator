# LINQ companion package scope (#223)

## Decision

The LINQ companion package remains outside the current release. The generator remains the
compile-time physical layer; an expression-tree provider belongs in a separate package only after
the builder, column catalog, accessor, and row-group selection seams are stable.

The follow-up must define an AOT-safe plan core, residual-predicate behavior, `ExplainAsync()`, strict
fallback behavior, and multi-file/partition pruning. It must demonstrate both emitters through the
backend-neutral accessor rather than reaching into generated implementation details.

## Follow-up

Revisit #223 after the builder, column catalog, accessor, and row-group selection seams are stable.
Keep the provider in a separate package and test both emitters through the backend-neutral accessor.
