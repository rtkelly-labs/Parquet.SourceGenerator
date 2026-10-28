// IsExternalInit reached the BCL in .NET 5. Both netstandard2.0 AND netstandard2.1 lack it, so the
// guard has to name them both — a netstandard2.0-only guard left the netstandard2.1 build with no
// polyfill at all, and the first `init` accessor or positional record added to this assembly would
// have broken that target alone.
#if NETSTANDARD2_0 || NETSTANDARD2_1
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
#endif
