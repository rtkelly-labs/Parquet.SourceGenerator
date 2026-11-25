using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// The per-backend seam of the shared behavioural suite. Every call the shared tests make into
/// generated code goes through here; each test project implements these members once against its
/// own backend's generated surface (<c>ModernBehaviourDriver.cs</c>,
/// <c>LegacyBehaviourDriver.cs</c>). A test project that compiles this folder without an
/// implementation does not build, so no backend can silently opt out.
/// </summary>
internal static partial class BehaviourDriver
{
    /// <summary>Names the backend under test, for failure messages.</summary>
    public static partial string Backend();

    /// <summary>
    /// Whether this backend is listed as failing the behaviour <paramref name="id"/> (one of
    /// <see cref="KnownFailures"/>). A listed failure is not skipped: its test asserts that it still
    /// fails, so a fix turns it red until the entry is removed from the backend's list.
    /// </summary>
    public static partial bool IsKnownFailure(string id);

    public static partial Task WriteAsync(
        IReadOnlyList<TypeMatrixRow> rows,
        Stream stream,
        ParquetSerializerOptions? options
    );

    public static partial Task WriteAsync(
        IReadOnlyList<TypeMatrixRequiredOnly> rows,
        Stream stream,
        ParquetSerializerOptions? options
    );

    public static partial Task WriteAsync(
        IReadOnlyList<CodecRow> rows,
        Stream stream,
        ParquetSerializerOptions? options
    );

    public static partial Task<TypeMatrixRow[]> ReadTypeMatrixAsync(
        Stream stream,
        ParquetSerializerOptions? options
    );

    public static partial Task<TypeMatrixRequiredOnly[]> ReadTypeMatrixRequiredOnlyAsync(
        Stream stream,
        ParquetSerializerOptions? options
    );

    public static partial Task<CodecRow[]> ReadCodecRowsAsync(
        Stream stream,
        ParquetSerializerOptions? options
    );
}
