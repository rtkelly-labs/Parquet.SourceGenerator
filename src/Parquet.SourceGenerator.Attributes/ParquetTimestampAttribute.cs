using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Specifies timestamp unit resolution for DateTime fields.
/// </summary>
public enum ParquetTimestampUnit
{
    /// <summary>
    /// Milliseconds since Unix epoch.
    /// </summary>
    Milliseconds,

    /// <summary>
    /// Microseconds since Unix epoch.
    /// </summary>
    Microseconds,

    // Nanoseconds is deliberately absent. Parquet.Net's DateTimeFormat tops out at
    // DateAndTimeMicros, so there is no way to emit a nanosecond-precision column through it. The
    // member used to exist and silently fell back to the default encoding, which is worse than not
    // offering it: callers got a coarser column than they asked for with nothing to indicate it.
}

/// <summary>
/// Configures timestamp resolution for DateTime properties.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true
)]
public sealed class ParquetTimestampAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="ParquetTimestampAttribute"/>.
    /// </summary>
    /// <param name="unit">The timestamp unit resolution.</param>
    public ParquetTimestampAttribute(ParquetTimestampUnit unit)
    {
        Unit = unit;
    }

    /// <summary>
    /// Gets the timestamp resolution unit.
    /// </summary>
    public ParquetTimestampUnit Unit { get; }
}
