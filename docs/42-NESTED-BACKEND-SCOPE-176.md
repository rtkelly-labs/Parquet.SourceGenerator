# Nested backend scope (#176)

## Decision

The functional scope is declared by backend rather than by an unsupported promise of full parity.
The AOT matrix currently proves flat models only, so this document does not claim AOT coverage for
compound shapes.

| Backend | Supported generated shapes |
| --- | --- |
| Modern Parquet.Net v6 | `StructField` and list-of-leaf/POCO paths covered by the golden corpus; Native AOT coverage remains flat-only |
| Classic Parquet.Net v4/v5 | Flat generated models only |
| Both backends | Maps and nested compound shapes outside the modern golden corpus are unsupported and must produce the existing diagnostic |

This is the compatibility boundary consumers can rely on. The full both-backend implementation from
the original issue remains later work. It needs legacy definition/repetition-level round trips, map
semantics, AOT coverage, property-based fuzzing, and external interoperability.

## Follow-up

Revisit #176 when those tests and interoperability checks are available. Keep unsupported shapes on
the existing diagnostic path until then.
