using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Specifies compression method for Parquet serialization.
/// </summary>
public enum ParquetCompressionMethod
{
    /// <summary>
    /// No compression (uncompressed raw bytes).
    /// </summary>
    None = 0,

    /// <summary>
    /// Snappy compression (default).
    /// </summary>
    Snappy = 1,

    /// <summary>
    /// Gzip compression.
    /// </summary>
    Gzip = 2,

    /// <summary>
    /// Lz4 compression.
    /// </summary>
    Lz4 = 3,

    /// <summary>
    /// Brotli compression.
    /// </summary>
    Brotli = 4,

    /// <summary>
    /// Zstd compression.
    /// </summary>
    Zstd = 5
}

/// <summary>
/// Specifies how hard the chosen compression method should work.
/// </summary>
/// <remarks>
/// Mirrors <c>System.IO.Compression.CompressionLevel</c>. Declared here rather than reused so this
/// assembly's netstandard2.0 and netstandard2.1 targets do not have to reason about
/// <c>SmallestSize</c>, which only exists from .NET 6 onwards.
/// </remarks>
public enum ParquetCompressionLevel
{
    /// <summary>
    /// Balances compression ratio against speed.
    /// </summary>
    Optimal = 0,

    /// <summary>
    /// Favours speed over compression ratio.
    /// </summary>
    Fastest = 1,

    /// <summary>
    /// Performs no compression, whatever the compression method.
    /// </summary>
    NoCompression = 2,

    /// <summary>
    /// Favours compression ratio over speed.
    /// </summary>
    SmallestSize = 3
}

/// <summary>
/// Configurable options for Parquet source generator serialization and deserialization operations.
/// </summary>
public sealed class ParquetSerializerOptions
{
    /// <summary>
    /// Gets a new configuration instance carrying the default values.
    /// </summary>
    /// <remarks>
    /// Deliberately a fresh instance per access rather than a shared singleton. The properties
    /// below are settable, so a shared instance let any caller rewrite the defaults for every
    /// serializer in the process — <c>ParquetSerializerOptions.Default.RowGroupSize = 1;</c> in one
    /// library silently reconfigured every other one. The allocation is negligible beside the
    /// megabytes a Parquet read or write moves, and it is only taken when a caller omits options
    /// entirely.
    /// <para>
    /// The properties are not <c>init</c>-only because this assembly targets netstandard2.0 and
    /// netstandard2.1, neither of which carries <c>IsExternalInit</c>; making them init-only would
    /// break object-initializer use for exactly the .NET Framework consumers this package is meant
    /// to keep serving.
    /// </para>
    /// </remarks>
    public static ParquetSerializerOptions Default => new();

    /// <summary>
    /// Gets or sets the target row group size for batched writing operations (default is 50,000 rows).
    /// </summary>
    public int RowGroupSize { get; set; } = 50_000;

    /// <summary>
    /// Gets or sets the maximum degree of parallelism for parallel row group reading (default is -1,
    /// using <c>Environment.ProcessorCount</c>).
    /// </summary>
    /// <remarks>
    /// Applies to <c>ReadParquetParallelAsync</c> over a <c>ReadOnlyMemory&lt;byte&gt;</c>, where
    /// each worker gets its own reader over its own view of the buffer. The <c>Stream</c> overload
    /// reads sequentially and ignores this: a stream cannot be shared between readers. An explicit
    /// <c>maxDegreeOfParallelism</c> argument takes precedence over this value, and the effective
    /// worker count is capped at the file's row-group count.
    /// </remarks>
    public int MaxDegreeOfParallelism { get; set; } = -1;

    /// <summary>
    /// Gets or sets the compression method to apply when creating Parquet files (default is Snappy).
    /// </summary>
    public ParquetCompressionMethod CompressionMethod { get; set; } = ParquetCompressionMethod.Snappy;

    /// <summary>
    /// Gets or sets how hard the compression method should work. Null leaves Parquet.Net's own
    /// default in place, which is <c>SmallestSize</c>.
    /// </summary>
    /// <remarks>
    /// Nullable rather than defaulted so that "not specified" stays distinguishable from any legal
    /// value — the same mistake the row group size sentinel used to make.
    /// </remarks>
    public ParquetCompressionLevel? CompressionLevel { get; set; }

    // UseMicrosecondTimestamps has been removed. It could never have worked: the schema is emitted
    // at compile time into a `static readonly ParquetSchema Schema`, so no runtime flag can change
    // a column's encoding. Per-property [ParquetTimestamp(ParquetTimestampUnit.Microseconds)] is
    // the mechanism that actually does it, because it is visible to the generator.
}
