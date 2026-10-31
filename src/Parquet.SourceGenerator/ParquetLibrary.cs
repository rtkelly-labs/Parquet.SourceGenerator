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

    /// <summary>Rewrites emitted code's Parquet.Net names to Lake.Parquet's.</summary>
    public static string Retarget(string source) =>
        ParquetRoot.Replace(source, "global::Lake.Parquet.");
}
