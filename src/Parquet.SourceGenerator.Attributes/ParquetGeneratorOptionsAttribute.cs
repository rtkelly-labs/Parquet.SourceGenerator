using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Sets project-wide source-generator feature policy when placed on the consumer assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ParquetGeneratorOptionsAttribute : Attribute
{
    /// <summary>Gets or sets the source-generator feature level for the assembly.</summary>
    public ParquetGeneratorFeatureLevel FeatureLevel { get; set; } =
        ParquetGeneratorFeatureLevel.Level2CompoundPreview;
}
