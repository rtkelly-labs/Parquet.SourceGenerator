using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// The modern backend's half of the shared behavioural suite (test/Shared/Behaviour): the only
/// file in this project that names the generated surface for the shared models.
/// </summary>
internal static partial class BehaviourDriver
{
    public static partial string Backend() => "modern (Parquet.Net 6.x)";

    /// <summary>The modern backend lists no known failures.</summary>
    public static partial bool IsKnownFailure(string id) => false;

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
    ) =>
        TypeMatrixRowParquet
            .From(stream)
            .WithOptions(options ?? ParquetSerializerOptions.Default)
            .ToArrayAsync();

    public static partial Task<TypeMatrixRequiredOnly[]> ReadTypeMatrixRequiredOnlyAsync(
        Stream stream,
        ParquetSerializerOptions? options
    ) =>
        TypeMatrixRequiredOnlyParquet
            .From(stream)
            .WithOptions(options ?? ParquetSerializerOptions.Default)
            .ToArrayAsync();

    public static partial Task<CodecRow[]> ReadCodecRowsAsync(
        Stream stream,
        ParquetSerializerOptions? options
    ) =>
        CodecRowParquet
            .From(stream)
            .WithOptions(options ?? ParquetSerializerOptions.Default)
            .ToArrayAsync();
}
