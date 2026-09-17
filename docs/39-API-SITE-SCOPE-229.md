# Generated API documentation site (#229)

## Decision

The GitHub Pages API grid is deferred. The consumer API is generated into downstream
compilations, so a conventional shipped-assembly documentation tool cannot be the source of truth.
The site must consume the generated API baselines and profile matrix instead.

The later site should publish versioned generated signatures, backend/profile labels, links to
the relevant guide and compatibility entry, and a visible generated-versus-shipped boundary. It must
build from checked-in contracts so documentation cannot silently drift from the API gate.

## Follow-up

Revisit #229 after the generated API baselines and profile matrix are stable. Build the site from
checked-in contracts so it cannot drift from the API gate.
