using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// The legacy backend's half of the shared suite: the only file in this project that names the
/// legacy generated surface for the shared models. #494 renames <c>*ParquetLegacyExtensions</c>
/// and replaces the flat reads with the modern builder; that change lands here and nowhere else.
/// </summary>
internal static partial class BehaviourDriver
{
    public static partial string Backend() => "legacy (Parquet.Net 4.25)";

    /// <summary>
    /// The legacy backend's known failures. Each is asserted to still fail, never skipped.
    /// </summary>
    /// <remarks>
    /// <see cref="KnownFailures.EmptyBinaryValue"/>: Parquet.Net 4.25 writes a zero-length
    /// <c>byte[]</c> value, but its plain decoder throws <c>IndexOutOfRangeException</c> reading the
    /// column back, required or optional (reproduced against Parquet.Net 4.25 directly, without
    /// generated code). Upstream; recorded in UPSTREAM_DEPENDENCY_LIMITATIONS.md.
    /// <para>
    /// <see cref="KnownFailures.CodecUnavailable"/> for LZ4, Brotli and Zstd on .NET Framework:
    /// Parquet.Net 4.25 compresses them through IronCompress's native library, which a .NET
    /// Framework test host (like an AnyCPU .NET Framework app) does not load: IronCompress throws
    /// <c>NotSupportedException</c> ("native: False"). Gzip, Snappy and None are managed and work.
    /// </para>
    /// </remarks>
    public static partial bool IsKnownFailure(string id) =>
        id == KnownFailures.EmptyBinaryValue || IsUnavailableCodec(id);

    private static bool IsUnavailableCodec(string id)
    {
#if NETFRAMEWORK
        return id == KnownFailures.CodecUnavailable(ParquetCompressionMethod.Lz4)
            || id == KnownFailures.CodecUnavailable(ParquetCompressionMethod.Brotli)
            || id == KnownFailures.CodecUnavailable(ParquetCompressionMethod.Zstd);
#else
        _ = id;
        return false;
#endif
    }

    public static partial Task WriteAsync(
        IReadOnlyList<TypeMatrixRow> rows,
        Stream stream,
        ParquetSerializerOptions? options
    ) => rows.WriteParquetAsync(stream, options);

    public static partial Task WriteAsync(
        IReadOnlyList<TypeMatrixRequiredOnly> rows,
        Stream stream,
        ParquetSerializerOptions? options
    ) => rows.WriteParquetAsync(stream, options);

    public static partial Task WriteAsync(
        IReadOnlyList<CodecRow> rows,
        Stream stream,
        ParquetSerializerOptions? options
    ) => rows.WriteParquetAsync(stream, options);

    public static partial Task<TypeMatrixRow[]> ReadTypeMatrixAsync(
        Stream stream,
        ParquetSerializerOptions? options
    ) => TypeMatrixRowParquetLegacyExtensions.ReadParquetArrayAsync(stream, options);

    public static partial Task<TypeMatrixRequiredOnly[]> ReadTypeMatrixRequiredOnlyAsync(
        Stream stream,
        ParquetSerializerOptions? options
    ) => TypeMatrixRequiredOnlyParquetLegacyExtensions.ReadParquetArrayAsync(stream, options);

    public static partial Task<CodecRow[]> ReadCodecRowsAsync(
        Stream stream,
        ParquetSerializerOptions? options
    ) => CodecRowParquetLegacyExtensions.ReadParquetArrayAsync(stream, options);
}
