using System;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace Parquet.SourceGenerator;

/// <summary>
/// Branchless and (where the hardware offers it) SIMD-accelerated construction of Parquet
/// definition levels and packed non-null value buffers from nullable value columns (issue #145).
/// </summary>
/// <remarks>
/// <para>
/// Every routine here is written so the vectorised path and the scalar fallback produce
/// byte-identical output for the same input. The vectorised paths are selected at run time via
/// <c>Vector128.IsHardwareAccelerated</c>; on frameworks below .NET 8, or on hardware without
/// vector support, the scalar path runs and is itself branch-free.
/// </para>
/// <para>
/// The generated write path extracts nullable columns from an array-of-structures source
/// (<c>item.Property</c> over a span of rows), which is not vectorisable — it uses the
/// single-pass branchless form, <see cref="ExtractBranchless{T}"/>, in spirit. These helpers
/// exist for callers that already hold a contiguous <see cref="Nullable{T}"/> column (the
/// columnar hand-off shape) and want the two-pass split/compact form instead.
/// </para>
/// </remarks>
public static class NullableColumnExtractor
{
    /// <summary>
    /// Single-pass branchless extraction from a contiguous nullable column.
    /// Writes <c>0</c>/<c>1</c> definition levels for every slot and packs the non-null payloads
    /// into <paramref name="values"/>, returning the number packed.
    /// </summary>
    /// <remarks>
    /// The payload store is unconditional: a null slot writes a discardable
    /// <c>default(T)</c> at the current compaction cursor, which the next non-null slot
    /// overwrites. The cursor never leaves the buffer because it is always less than or equal to
    /// the loop index.
    /// </remarks>
    /// <typeparam name="T">The column's underlying value type.</typeparam>
    /// <param name="source">The nullable column.</param>
    /// <param name="definitionLevels">Receives one definition level per source slot.</param>
    /// <param name="values">Receives the packed non-null payloads.</param>
    /// <returns>The number of non-null payloads written to <paramref name="values"/>.</returns>
    /// <exception cref="ArgumentException">A destination is shorter than the source.</exception>
    public static int ExtractBranchless<T>(
        ReadOnlySpan<T?> source,
        Span<int> definitionLevels,
        Span<T> values
    )
        where T : struct
    {
        if (definitionLevels.Length < source.Length)
            throw new ArgumentException(
                "Definition level span is shorter than the source span.",
                nameof(definitionLevels)
            );
        if (values.Length < source.Length)
            throw new ArgumentException(
                "Value span is shorter than the source span.",
                nameof(values)
            );

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

    /// <summary>
    /// Two-pass extraction: a branchless split of the nullable column into a presence bitmap plus
    /// an unpacked payload buffer, then a vectorised widen of the bitmap into definition levels
    /// and a vectorised in-place compaction of the payloads.
    /// </summary>
    /// <typeparam name="T">The column's underlying value type.</typeparam>
    /// <param name="source">The nullable column.</param>
    /// <param name="presence">Scratch buffer receiving one <c>0</c>/<c>1</c> byte per slot.</param>
    /// <param name="definitionLevels">Receives one definition level per source slot.</param>
    /// <param name="values">
    /// Receives the payloads: first unpacked (one per slot), then compacted in place.
    /// </param>
    /// <returns>The number of non-null payloads left at the front of <paramref name="values"/>.</returns>
    /// <exception cref="ArgumentException">A destination is shorter than the source.</exception>
    public static int ExtractTwoPass<T>(
        ReadOnlySpan<T?> source,
        Span<byte> presence,
        Span<int> definitionLevels,
        Span<T> values
    )
        where T : struct
    {
        if (definitionLevels.Length < source.Length)
            throw new ArgumentException(
                "Definition level span is shorter than the source span.",
                nameof(definitionLevels)
            );
        if (values.Length < source.Length)
            throw new ArgumentException(
                "Value span is shorter than the source span.",
                nameof(values)
            );
        if (presence.Length < source.Length)
            throw new ArgumentException(
                "Presence buffer is shorter than the source span.",
                nameof(presence)
            );

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

    /// <summary>
    /// Widens a span of <c>0</c>/<c>1</c> presence bytes into <see cref="int"/> definition levels.
    /// </summary>
    /// <param name="presence">The presence bytes.</param>
    /// <param name="definitionLevels">Receives one definition level per presence byte.</param>
    /// <exception cref="ArgumentException">The destination is shorter than the source.</exception>
    public static void ExpandPresenceToDefinitionLevels(
        ReadOnlySpan<byte> presence,
        Span<int> definitionLevels
    )
    {
        if (definitionLevels.Length < presence.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(definitionLevels)
            );

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

    /// <summary>
    /// Counts the set presence bytes.
    /// </summary>
    /// <param name="presence">The presence bytes (each <c>0</c> or <c>1</c>).</param>
    /// <returns>The number of non-zero bytes.</returns>
    public static int CountPresent(ReadOnlySpan<byte> presence)
    {
        int length = presence.Length;
        int i = 0;
        int total = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 16)
        {
            ref byte srcRef = ref MemoryMarshal.GetReference(presence);
            int limit = length - 16;
            // 0/1 bytes: 255 lanes can accumulate before a byte lane could overflow, and each
            // block adds at most 1 per lane, so drain the accumulator every 255 blocks.
            while (i <= limit)
            {
                Vector128<byte> acc = Vector128<byte>.Zero;
                int blocks = 0;
                while (i <= limit && blocks < 255)
                {
                    acc += Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                    i += 16;
                    blocks++;
                }

                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(acc);
                total += Vector128.Sum(lo) + Vector128.Sum(hi);
            }
        }
#endif

        for (; i < length; i++)
        {
            total += presence[i];
        }

        return total;
    }

    /// <summary>
    /// Compacts <paramref name="values"/> in place, keeping only the slots whose presence byte is
    /// non-zero and preserving their order.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The unpacked payloads; on return the kept payloads sit at the front.</param>
    /// <param name="presence">One presence byte per payload.</param>
    /// <returns>The number of payloads kept.</returns>
    /// <exception cref="ArgumentException">The presence span is shorter than the value span.</exception>
    public static int CompactInPlace<T>(Span<T> values, ReadOnlySpan<byte> presence)
        where T : struct
    {
        if (presence.Length < values.Length)
            throw new ArgumentException(
                "Presence span is shorter than the value span.",
                nameof(presence)
            );

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
                    // Whole block is null: nothing to keep.
                    i += 16;
                    continue;
                }

                if (block == ones)
                {
                    // Whole block is present: one bulk move instead of sixteen.
                    if (packed != i)
                    {
                        values.Slice(i, 16).CopyTo(values.Slice(packed, 16));
                    }

                    packed += 16;
                    i += 16;
                    continue;
                }

                // Mixed block: branchless element-at-a-time compaction.
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
