using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Parquet.SourceGenerator;

/// <summary>
/// Lightweight L1 string cache keyed directly on <see cref="ReadOnlySpan{T}"/>
/// of <see cref="char"/>, backed by a pooled open-addressed table.
/// A cache hit returns the previously materialized <see cref="string"/> instance without
/// allocating, so repeated (categorical) column values cost one string per distinct value
/// rather than one string per row.
/// </summary>
/// <remarks>
/// Hash equality is never trusted on its own: every candidate is confirmed with a full
/// ordinal span comparison before it is returned, so collisions can only cost a probe,
/// never correctness.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct StringDeduplicator : IDisposable
{
    private const int ProbeLimit = 4;

    private string?[]? _entries;
    private readonly int _mask;

    /// <summary>
    /// Initializes a new instance of the <see cref="StringDeduplicator"/> struct with the specified initial table capacity.
    /// </summary>
    /// <param name="capacity">The power-of-two table capacity to allocate (default 512).</param>
    public StringDeduplicator(int capacity = 512)
    {
        _entries = ArrayPool<string?>.Shared.Rent(capacity);
        _mask = capacity - 1;
        Array.Clear(_entries, 0, capacity);
    }

    /// <summary>
    /// Returns the cached string equal to <paramref name="value"/>, inserting it if not already present.
    /// </summary>
    /// <param name="value">The string value to deduplicate.</param>
    /// <returns>The deduplicated string instance, or null if <paramref name="value"/> is null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Deduplicate(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        var entries = _entries;
        if (entries is null)
        {
            return value;
        }

        int mask = _mask;
        int index = (int)(HashString(value) & (uint)mask);
        for (int probe = 0; probe < ProbeLimit; probe++)
        {
            int slot = (index + probe) & mask;
            string? candidate = entries[slot];
            if (candidate is null)
            {
                entries[slot] = value;
                return value;
            }

            // A matching hash is never trusted on its own: compare in full.
            if (string.Equals(candidate, value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        // Probe window exhausted: evict the primary slot so hot values stay resident.
        entries[index] = value;
        return value;
    }

    /// <summary>
    /// Returns the cached string equal to <paramref name="value"/>. A cache hit allocates
    /// nothing at all — no string instance is created for a value already seen.
    /// </summary>
    public string GetOrAdd(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var entries = _entries;
        if (entries is null)
        {
            return value.ToString();
        }

        int mask = _mask;
        int index = (int)(HashSpan(value) & (uint)mask);
        for (int probe = 0; probe < ProbeLimit; probe++)
        {
            int slot = (index + probe) & mask;
            string? candidate = entries[slot];
            if (candidate is null)
            {
                string inserted = value.ToString();
                entries[slot] = inserted;
                return inserted;
            }

            // A matching hash is never trusted on its own: compare in full using SIMD SequenceEqual.
            if (value.SequenceEqual(candidate.AsSpan()))
            {
                return candidate;
            }
        }

        string replacement = value.ToString();
        entries[index] = replacement;
        return replacement;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashSpan(ReadOnlySpan<char> value)
    {
#if NETCOREAPP2_1_OR_GREATER
        return (uint)string.GetHashCode(value, StringComparison.Ordinal);
#else
        uint hash = 2166136261u;
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= 16777619u;
        }
        return hash;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashString(string value)
    {
#if NETCOREAPP2_1_OR_GREATER
        return (uint)string.GetHashCode(value, StringComparison.Ordinal);
#else
        uint hash = 2166136261u;
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= 16777619u;
        }
        return hash;
#endif
    }

    /// <summary>
    /// Returns the rented table buffer to the shared array pool.
    /// </summary>
    public void Dispose()
    {
        var entries = _entries;
        if (entries != null)
        {
            _entries = null;
            ArrayPool<string?>.Shared.Return(entries, clearArray: true);
        }
    }
}
