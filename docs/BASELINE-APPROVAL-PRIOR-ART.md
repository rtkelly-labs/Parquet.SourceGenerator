# Prior art for approving generated baselines

These first-party tools and articles separate three concerns: where a reference lives, how a candidate is compared with it, and what makes a change acceptable. The current Parquet.SourceGenerator workflow already provides a derived-output artifact and PR diff; an explicit approval gate remains a proposal. [Current workflow](17-GENERATED-API-BASELINES.md#the-review-diff), [proposed policy](ARTIFACT-BACKED-BASELINES.md).

## Committed references

| Tool | What its documentation describes | Lesson for this repository |
| --- | --- | --- |
| [Playwright snapshots](https://playwright.dev/docs/test-snapshots) | `toHaveScreenshot()` compares a capture with a reference screenshot; `toMatchSnapshot()` covers other data. Updated snapshots are committed and reviewed as a source diff. The rendering environment affects image stability. | A committed file is an effective, locally available oracle, but bulky captures make PRs noisy. |
| [Loki](https://github.com/oblador/loki) | The Storybook-oriented CLI compares screenshots with reference images; `loki approve` updates them. Its README recommends Chrome in Docker to improve reproducibility. | Approval can be a reviewed file update without a hosted service. |
| [Rust insta](https://insta.rs/docs/patterns/) | Pending `.snap.new` files can be uploaded as CI artifacts for review. [The CLI](https://insta.rs/docs/cli/) accepts or rejects them, while accepted `.snap` files remain in Git. | Artifacts are useful candidate handoffs even when they are not the approved store. |

## Hosted and externally stored references

The [Storybook visual-testing article](https://storybook.js.org/blog/visual-testing-is-the-greatest-trick-in-ui-development/) describes the capture → compare → inspect → accept loop. Acceptance changes the next baseline; the article explains the human decision, not an Actions artifact design.

[Chromatic's branching and baseline guide](https://www.chromatic.com/docs/branching-and-baselines/) distinguishes UI Tests against accepted branch history from UI Review against a PR's merge base. Its [mandatory-check documentation](https://www.chromatic.com/docs/mandatory-pr-checks/) describes a PR check that waits for review of changed output. This demonstrates a hosted approval gate, but the two comparison references answer different questions.

[Argos's screenshot-testing guide](https://argos-ci.com/blog/screenshot-testing-guide) describes comparing PR captures with an approved build at the merge base, recording review verdicts, and maintaining a PR status. [Argos Diff](https://argos-ci.com/diff) also handles text artifacts such as Markdown and JSON. This is a vendor account of a hosted baseline service rather than an Actions-only implementation.

The open-source [reg-suit README](https://github.com/reg-viz/reg-suit/blob/master/README.md) describes fetching reference images from external storage, comparing current captures, and publishing a report. Its [GitHub notifier](https://github.com/reg-viz/reg-suit/blob/master/packages/reg-notify-github-plugin/README.md) updates a PR comment and commit status, with a reviewer action to approve expected changes. It is useful prior art for a sticky comment plus gate; its references live in external storage, not Actions artifacts.

## What GitHub provides

[Workflow artifacts](https://docs.github.com/en/actions/concepts/workflows-and-actions/workflow-artifacts) persist outputs beyond a job and make candidates downloadable. The [upload-artifact action](https://github.com/actions/upload-artifact/blob/main/README.md) exposes an artifact digest and immutable artifact ID; an overwrite creates a new artifact. Artifacts expire, and deleting a workflow run deletes its artifacts. These are transport properties, not approval semantics. A historical approval record must not depend solely on a live download link.

GitHub provides [PR comments](https://docs.github.com/en/rest/issues/comments), [labels](https://docs.github.com/en/rest/issues/labels), [workflow events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows), and [required status checks](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches). A label can trigger reevaluation, but it persists after a new PR push and does not identify which output was approved. A proposed gate must bind the reviewer decision to the current head and candidate digest, then recompute its verdict on rerun. GitHub's [secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use) also matters: a privileged comment or approval job must treat PR artifacts as untrusted data.

## Recommendation

Build on the existing `derived` job rather than reintroducing regeneration commits. Its verified `main` artifact, deterministic merge-base fallback, full patch, and sticky PR comment already solve the generation and review-transport problems. Keep the current behavior documented separately from any new gate. If a gate is added, decide whether approval is relative to the merge base or latest approved `main`; bind it to an exact digest and head SHA; record the decision durably; and make the check required only after its failure, expiry, and rerun paths are tested. Preserve the source-controlled package API and seam catalogues, which have different local-build responsibilities. [Derived-output workflow](17-GENERATED-API-BASELINES.md#the-review-diff), [API change contract](18-API-CHANGE-CONTRACT.md).
