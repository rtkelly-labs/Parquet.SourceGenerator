# Artifact-backed baseline approval

Status: proposal. The workflows in this repository still commit regenerated golden files to a PR. This document describes a possible replacement for generated outputs that exist to support review, not a change that has shipped.

## Why separate the output from its approval

A generated screenshot or reference document has two jobs. The candidate shows what the PR produces. The baseline records what reviewers already accepted. They may have the same file format, but they have different authority. A PR run can create a candidate; it must not approve its own output.

The current [`/update-golden` workflow](../.github/workflows/update-golden-files.yml) writes refreshed `GoldenFiles/*.g.cs`, `*.api.txt`, and metrics files back to the PR branch. This makes the change visible in a Git diff, but it also requires a regeneration commit. An artifact-backed approach would publish the candidate and its comparison report from CI, then make the review decision visible through a required check and one updated PR comment. The generated files would not need a PR commit merely to be inspected.

## Proposed comparison and approval model

1. A successful, trusted `main` run publishes an approved baseline bundle. Its manifest identifies the `main` commit, toolchain and capture configuration, file names, and content digests.
2. A PR run produces a candidate bundle and manifest for its exact head commit. It compares those files with the approved bundle for a pinned `main` commit. Added and removed files count as changes.
3. CI uploads the candidate and a readable difference report. One bot-owned PR comment is updated with the baseline and candidate identities, changed-file summary, report link, and approval state.
4. A required check passes when the outputs match. When they differ, it blocks merge until an authorized reviewer approves the exact candidate. Record the approver, PR head commit, candidate digest, baseline commit, and reason.
5. A new PR commit or a change to the chosen `main` baseline triggers a fresh comparison. An old label cannot approve newly generated output. After merge, a trusted `main` run publishes the next approved bundle.

The comparison should use a pinned `main` commit, even if branch protection requires the PR to be current with `main`. Pinning prevents `main` from changing halfway through a run. If `main` moves before merge, rerun the comparison against the new commit. A missing file on either side is a change to review, not a reason to skip the check.

A label can request an approval evaluation, but the label alone is not the decision. It persists across PR pushes. The gate must bind a reviewer decision to the current head commit and candidate digest, or remove the label and require another decision after every push. A protected approval record is preferable to treating the presence of a label as sufficient evidence.

## What belongs in an artifact

Large visual captures, rendered docs, and generated reference material are good first candidates. They need human review and comparison, but their binary or verbose output does not necessarily need to be present in every developer's checkout. A report should expose both the raw candidate and a useful diff. For images, that might be side-by-side or overlay views. For text, it should be a text diff rather than a download-only archive.

The repository's existing `.api.txt` baselines have another job. `PARQAPI001` uses them during a local build, and the [API change contract](18-API-CHANGE-CONTRACT.md) requires catalogue and ledger entries. Moving these files only into CI artifacts would remove the local reference that the build reads. Keep them in Git unless a separate offline and local-build design replaces that contract. The same question applies to any `.g.cs` or metrics baseline consumed directly by local tests. Artifact transport and baseline policy are separate decisions.

## Failure and trust boundaries

GitHub Actions artifacts expire. A missing approved `main` bundle must fail with a clear "baseline unavailable" result. It must not fall back to the PR candidate. Recovery could regenerate the pinned `main` commit under a pinned toolchain, or move approved bundles to a durable, versioned store. Neither path should silently change the comparison reference.

PR artifacts are untrusted data. A privileged job that comments, labels, or records approval must not execute code from a PR artifact or check out and run untrusted PR code. The report should display artifact contents safely, especially HTML. Retain the manifest and approval record long enough to explain why a merged change passed its gate; ordinary Actions artifact retention may be too short for that audit trail.

This is an approval mechanism, not a replacement for deterministic generation. The same inputs and pinned environment should reproduce a candidate. If they do not, fix that before using a baseline diff as a merge gate.

## Related reading

- [Prior art for approving generated baselines](BASELINE-APPROVAL-PRIOR-ART.md) compares Playwright, insta, Chromatic, Argos, and reg-suit.
- [Generated public API baselines](17-GENERATED-API-BASELINES.md) describes the current local `.api.txt` contract.
- [GitHub workflow artifacts](https://docs.github.com/en/actions/concepts/workflows-and-actions/workflow-artifacts) explains artifact storage and retention.
- [reg-suit's GitHub notifier](https://github.com/reg-viz/reg-suit/blob/master/packages/reg-notify-github-plugin/README.md) shows an open-source PR comment and approval-status pattern.
