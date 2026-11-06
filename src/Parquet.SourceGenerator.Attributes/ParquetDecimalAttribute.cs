using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Configures precision and scale parameters for decimal columns.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true
)]
public sealed class ParquetDecimalAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="ParquetDecimalAttribute"/>.
    /// </summary>
    /// <param name="precision">The total number of digits.</param>
    /// <param name="scale">The number of digits to the right of the decimal point.</param>
    public ParquetDecimalAttribute(int precision, int scale)
    {
        Precision = precision;
        Scale = scale;
    }

    /// <summary>
    /// Gets the precision (total digits).
    /// </summary>
    public int Precision { get; }

    /// <summary>
    /// Gets the scale (fractional digits).
    /// </summary>
    public int Scale { get; }
}
