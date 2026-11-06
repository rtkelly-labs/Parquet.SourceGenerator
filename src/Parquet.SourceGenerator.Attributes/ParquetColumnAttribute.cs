using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Controls column binding parameters such as field name override and ordering.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true
)]
public sealed class ParquetColumnAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="ParquetColumnAttribute"/> without renaming the column.
    /// </summary>
    /// <remarks>
    /// Exists so ordering can be expressed on its own — <c>[ParquetColumn(Order = 2)]</c>. With only
    /// the name-taking constructor available, reordering a column forced you to restate its name.
    /// </remarks>
    public ParquetColumnAttribute() { }

    /// <summary>
    /// Initializes a new instance of <see cref="ParquetColumnAttribute"/> with the specified column name.
    /// </summary>
    /// <param name="name">The name of the column in the Parquet schema.</param>
    public ParquetColumnAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    /// Gets or sets the name of the column in the Parquet schema. When null, the member name is used.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the explicit column index order.
    /// </summary>
    public int Order { get; set; } = -1;

    /// <summary>
    /// Gets or sets whether string values in this column should be deduplicated during deserialization.
    /// When true, identical string values within a row group share the same object reference,
    /// reducing managed heap allocations and memory footprint for low-cardinality columns.
    /// </summary>
    public bool Deduplicate { get; set; }

    /// <summary>
    /// Gets or sets the physical column encoding hint to use when serializing this column.
    /// </summary>
    public ParquetColumnEncoding Encoding { get; set; } = ParquetColumnEncoding.Default;
}

/// <summary>
/// Specifies the physical column encoding hint to apply when serializing column data.
/// </summary>
public enum ParquetColumnEncoding
{
    /// <summary>
    /// Default encoding, chosen automatically based on data type and cardinality heuristics.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Dictionary encoding (PLAIN_DICTIONARY / RLE_DICTIONARY). Applies to string and byte[] fields.
    /// </summary>
    Dictionary = 1,

    /// <summary>
    /// Delta binary packed encoding (DELTA_BINARY_PACKED). Applies to 32-bit and 64-bit integer fields.
    /// Highly effective for monotonic sequence numbers, IDs, and timestamps.
    /// </summary>
    DeltaBinaryPacked = 2,

    /// <summary>
    /// Byte stream split encoding (BYTE_STREAM_SPLIT). Applies to floating-point (float, double) and integer fields.
    /// Transposes floating point bytes to group exponent and mantissa bytes contiguously.
    /// </summary>
    ByteSplitStream = 3,
}
