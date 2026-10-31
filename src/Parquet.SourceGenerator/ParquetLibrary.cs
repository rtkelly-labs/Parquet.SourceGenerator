using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Parquet.SourceGenerator;

/// <summary>
/// The Parquet library the generated code is written against. The emitters write Parquet.Net's names
/// (<c>global::Parquet.…</c>); a consumer that references Lake.Parquet instead (the rtkelly-labs fork of
/// Parquet.Net, the same API in the <c>Lake.Parquet</c> namespace, assembly <c>Lake.Parquet</c>) gets the
/// same code with that root namespace. A compilation that references both keeps Parquet.Net.
/// </summary>
internal static class ParquetLibrary
{
    // global::Parquet. followed by anything but this generator's own namespace
    private static readonly Regex ParquetRoot = new(
        @"global::Parquet\.(?!SourceGenerator\b)",
        RegexOptions.CultureInvariant
    );

    /// <summary>
    /// True when the compilation references Lake.Parquet and not Parquet.Net (assembly <c>Parquet</c>).
    /// </summary>
    public static bool ReferencesLakeParquet(Compilation compilation)
    {
        bool lake = false;
        foreach (AssemblyIdentity identity in compilation.ReferencedAssemblyNames)
        {
            if (string.Equals(identity.Name, "Parquet", System.StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(identity.Name, "Lake.Parquet", System.StringComparison.Ordinal))
            {
                lake = true;
            }
        }

        return lake;
    }

    // a whole emitted line restoring an INT32/INT64-backed decimal column (ParquetDecimalScale)
    private static readonly Regex DecimalRestore = new(
        @"^[ \t]*global::Parquet\.SourceGenerator\.ParquetDecimalScale\.RestoreIntegerScaled\(.*\);[ \t]*\r?\n",
        RegexOptions.CultureInvariant | RegexOptions.Multiline
    );

    /// <summary>
    /// Rewrites emitted code for Lake.Parquet: its names in place of Parquet.Net's, and no decimal restore. Lake.Parquet
    /// decodes INT32/INT64-backed decimals from their bits, exactly on every runtime (its lake(0034)), so the pass that
    /// repairs Parquet.Net's inexact decoding on .NET 11 has nothing to do.
    /// </summary>
    public static string Retarget(string source) =>
        ParquetRoot.Replace(DecimalRestore.Replace(source, string.Empty), "global::Lake.Parquet.");
}
