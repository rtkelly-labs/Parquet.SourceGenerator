using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Excludes a property or field from being serialized to or deserialized from Parquet streams.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true
)]
public sealed class ParquetIgnoreAttribute : Attribute { }
