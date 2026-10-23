namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// Identifiers for behaviour a backend is known to get wrong. Each backend's driver lists the ones
/// that apply to it (<see cref="BehaviourDriver.IsKnownFailure"/>), with the reason, and the docs
/// list them under "Known failures" in docs/guides/legacy-support.md. Nothing here is skipped.
/// </summary>
public static class KnownFailures
{
    /// <summary>A zero-length <c>byte[]</c> value round-trips as a zero-length value.</summary>
    public const string EmptyBinaryValue = "empty-binary-value";

    /// <summary>Writing with <paramref name="codec"/> works on the running platform.</summary>
    public static string CodecUnavailable(ParquetCompressionMethod codec) =>
        "codec-unavailable-" + codec;
}
