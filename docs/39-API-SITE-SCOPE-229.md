# Generated API documentation site (#229)

## Decision

The profile-aware GitHub Pages API grid is deferred. The consumer API is generated into downstream
compilations, so a conventional shipped-assembly documentation tool cannot be the source of truth.
The site must consume the generated API baselines (and, later, the profile matrix) instead.

Those baselines are not checked in. They are derived from the emitter and attached to each GitHub
release as `derived-outputs.tar.gz`, which `docs-dispatch.yml` hands to the docs site by tag (see
[document 17](17-GENERATED-API-BASELINES.md#per-release-output) and
[document 31](31-GENERATED-SHIPPED-BOUNDARY-DECISION.md)). Pinning the grid to a release's output
rather than to whatever `main` holds is what keeps documentation from silently drifting from the
API gate.

The later site should publish versioned generated signatures, backend/profile labels, links to
the relevant guide and compatibility entry, and a visible generated-versus-shipped boundary.

## Follow-up

Revisit #229 after the generated API baselines and profile matrix are stable. Build the grid from
the per-release derived output so it cannot drift from the API gate; do not reintroduce checked-in
copies of the emitted API.
