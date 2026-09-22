# Nested backend scope (#176)

## Decision

The functional scope is declared by backend rather than by an unsupported promise of full parity.
The AOT matrix proves flat models and, through its NodaTime check, nested groups, groups inside
groups, row-level lists of leaves and of groups, and list elements containing one level of groups.
The exact shape boundary is [document 46](./46-SERIALIZATION-SHAPES.md).

| Backend | Supported generated shapes |
| --- | --- |
| Modern Parquet.Net v6 | `StructField` and list-of-leaf/POCO paths covered by the golden corpus and the shape matrix ([46](./46-SERIALIZATION-SHAPES.md)); Native AOT covers the shapes listed above |
| Classic Parquet.Net v4/v5 | Flat generated models only |
| Both backends | Maps and nested compound shapes outside the modern golden corpus are unsupported and must produce the existing diagnostic |

This is the compatibility boundary consumers can rely on. The full both-backend implementation from
the original issue remains later work. It needs legacy definition/repetition-level round trips, map
semantics, AOT coverage, property-based fuzzing, and external interoperability.

## Follow-up

Revisit #176 when those tests and interoperability checks are available. Keep unsupported shapes on
the existing diagnostic path until then.
