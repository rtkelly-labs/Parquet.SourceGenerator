using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace Parquet.SourceGenerator.Benchmarks;

/// <summary>
/// Re-measures the SIMD nullable-column extraction left behind by PR #210 (issue #145) on the
/// input it was never tested against: a genuinely columnar <c>T?[]</c>.
/// </summary>
/// <remarks>
/// <para>
/// #210 measured the two-pass vectorised split/compact against the write path's array-of-structures
/// source (<c>item.Property</c> over a span of rows), where the load is scattered and the vector
/// path lost. It shipped the helper anyway, noting it should be re-measured "on genuinely columnar
/// input" — which is exactly the input issue #137's hand-off deals with.
/// </para>
/// <para>
/// The routines below are a benchmark-local copy of #210's <c>NullableColumnExtractor</c>,
/// deliberately not a third shipped copy of that API: #210 owns the public surface, and this file
/// exists only to produce a number. Keep them byte-identical in behaviour to the originals.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[InProcess]
[WarmupCount(3)]
[IterationCount(15)]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ColumnarNullExtractionProbe
{
    private long?[] _column = null!;
    private long[] _values = null!;
    private int[] _definitionLevels = null!;
    private byte[] _presence = null!;

    [Params(50_000)]
    public int Count { get; set; }

    /// <summary>Share of slots that are null. Density changes which path the compaction takes.</summary>
    [Params(0, 6, 50)]
    public int NullPercent { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _column = new long?[Count];
        for (int i = 0; i < Count; i++)
        {
            bool isNull = NullPercent != 0 && i % 100 < NullPercent;
            _column[i] = isNull ? null : i * 7L;
        }

        _values = new long[Count];
        _definitionLevels = new int[Count];
        _presence = new byte[Count];
    }

    [Benchmark(Baseline = true, Description = "Branchless single pass (#210 production shape)")]
    public int Branchless() => ExtractBranchless<long>(_column, _definitionLevels, _values);

    [Benchmark(Description = "Two-pass SIMD split + widen + compact")]
    public int TwoPassSimd() =>
        ExtractTwoPass<long>(_column, _presence, _definitionLevels, _values);

    // ──────────────────────────────────────────────────────────
    //  Benchmark-local copy of PR #210's NullableColumnExtractor
    // ──────────────────────────────────────────────────────────

    private static int ExtractBranchless<T>(
        ReadOnlySpan<T?> source,
        Span<int> definitionLevels,
        Span<T> values
    )
        where T : struct
    {
        int length = source.Length;
        int packed = 0;
        for (int i = 0; i < length; i++)
        {
            T? slot = source[i];
            int flag = slot.HasValue ? 1 : 0;
            values[packed] = slot.GetValueOrDefault();
            packed += flag;
            definitionLevels[i] = flag;
        }

        return packed;
    }

    private static int ExtractTwoPass<T>(
        ReadOnlySpan<T?> source,
        Span<byte> presence,
        Span<int> definitionLevels,
        Span<T> values
    )
        where T : struct
    {
        int length = source.Length;
        for (int i = 0; i < length; i++)
        {
            T? slot = source[i];
            presence[i] = slot.HasValue ? (byte)1 : (byte)0;
            values[i] = slot.GetValueOrDefault();
        }

        ReadOnlySpan<byte> flags = presence.Slice(0, length);
        ExpandPresenceToDefinitionLevels(flags, definitionLevels);
        return CompactInPlace(values.Slice(0, length), flags);
    }

    private static void ExpandPresenceToDefinitionLevels(
        ReadOnlySpan<byte> presence,
        Span<int> definitionLevels
    )
    {
        int length = presence.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 16)
        {
            ref byte srcRef = ref MemoryMarshal.GetReference(presence);
            ref int dstRef = ref MemoryMarshal.GetReference(definitionLevels);
            int limit = length - 16;

            while (i <= limit)
            {
                Vector128<byte> bytes = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                (Vector128<ushort> lo16, Vector128<ushort> hi16) = Vector128.Widen(bytes);
                (Vector128<uint> q0, Vector128<uint> q1) = Vector128.Widen(lo16);
                (Vector128<uint> q2, Vector128<uint> q3) = Vector128.Widen(hi16);
                q0.AsInt32().StoreUnsafe(ref dstRef, (nuint)i);
                q1.AsInt32().StoreUnsafe(ref dstRef, (nuint)(i + 4));
                q2.AsInt32().StoreUnsafe(ref dstRef, (nuint)(i + 8));
                q3.AsInt32().StoreUnsafe(ref dstRef, (nuint)(i + 12));
                i += 16;
            }
        }
#endif

        for (; i < length; i++)
        {
            definitionLevels[i] = presence[i];
        }
    }

    private static int CompactInPlace<T>(Span<T> values, ReadOnlySpan<byte> presence)
        where T : struct
    {
        int length = values.Length;
        int i = 0;
        int packed = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 16)
        {
            ref byte flagRef = ref MemoryMarshal.GetReference(presence);
            Vector128<byte> ones = Vector128.Create((byte)1);
            int limit = length - 16;

            while (i <= limit)
            {
                Vector128<byte> block = Vector128.LoadUnsafe(ref flagRef, (nuint)i);
                if (block == Vector128<byte>.Zero)
                {
                    i += 16;
                    continue;
                }

                if (block == ones)
                {
                    if (packed != i)
                    {
                        values.Slice(i, 16).CopyTo(values.Slice(packed, 16));
                    }

                    packed += 16;
                    i += 16;
                    continue;
                }

                for (int k = 0; k < 16; k++)
                {
                    values[packed] = values[i + k];
                    packed += presence[i + k];
                }

                i += 16;
            }
        }
#endif

        for (; i < length; i++)
        {
            values[packed] = values[i];
            packed += presence[i];
        }

        return packed;
    }
}
