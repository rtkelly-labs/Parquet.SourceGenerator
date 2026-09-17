# Generated API documentation site (#229)

The GitHub Pages API grid is deferred from 0.1. The consumer API is generated into downstream
compilations, so a conventional shipped-assembly documentation tool cannot be the source of truth.
The site must consume the generated API baselines and profile matrix instead.

The post-freeze site should publish versioned generated signatures, backend/profile labels, links to
the relevant guide and compatibility entry, and a visible generated-versus-shipped boundary. It must
build from checked-in contracts so documentation cannot silently drift from the API gate.

This closes the 0.1 scope question for #229; implementation remains a post-freeze follow-up.
