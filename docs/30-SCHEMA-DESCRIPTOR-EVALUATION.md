# Schema descriptor evaluation (#291)

Issue #291 asked whether generated `ParquetSchema` constructor trees could be replaced with a
compact descriptor representation without affecting runtime throughput or compatibility.

## Finding

The experiment was implemented in an isolated branch and measured with the repository's Roslyn
generated-code metrics. It emitted a descriptor table and a one-time materializer that rebuilt the
same `Field` tree during static initialization. The generated read and write paths remained
unchanged and the complete golden regression suite continued to compile and pass.

The descriptor table is not an effective optimization when emitted into every generated extension.
The per-type descriptor struct, arrays, and materializer cost more executable code than the
constructor tree they replace.

| Golden model | Direct schema ELOC | Descriptor schema ELOC | Result |
| --- | ---: | ---: | --- |
| `OrderEvent` | 1,020 | 1,030 | +10 |
| `NestedOrder` | 1,213 | 1,223 | +10 |
| `ListOrder` | 1,826 | 1,836 | +10 |
| `PocoOrder` | 2,679 | 2,689 | +10 |
| `ScalarMetric` | 934 | 944 | +10 |

The experiment therefore does not meet the issue's ELOC-reduction acceptance criterion. It is also
not appropriate to claim a throughput improvement from a static-initialization-only rewrite.

## Decision

Close #291 as evaluated and deferred. Keep the direct constructor form as the smaller generated
representation. Revisit descriptor encoding only with a shared runtime schema factory, or an
equivalent support assembly, that can be amortized across generated types.

The follow-up must measure generated ELOC and cold-start/static-initialization cost before it can
become a release requirement. The current form preserves the existing `GoldenCodeGenRegressionTests`,
generated API baselines, metrics baselines, Native AOT surface, and both backend compatibility paths.
