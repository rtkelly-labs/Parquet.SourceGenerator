# Feature profiles and per-type overrides (#225)

## Decision

Named feature profiles are deferred. Issue #290 now provides the configuration channel and
feature-level policy, but the profile matrix still depends on the final generated builder and the
stable emitted API freeze.

The later design will support named profiles rather than arbitrary independent bits, document
the MSBuild-versus-attribute precedence, and test each profile on both emitters. `NetStandardCompat`
must be demonstrated by a real downstream net472/netstandard2.0 consumer before it is advertised.

## Follow-up

Revisit #225 after the generated builder and emitted API are stable. Use named profiles, document
MSBuild-versus-attribute precedence, and test every profile on both emitters.
