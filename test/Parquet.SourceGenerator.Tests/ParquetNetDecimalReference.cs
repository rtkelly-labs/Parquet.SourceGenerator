using System;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Parquet.Net's own readers (<c>ParquetSerializer</c>, <c>DataColumn</c>) decode INT32- and
/// INT64-backed decimals through a <c>double</c>-derived factor, which from .NET 11 leaves the
/// <c>double</c>'s error in every value (<see cref="ParquetDecimalScale"/>). A test that uses them
/// as the reference corrects the reference side the same way the generated readers do, so it still
/// compares against what Parquet.Net returned before .NET 11. On earlier runtimes this is a no-op.
/// </summary>
internal static class ParquetNetDecimalReference
{
    public static decimal Exact(decimal value, int scale)
    {
        Span<decimal> one = [value];
        ParquetDecimalScale.RestoreIntegerScaled(one, scale);
        return one[0];
    }

    public static decimal? Exact(decimal? value, int scale) =>
        value is decimal present ? Exact(present, scale) : null;
}
