# Generated/shipped boundary decision (#237)

## Decision

Keep format-shaped orchestration in generated code. The generated extension owns
the `ParquetReader`/`ParquetWriter` calls, backend-specific field handling, and the typed
materialization loop. The shipped Attributes package owns consumer-facing annotations, options,
value types, and the existing low-level extraction and column-transformation helpers
(`NullableColumnExtractor` and `VectorizedColumnTransforms`). Those helpers are retained in the
Attributes package as existing runtime behavior; this decision does not reclassify them as
annotations or propose removing them.

This is a deliberate scope decision, not a claim that a shared boundary could never work. The
modern and classic emitters target materially different Parquet.Net APIs, while the current public
surface is still settling around the read builder, column catalog, and row-group metadata seams.
Moving one path before those seams are stable would create a second boundary that would need to be
redesigned soon after it ships.

## Follow-up gate

Reopen the portability experiment after the builder and backend-neutral catalog/accessor contracts
are stable. The experiment must then port one sequential buffer read, compare TPC-H LineItem and
Adult Census throughput, record the Native AOT binary-size delta, and use a materialization-focused
allocation/time denominator. End-to-end throughput alone is insufficient because Parquet.Net's
encoding and compression dominate that measurement.

Until that evidence exists, generated code remains the lower-risk boundary. Issues #220–#222 remain
design work for a later release rather than dependencies of the current release. The earlier
pushdown proposal is retained as a future design reference and is explicitly superseded for the
current release by this decision.
