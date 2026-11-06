using System;
using System.Collections.Generic;

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
    Zstd = 5,
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
    SmallestSize = 3,
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
    /// <remarks>
    /// This is the only place the row group size can be set. <c>WriteParquetBatchedAsync</c> and the
    /// <c>IAsyncEnumerable</c> overload of <c>WriteParquetAsync</c> carried a duplicate
    /// <c>rowGroupSize</c> parameter until <c>0.0.x</c>; it was removed so the setting has one home.
    /// </remarks>
    public int RowGroupSize { get; set; } = 50_000;

    /// <summary>
    /// Gets or sets the maximum degree of parallelism for parallel row group reading (default is -1,
    /// using <c>Environment.ProcessorCount</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies to <c>ReadParquetParallelAsync</c> over a <c>ReadOnlyMemory&lt;byte&gt;</c>, where
    /// each worker gets its own reader over its own view of the buffer. The <c>Stream</c> overload
    /// reads sequentially and ignores this: a stream cannot be shared between readers. The effective
    /// worker count is capped at the file's row-group count.
    /// </para>
    /// <para>
    /// This is the only place the degree of parallelism can be set. The parallel read methods carried
    /// a duplicate <c>maxDegreeOfParallelism</c> parameter until <c>0.0.x</c>; it was removed so the
    /// setting has one home.
    /// </para>
    /// </remarks>
    public int MaxDegreeOfParallelism { get; set; } = -1;

    /// <summary>
    /// Gets or sets the compression method to apply when creating Parquet files (default is Snappy).
    /// </summary>
    public ParquetCompressionMethod CompressionMethod { get; set; } =
        ParquetCompressionMethod.Snappy;

    /// <summary>
    /// Gets or sets how hard the compression method should work. Null leaves Parquet.Net's own
    /// default in place, which is <c>SmallestSize</c>.
    /// </summary>
    /// <remarks>
    /// Nullable rather than defaulted so that "not specified" stays distinguishable from any legal
    /// value — the same mistake the row group size sentinel used to make.
    /// </remarks>
    public ParquetCompressionLevel? CompressionLevel { get; set; }

    /// <summary>
    /// Gets or sets whether to deduplicate string instances during deserialization (default is false).
    /// When enabled, identical string values within a row group share the same object reference,
    /// dramatically reducing managed heap allocations and memory footprint for low-cardinality columns.
    /// </summary>
    public bool DeduplicateStrings { get; set; }

    /// <summary>
    /// Gets or sets the dictionary uniqueness threshold, which is a value from 0 (no unique values) to 1 (all values are unique)
    /// indicating when dictionary encoding is applied. Uniqueness factor needs to be less than or equal to this threshold.
    /// Null leaves Parquet.Net's default in place (0.8).
    /// </summary>
    public double? DictionaryEncodingThreshold { get; set; }

    /// <summary>
    /// Gets or sets the number of values to sample before attempting full dictionary encoding.
    /// When positive, a quick uniqueness check is performed on the first sample to determine if dictionary encoding should be applied,
    /// avoiding expensive full-column scans on high-cardinality data. Default is null (leaving Parquet.Net's default of 0 / full scan).
    /// </summary>
    public int? DictionaryEncodingSampleSize { get; set; }

    /// <summary>
    /// Specifies runtime encoding hints to the writer for specific columns by column path or name.
    /// Takes precedence over or supplements compile-time attribute annotations.
    /// </summary>
    public IDictionary<string, ParquetColumnEncoding> ColumnEncodingHints { get; } =
        new Dictionary<string, ParquetColumnEncoding>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the maximum number of values permitted in a single column chunk allocation or total collection size (default is 10,000,000).
    /// </summary>
    public int MaxAllocationValues { get; set; } = 10_000_000;

    /// <summary>
    /// Gets or sets the maximum number of row groups permitted in a single Parquet file (default is 100,000).
    /// </summary>
    public int MaxRowGroupCount { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the maximum compound schema nesting depth permitted (default is 64).
    /// </summary>
    public int MaxNestingDepth { get; set; } = 64;

    // UseMicrosecondTimestamps has been removed. It could never have worked: the schema is emitted
    // at compile time into a `static readonly ParquetSchema Schema`, so no runtime flag can change
    // a column's encoding. Per-property [ParquetTimestamp(ParquetTimestampUnit.Microseconds)] is
    // the mechanism that actually does it, because it is visible to the generator.
}
