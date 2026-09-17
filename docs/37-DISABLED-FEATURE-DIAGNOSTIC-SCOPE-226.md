# Diagnostic for disabled feature members (#226)

The disabled-feature diagnostic is deferred from 0.1 with feature profiles. There is no reliable
omission diagnostic until #225 defines named profiles and the generator/analyzer share their exact
configuration state.

When implemented, the diagnostic must name the disabled profile or feature, give the enabling remedy,
read the same MSBuild/attribute configuration as generation, and cover every supported profile in
compiled tests. Shipping it before that shared state exists would risk reporting a false cause.

This closes the 0.1 scope question for #226; implementation remains a post-freeze follow-up.
