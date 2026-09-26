# Artifact-backed baseline review and approval

This page separates the derived-output review workflow that exists today from a possible approval gate. A PR artifact is a *candidate*, not an approved reference. The comparison uses output from the merge base, fetched from a `main` push artifact or regenerated from that commit.

## What exists today

The [`derived` CI job](../.github/workflows/ci.yml) renders golden code, emitted API signatures, metrics, and call graphs from source. On a `main` push, it uploads `derived-baseline-<sha>` with 90-day retention after its own generation checks pass. The parallel `test` job and overall workflow can still fail. On a PR, `derived` compares the head output with the merge base: it downloads an artifact only from this repository's `main` push run for that exact commit, or regenerates the merge base in a worktree when the artifact is absent. The resolver checks the run's provenance, not its conclusion. Older merge bases can use the golden files that were checked in at the time. The job uploads both trees and the full patch as `derived-outputs`, appends the report to its step summary, and maintains one PR comment marked `<!-- derived-review-diff -->`. [Generated public API baselines](17-GENERATED-API-BASELINES.md#the-review-diff) documents the implementation and its failure behavior.

The generated `.g.cs`, `.api.txt`, `.api.shape.txt`, and metrics files are no longer committed as golden baselines. `PARQAPI001` is retired. The emitted API is reviewed in the PR diff rather than gated by a local catalogue; shipped package API and internal seams retain their separate build gates. [The API change contract](18-API-CHANGE-CONTRACT.md#the-emitted-surface-reviewed-not-catalogued) explains that distinction.

The current checks enforce successful generation and the remaining tests and gates. **They do not require a separate human approval when derived output changes.** A sticky comment makes a difference visible; it is not an approval record. The present merge-base fallback also means that an expired artifact does not fail the job.

## Proposed approval policy (not implemented)

For outputs that warrant an explicit review decision, add a required check with these properties:

1. Pin the comparison reference to an exact `main` commit whose required checks passed. Identify whether its output came from a verified artifact or a deterministic regeneration. Record the generator version, capture environment, file list, and per-file digests in a manifest. This full-workflow check is an additional requirement, not a property of the current resolver.
2. Publish the PR candidate, full diff, and their identities. Treat added and removed files as differences. Keep one comment current with the candidate digest, reference commit, changed-file summary, and links to the report.
3. Pass automatically when the outputs match. For changed output, require an authorized reviewer to approve **that candidate digest and PR head commit**. Record the reviewer, decision, reference commit, and reason in durable state. A persistent label may request evaluation, but cannot itself approve later bytes.
4. On a new push, a changed candidate, or a changed comparison reference, recompute the verdict. An approval for an earlier candidate must not carry over. After merge, a successful trusted `main` run publishes the next reference.

The choice between a merge-base reference and the latest approved `main` output is policy, not an implementation detail. The existing diff uses the merge base so a PR shows what it changed. A gate against the latest approved `main` would answer a different question and would need to handle unrelated changes on `main`. Decide that policy before implementing the check.

Actions artifacts are temporary and can disappear when retention expires or a run is deleted. The current CI deliberately regenerates the merge base when its artifact is absent. A future approval gate should either accept that reproducible fallback and bind the approval to the resulting digest, or fail with a named missing-reference error and provide a recovery procedure. It must never silently use the PR candidate as its own reference. For long-term audit, store the approval decision and manifest somewhere more durable than the artifact link.

PR artifacts are untrusted data. A privileged job that posts comments, reads labels, or records approval must not execute code from the candidate artifact or run untrusted PR code with write credentials. The same inputs and pinned environment should reproduce a candidate; unexplained nondeterminism is a problem to fix before making a diff an approval gate.

## Scope

The first candidates for explicit approval would be outputs that are primarily read by reviewers, such as rendered documents, screenshots, and the existing derived-output report. Small source-controlled contracts still needed by local builds, such as `PublicAPI.Shipped.txt` and `src/api/seams.txt`, should remain in Git unless their local enforcement model is deliberately redesigned. This is separate from deciding where CI stores bulky generated output.

See [prior art for generated-baseline approval](BASELINE-APPROVAL-PRIOR-ART.md) for tools that use committed references, hosted baselines, or CI artifacts, and for the limits of each pattern.
