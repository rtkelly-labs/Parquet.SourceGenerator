# Diffra schemas (vendored)

The shadow Diffra steps validate what they emit against these files, so that no test or CI step
fetches a schema over the network.

| File | Source |
|---|---|
| `collection-v1.schema.json` | Copied unchanged from [`schemas/collection-v1.schema.json`](https://github.com/rtkelly-labs/Diffra/blob/3e7609229e55a1654af97b355071ea6fead8ce3a/schemas/collection-v1.schema.json) in rtkelly-labs/Diffra at commit `3e76092`. |
| `test-results-payload.schema.json` | Derived by hand from `diffra.project.yml`, following Diffra's [`examples/project-file/test-results/payload.schema.json`](https://github.com/rtkelly-labs/Diffra/blob/3e7609229e55a1654af97b355071ea6fead8ce3a/examples/project-file/test-results/payload.schema.json) at the same commit. Only `scope`, `methodology` and `stratum` differ; `$defs/testResult` is copied unchanged. |
| `benchmark-payload.schema.json` | Derived by hand from `diffra.project.yml`, following Diffra's [`examples/project-file/benchmarks/payload.schema.json`](https://github.com/rtkelly-labs/Diffra/blob/3e7609229e55a1654af97b355071ea6fead8ce3a/examples/project-file/benchmarks/payload.schema.json) at the same commit. `scope`, `methodology` and `stratum` differ; all five fields are required; and `$defs/caseKey` makes the parenthesised parameter list optional, because a benchmark without parameters is `Type.Method` in `benchmarks/baseline.json`. The value and unit definitions are copied unchanged. |

Diffra publishes no schema for a standard type's payload: its example schema fixes the example's
own scope, methodology and strata. Until it does, each project has to derive one by hand, as here.

Diffra is MIT licensed. `docs/internals/diffra-shadow-payloads.md` describes the steps that use
these files.
