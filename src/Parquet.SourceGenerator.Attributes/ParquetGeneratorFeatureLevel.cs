namespace Parquet.SourceGenerator;

/// <summary>
/// Selects the compatibility and language-feature level used by source generation.
/// </summary>
public enum ParquetGeneratorFeatureLevel
{
    /// <summary>Emit only the flat, compatibility-safe model surface.</summary>
    Level1Flat = 1,

    /// <summary>Enable the currently supported compound preview shapes.</summary>
    Level2CompoundPreview = 2,

    /// <summary>Enable the latest modern generator shapes.</summary>
    Level3ModernCSharp = 3,
}
