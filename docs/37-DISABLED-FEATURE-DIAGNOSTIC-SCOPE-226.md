# Diagnostic for disabled feature members (#226)

## Decision

The disabled-feature diagnostic is deferred with feature profiles. There is no reliable
omission diagnostic until #225 defines named profiles and the generator and the diagnostic read the
same configuration state. That state comes from the configuration channel and feature-level policy
of #290 (shared generator configuration plumbing is #224); #225 owns the profile design on top of it.

When implemented, the diagnostic must name the disabled profile or feature, give the enabling remedy,
read the same MSBuild/attribute configuration as generation, and cover every supported profile in
compiled tests. Shipping it before that shared state exists would risk reporting a false cause.

## Follow-up

Revisit #226 after #225 defines the profile set on top of the #290 configuration channel. Compile tests must
cover every supported profile.
