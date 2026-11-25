using System;
using System.ComponentModel;

namespace Parquet.SourceGenerator;

/// <summary>
/// Restores INT32- and INT64-backed Parquet decimals to their exact value after Parquet.Net has
/// decoded them.
/// </summary>
/// <remarks>
/// <para>
/// Parquet.Net (6.x and 4.25) decodes an integer-backed decimal as
/// <c>unscaled * (decimal)Math.Pow(10, -scale)</c>. Up to .NET 10 the <c>double</c> to
/// <c>decimal</c> conversion rounded to 15 significant digits, which happened to turn the
/// <c>double</c> nearest 10^-scale back into the exact power of ten. From .NET 11 it keeps the
/// <c>double</c>'s value (<c>0.0100000000000000002081668171</c> for scale 2), so every decoded value
/// carries the <c>double</c>'s error: an unscaled 1999 at scale 2 reads as
/// <c>19.990000000000000416125467383</c>.
/// </para>
/// <para>
/// The decoded value is the unscaled integer times a factor this process can recompute, so
/// dividing by that factor and rounding recovers the integer exactly (it has at most 19 digits and
/// the division is good to about 28), and multiplying by the exact power of ten rebuilds the value
/// Parquet.Net produced on earlier runtimes, scale included. Where the factor is already the exact
/// power of ten, at the right scale, the call returns after one lookup.
/// </para>
/// <para>
/// The result is at the column's scale, the representation earlier runtimes produced (19.99, not
/// 19.9900000000000000000000000000). Whether a value is noisy cannot be told from the value: at
/// scale 10 the double's error on an 18-digit unscaled value is 36 units of the column's own scale,
/// so the noisy value looks exact. The correction is therefore applied unconditionally, exactly
/// once, and only to INT32- and INT64-backed chunks. Fixed-length and byte-array decimals decode
/// exactly through <c>BigInteger</c>; inverting the factor on one whose unscaled value has more
/// than about 16 digits would corrupt it. The emitted reader checks the chunk's physical type.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ParquetDecimalScale
{
    // 2^63: no INT64-backed decimal has a larger unscaled magnitude. A larger value in the span is
    // a stale pooled slot past the written range and is left alone rather than overflowing.
    private const decimal MaxUnscaledMagnitude = 9_223_372_036_854_775_808m;

    private const int MaxScale = 28;

    // Computed once per process with the same expression Parquet.Net evaluates, so the factor
    // matches the one its decode used. A factor equal to the power of ten but held at more places
    // (.NET 11 at high scales) still needs the pass: the values come out equal but print with
    // trailing zeros. decimal.Scale does not exist on netstandard, and GetBits allocates, so both
    // are worked out here rather than per read.
    private static readonly decimal[] Factors = new decimal[MaxScale + 1];
    private static readonly bool[] NeedsCorrection = new bool[MaxScale + 1];

#pragma warning disable CA1810 // The tables are filled by a loop; an initializer cannot express it.
    static ParquetDecimalScale()
#pragma warning restore CA1810
    {
        for (int scale = 1; scale <= MaxScale; scale++)
        {
            decimal factor = (decimal)Math.Pow(10, -scale);
            Factors[scale] = factor;
            NeedsCorrection[scale] =
                factor != 0m && (factor != Exact(scale) || ScaleOf(factor) != scale);
        }
    }

    /// <summary>Restores integer-backed decimals decoded at <paramref name="scale"/>.</summary>
    /// <param name="values">The decoded values, corrected in place.</param>
    /// <param name="scale">The column's scale as declared in the file, not the model.</param>
    public static void RestoreIntegerScaled(Span<decimal> values, int scale)
    {
        if (TryGetCorrection(scale, out decimal factor))
        {
            Restore(values, scale, factor);
        }
    }

    /// <summary>Restores nullable integer-backed decimals decoded at <paramref name="scale"/>.</summary>
    /// <param name="values">The decoded values, corrected in place; nulls are left as they are.</param>
    /// <param name="scale">The column's scale as declared in the file, not the model.</param>
    public static void RestoreIntegerScaled(Span<decimal?> values, int scale)
    {
        if (!TryGetCorrection(scale, out decimal factor))
        {
            return;
        }

        decimal exact = Exact(scale);
        decimal limit = MaxUnscaledMagnitude * factor;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is decimal value)
            {
                values[i] = Restore(value, factor, exact, limit);
            }
        }
    }

    /// <summary>
    /// The factor Parquet.Net multiplied by, or <see langword="false"/> when it is the exact power
    /// of ten and there is nothing to correct.
    /// </summary>
    internal static bool TryGetCorrection(int scale, out decimal factor)
    {
        if (scale <= 0 || scale > MaxScale)
        {
            factor = 0m;
            return false;
        }

        factor = Factors[scale];
        return NeedsCorrection[scale];
    }

    /// <summary>The correction itself, with the factor supplied so it can be tested on any runtime.</summary>
    internal static void Restore(Span<decimal> values, int scale, decimal factor)
    {
        decimal exact = Exact(scale);
        decimal limit = MaxUnscaledMagnitude * factor;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Restore(values[i], factor, exact, limit);
        }
    }

    private static decimal Exact(int scale) => new(1, 0, 0, false, (byte)scale);

    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static decimal Restore(decimal value, decimal factor, decimal exact, decimal limit) =>
        value > limit || value < -limit ? value : decimal.Round(value / factor) * exact;
}
