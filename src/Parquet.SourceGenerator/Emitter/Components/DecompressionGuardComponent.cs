using System.Text;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Emits the private factory method used to create a guarded read stream before Parquet.Net
/// allocates a decompression buffer for an untrusted page or reads footer counts.
/// The guard implementation is compiled in Parquet.SourceGenerator.Runtime.
/// </summary>
internal static class DecompressionGuardComponent
{
    public static void Emit(StringBuilder builder)
    {
        builder.AppendLine(
            "    private static global::Parquet.SourceGenerator.ParquetDecompressionGuardStream CreateGuardedReadStream(global::System.IO.Stream stream, global::Parquet.SourceGenerator.ParquetSerializerOptions options) => new(stream, options.MaxDecompressedPageSize, options.MaxDecompressionExpansionRatio, options.MaxRowGroupCount);"
        );
    }
}
