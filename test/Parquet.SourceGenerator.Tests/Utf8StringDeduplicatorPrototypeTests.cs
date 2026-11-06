using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Prototype of the UTF-8 byte-span keyed deduplication table described by issue #143.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <b>not</b> wired into the generated reader: Parquet.Net 6.1.0 exposes no
/// byte-level surface for UTF-8 string columns (see <c>UPSTREAM_DEPENDENCY_LIMITATIONS.md</c>),
/// so there is nowhere to feed it from today. It is kept — and tested — so the moment an upstream
/// <c>ReadRawAsync&lt;ReadOnlyMemory&lt;byte&gt;&gt;</c> surface exists, the emitter has a proven
/// table to bind to. Round-tripping bytes through a <see cref="string"/> just to call this would
/// defeat its entire purpose, so nothing in the shipped reader does that.
/// </para>
/// <para>
/// Correctness rule, identical to the shipped UTF-16 table: a hash match alone never returns a
/// value. Every candidate is confirmed by a full byte-wise comparison.
/// </para>
/// </remarks>
internal struct Utf8StringDeduplicator : IDisposable
{
    private const int ProbeLimit = 4;

    private string?[]? _entries;
    private byte[]?[]? _keys;
    private readonly int _mask;

    public Utf8StringDeduplicator(int capacity = 512)
    {
        _entries = ArrayPool<string?>.Shared.Rent(capacity);
        _keys = ArrayPool<byte[]?>.Shared.Rent(capacity);
        _mask = capacity - 1;
        Array.Clear(_entries, 0, capacity);
        Array.Clear(_keys, 0, capacity);
    }

    /// <summary>
    /// Returns the cached string for <paramref name="utf8"/>, decoding only on a cache miss.
    /// </summary>
    public string GetOrAdd(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
        {
            return string.Empty;
        }

        var entries = _entries;
        var keys = _keys;
        if (entries is null || keys is null)
        {
            return Encoding.UTF8.GetString(utf8);
        }

        int mask = _mask;
        int index = (int)(Hash(utf8) & (uint)mask);
        for (int probe = 0; probe < ProbeLimit; probe++)
        {
            int slot = (index + probe) & mask;
            byte[]? key = keys[slot];
            if (key is null)
            {
                return Insert(entries, keys, slot, utf8);
            }

            // Hash equality is never trusted: full byte-wise comparison decides.
            if (utf8.SequenceEqual(key))
            {
                return entries[slot]!;
            }
        }

        // Probe window exhausted: evict the primary slot so hot values stay resident.
        return Insert(entries, keys, index, utf8);
    }

    private static string Insert(
        string?[] entries,
        byte[]?[] keys,
        int slot,
        ReadOnlySpan<byte> utf8
    )
    {
        // Encoding.UTF8 is the replacement-fallback decoder, so invalid sequences become U+FFFD
        // rather than throwing — matching what Parquet.Net itself produces for the same bytes.
        string decoded = Encoding.UTF8.GetString(utf8);
        keys[slot] = utf8.ToArray();
        entries[slot] = decoded;
        return decoded;
    }

    /// <summary>FNV-1a over raw UTF-8 bytes; allocation free.</summary>
    private static uint Hash(ReadOnlySpan<byte> utf8)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < utf8.Length; i++)
        {
            hash ^= utf8[i];
            hash *= 16777619u;
        }

        return hash;
    }

    public void Dispose()
    {
        var entries = _entries;
        var keys = _keys;
        _entries = null;
        _keys = null;
        if (entries is not null)
        {
            ArrayPool<string?>.Shared.Return(entries, clearArray: true);
        }

        if (keys is not null)
        {
            ArrayPool<byte[]?>.Shared.Return(keys, clearArray: true);
        }
    }
}

public sealed class Utf8StringDeduplicatorPrototypeTests
{
    [Fact]
    public void EmptySpanReturnsEmptyStringWithoutTouchingTheTable()
    {
        using var cache = new Utf8StringDeduplicator(16);
        cache.GetOrAdd(ReadOnlySpan<byte>.Empty).ShouldBeSameAs(string.Empty);
        cache.GetOrAdd(Array.Empty<byte>()).ShouldBeSameAs(string.Empty);
    }

    [Fact]
    public void RepeatedValuesReturnTheSameInstance()
    {
        using var cache = new Utf8StringDeduplicator(64);
        byte[] a = Encoding.UTF8.GetBytes("Self-emp-not-inc");
        byte[] b = Encoding.UTF8.GetBytes("Self-emp-not-inc");

        string first = cache.GetOrAdd(a);
        string second = cache.GetOrAdd(b);

        first.ShouldBe("Self-emp-not-inc");
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public void EmbeddedNulBytesArePreservedAndDiscriminated()
    {
        using var cache = new Utf8StringDeduplicator(64);
        byte[] withNul = { (byte)'a', 0, (byte)'b' };
        byte[] withoutNul = { (byte)'a', (byte)'b' };

        string a = cache.GetOrAdd(withNul);
        string b = cache.GetOrAdd(withoutNul);

        a.Length.ShouldBe(3);
        a[1].ShouldBe('\0');
        b.ShouldBe("ab");
        b.ShouldNotBeSameAs(a);
    }

    [Fact]
    public void InvalidUtf8DecodesToReplacementCharactersAndStaysStable()
    {
        using var cache = new Utf8StringDeduplicator(64);
        byte[] loneContinuation = { 0x80, 0x81 };
        byte[] truncatedSequence = { 0xE2, 0x82 }; // start of U+20AC, cut short
        byte[] overlong = { 0xC0, 0xAF };

        string a = cache.GetOrAdd(loneContinuation);
        string b = cache.GetOrAdd(truncatedSequence);
        string c = cache.GetOrAdd(overlong);

        a.ShouldBe(Encoding.UTF8.GetString(loneContinuation));
        b.ShouldBe(Encoding.UTF8.GetString(truncatedSequence));
        c.ShouldBe(Encoding.UTF8.GetString(overlong));

        // Distinct invalid byte sequences can decode to the same replacement text, but the
        // cache keys on bytes so each keeps its own entry and none is confused for another.
        cache.GetOrAdd(loneContinuation).ShouldBeSameAs(a);
        cache.GetOrAdd(truncatedSequence).ShouldBeSameAs(b);
        cache.GetOrAdd(overlong).ShouldBeSameAs(c);
    }

    [Fact]
    public void VeryLongValuesSharingAPrefixAreNotConflated()
    {
        using var cache = new Utf8StringDeduplicator(64);
        byte[] left = Encoding.UTF8.GetBytes(new string('x', 500_000) + "A");
        byte[] right = Encoding.UTF8.GetBytes(new string('x', 500_000) + "B");

        string a = cache.GetOrAdd(left);
        string b = cache.GetOrAdd(right);

        a.ShouldEndWith("A", Case.Sensitive);
        b.ShouldEndWith("B", Case.Sensitive);
        cache.GetOrAdd(left).ShouldBeSameAs(a);
        cache.GetOrAdd(right).ShouldBeSameAs(b);
    }

    [Fact]
    public void HashCollisionsNeverReturnTheWrongValue()
    {
        const int Capacity = 512;
        const int Mask = Capacity - 1;

        // Gather distinct values that all hash into the same bucket.
        int target = -1;
        var colliding = new List<byte[]>();
        for (int i = 0; colliding.Count < 64 && i < 5_000_000; i++)
        {
            byte[] candidate = Encoding.UTF8.GetBytes(
                "v" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            int bucket = (int)(Fnv1a(candidate) & Mask);
            if (target < 0)
            {
                target = bucket;
            }

            if (bucket == target)
            {
                colliding.Add(candidate);
            }
        }

        colliding.Count.ShouldBe(64);

        using var cache = new Utf8StringDeduplicator(Capacity);
        for (int round = 0; round < 4; round++)
        {
            foreach (byte[] value in colliding)
            {
                cache.GetOrAdd(value).ShouldBe(Encoding.UTF8.GetString(value));
            }
        }
    }

    [Fact]
    public void CacheHitsAllocateNothing()
    {
        using var cache = new Utf8StringDeduplicator(64);
        byte[][] values =
        {
            Encoding.UTF8.GetBytes("Private"),
            Encoding.UTF8.GetBytes("Local-gov"),
            Encoding.UTF8.GetBytes("Self-emp-not-inc"),
            Encoding.UTF8.GetBytes("?"),
        };

        // Prime the table.
        foreach (byte[] value in values)
        {
            _ = cache.GetOrAdd(value);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            _ = cache.GetOrAdd(values[i & 3]);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0);
    }

    private static uint Fnv1a(ReadOnlySpan<byte> utf8)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < utf8.Length; i++)
        {
            hash ^= utf8[i];
            hash *= 16777619u;
        }

        return hash;
    }
}
