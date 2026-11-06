using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Parquet.SourceGenerator;

/// <summary>
/// The <c>min_value</c> / <c>max_value</c> / <c>null_count</c> zone map a Parquet writer records for one
/// column chunk, projected onto the CLR type of the generated model property (issue #149).
/// </summary>
/// <typeparam name="T">The property's non-nullable CLR type.</typeparam>
/// <remarks>
/// <para>
/// Statistics are advisory: a writer may omit them, and a file produced by another engine may
/// record them in a physical type this projection cannot map. Both cases surface as
/// <see cref="HasMinMax"/> being <see langword="false"/>, and the generated readers never prune a
/// row group whose statistics are incomplete.
/// </para>
/// <para>
/// Prefer the <c>May*</c> helpers over raw <see cref="Min"/> / <see cref="Max"/> in a pruning
/// predicate: they answer "could this row group contain a matching row?", and answer
/// <see langword="true"/> whenever statistics are missing, so a predicate built from them can never
/// prune away real data.
/// </para>
/// <para>
/// Ordering uses <see cref="Comparer{T}.Default"/>, except for <see cref="string"/>, which uses
/// <see cref="StringComparer.Ordinal"/>. Parquet orders binary columns by unsigned UTF-8 bytes; an
/// ordinal UTF-16 comparison agrees with that across all of ASCII and disagrees only where surrogate
/// pairs meet the U+E000..U+FFFF range.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct ParquetColumnStatistics<T> : IEquatable<ParquetColumnStatistics<T>>
{
    /// <summary>
    /// Ordering used for every range test. <see cref="Comparer{T}.Default"/> for a string means
    /// <see cref="string.CompareTo(string)"/>, which is culture-sensitive and would order values
    /// nothing like the writer that produced the zone map; binary columns get an ordinal comparer
    /// instead, which at least agrees with Parquet's unsigned byte order across ASCII.
    /// </summary>
    private static readonly IComparer<T> Ordering =
        typeof(T) == typeof(string) ? (IComparer<T>)StringComparer.Ordinal : Comparer<T>.Default;

    private readonly T _min;
    private readonly T _max;
    private readonly bool _hasMin;
    private readonly bool _hasMax;

    /// <summary>
    /// Creates a projected column-chunk zone map.
    /// </summary>
    /// <param name="hasMin">Whether <paramref name="min"/> carries a recorded minimum.</param>
    /// <param name="min">The chunk minimum, when <paramref name="hasMin"/> is set.</param>
    /// <param name="hasMax">Whether <paramref name="max"/> carries a recorded maximum.</param>
    /// <param name="max">The chunk maximum, when <paramref name="hasMax"/> is set.</param>
    /// <param name="nullCount">The chunk's recorded null count, when present.</param>
    /// <param name="distinctCount">The chunk's recorded distinct count, when present.</param>
    public ParquetColumnStatistics(
        bool hasMin,
        T min,
        bool hasMax,
        T max,
        long? nullCount,
        long? distinctCount
    )
    {
        _hasMin = hasMin;
        _hasMax = hasMax;
        _min = min;
        _max = max;
        NullCount = nullCount;
        DistinctCount = distinctCount;
    }

    /// <summary>Whether the chunk recorded both a minimum and a maximum this projection could map.</summary>
    public bool HasMinMax => _hasMin && _hasMax;

    /// <summary>The recorded minimum, or <c>default</c> when <see cref="HasMinMax"/> is false.</summary>
    public T? Min => _hasMin ? _min : default;

    /// <summary>The recorded maximum, or <c>default</c> when <see cref="HasMinMax"/> is false.</summary>
    public T? Max => _hasMax ? _max : default;

    /// <summary>The chunk's recorded null count, when the writer stored one.</summary>
    public long? NullCount { get; }

    /// <summary>The chunk's recorded distinct count, when the writer stored one.</summary>
    public long? DistinctCount { get; }

    /// <summary>Whether the chunk is known to hold no nulls at all.</summary>
    public bool IsKnownNonNull => NullCount == 0;

    /// <summary>Could this chunk hold a value equal to <paramref name="value"/>? (<c>x == value</c>)</summary>
    public bool MayContain(T value) =>
        !HasMinMax || (Ordering.Compare(value, _min) >= 0 && Ordering.Compare(value, _max) <= 0);

    /// <summary>Could this chunk hold a value <c>&gt;= threshold</c>?</summary>
    public bool MayContainAtLeast(T threshold) =>
        !HasMinMax || Ordering.Compare(_max, threshold) >= 0;

    /// <summary>Could this chunk hold a value <c>&gt; threshold</c>?</summary>
    public bool MayContainGreaterThan(T threshold) =>
        !HasMinMax || Ordering.Compare(_max, threshold) > 0;

    /// <summary>Could this chunk hold a value <c>&lt;= threshold</c>?</summary>
    public bool MayContainAtMost(T threshold) =>
        !HasMinMax || Ordering.Compare(_min, threshold) <= 0;

    /// <summary>Could this chunk hold a value <c>&lt; threshold</c>?</summary>
    public bool MayContainLessThan(T threshold) =>
        !HasMinMax || Ordering.Compare(_min, threshold) < 0;

    /// <summary>Could this chunk hold a value in the inclusive range <c>[low, high]</c>?</summary>
    public bool MayContainBetween(T low, T high) =>
        !HasMinMax || (Ordering.Compare(_max, low) >= 0 && Ordering.Compare(_min, high) <= 0);

    /// <summary>Could this chunk hold any value from <paramref name="values"/>? (<c>IN (...)</c>)</summary>
    /// <param name="values">The candidate values. A null or empty set is treated as "no match possible".</param>
    public bool MayContainAny(params T[]? values)
    {
        if (!HasMinMax)
            return true;
        if (values == null)
            return false;
        for (int i = 0; i < values.Length; i++)
        {
            if (MayContain(values[i]))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool Equals(ParquetColumnStatistics<T> other) =>
        _hasMin == other._hasMin
        && _hasMax == other._hasMax
        && NullCount == other.NullCount
        && DistinctCount == other.DistinctCount
        && EqualityComparer<T>.Default.Equals(_min, other._min)
        && EqualityComparer<T>.Default.Equals(_max, other._max);

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is ParquetColumnStatistics<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + (_hasMin ? EqualityComparer<T>.Default.GetHashCode(_min!) : 0);
            hash = (hash * 31) + (_hasMax ? EqualityComparer<T>.Default.GetHashCode(_max!) : 0);
            hash = (hash * 31) + NullCount.GetHashCode();
            hash = (hash * 31) + DistinctCount.GetHashCode();
            return hash;
        }
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(
        ParquetColumnStatistics<T> left,
        ParquetColumnStatistics<T> right
    ) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(
        ParquetColumnStatistics<T> left,
        ParquetColumnStatistics<T> right
    ) => !left.Equals(right);
}

/// <summary>
/// Projects the loosely typed <c>MinValue</c> / <c>MaxValue</c> objects Parquet.Net hands back onto the
/// CLR type of a generated model property. Called only by generated code, once per row group.
/// </summary>
public static class ParquetColumnStatistics
{
    /// <summary>
    /// Builds a typed zone map from raw column-chunk statistics. A value that is neither already a
    /// <typeparamref name="T"/> nor losslessly convertible to one is dropped, which leaves
    /// <see cref="ParquetColumnStatistics{T}.HasMinMax"/> false and therefore disables pruning for
    /// that column rather than risking a wrong skip.
    /// </summary>
    /// <typeparam name="T">The property's non-nullable CLR type.</typeparam>
    /// <param name="min">The raw recorded minimum, or <see langword="null"/>.</param>
    /// <param name="max">The raw recorded maximum, or <see langword="null"/>.</param>
    /// <param name="nullCount">The recorded null count, when present.</param>
    /// <param name="distinctCount">The recorded distinct count, when present.</param>
    /// <returns>The projected statistics.</returns>
    public static ParquetColumnStatistics<T> FromRaw<T>(
        object? min,
        object? max,
        long? nullCount,
        long? distinctCount
    )
    {
        bool hasMin = TryProject(min, out T minValue);
        bool hasMax = TryProject(max, out T maxValue);
        return new ParquetColumnStatistics<T>(
            hasMin,
            minValue,
            hasMax,
            maxValue,
            nullCount,
            distinctCount
        );
    }

    private static bool TryProject<T>(object? raw, out T value)
    {
        if (raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        if (raw == null)
            return false;

        // Parquet stores the physical type: an INT32 chunk backs a short/byte/uint property, an
        // INT64 chunk a ulong one. Convert only across the primitive numeric ladder, and treat an
        // out-of-range or non-numeric value as "no statistics" rather than guessing.
        if (raw is not IConvertible)
            return false;

        try
        {
            value = (T)
                Convert.ChangeType(
                    raw,
                    typeof(T),
                    System.Globalization.CultureInfo.InvariantCulture
                );
            return true;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
