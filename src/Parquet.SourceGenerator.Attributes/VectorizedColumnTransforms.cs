using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace Parquet.SourceGenerator;

/// <summary>
/// Hardware-accelerated (SIMD) column transformation utilities for Parquet column arithmetic,
/// timestamp conversions, numeric scaling, and integer widening/narrowing.
/// </summary>
public static class VectorizedColumnTransforms
{
    /// <summary>
    /// The number of 100-nanosecond ticks between 0001-01-01 and the Unix epoch (1970-01-01T00:00:00Z).
    /// </summary>
    public static long UnixEpochTicks => 621355968000000000L;

    /// <summary>
    /// Binary bitmask constant representing <see cref="DateTimeKind.Utc"/> at the Unix epoch.
    /// In .NET, <see cref="DateTime"/> stores kind in the upper 2 bits of its 64-bit internal representation.
    /// </summary>
    public static long UtcEpochConstant => 621355968000000000L | (1L << 62);

    private const long TicksMask = 0x3FFFFFFFFFFFFFFFL;

    // ──────────────────────────────────────────────────────────
    //  TIMESTAMP CONVERSIONS
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Converts an array or span of Unix epoch microseconds to .NET DateTime ticks.
    /// </summary>
    public static void ConvertEpochMicrosecondsToTicks(
        ReadOnlySpan<long> source,
        Span<long> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && length >= 8)
        {
            var vMultiplier = Vector256.Create(10L);
            var vEpoch = Vector256.Create(UnixEpochTicks);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 8;

            while (i <= limit)
            {
                var v0 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector256.LoadUnsafe(ref srcRef, (nuint)(i + 4));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i += 8;
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= 4)
        {
            var vMultiplier = Vector128.Create(10L);
            var vEpoch = Vector128.Create(UnixEpochTicks);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 4;

            while (i <= limit)
            {
                var v0 = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 2));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 2));
                i += 4;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = (source[i] * 10L) + UnixEpochTicks;
        }
    }

    /// <summary>
    /// Converts an array or span of Unix epoch microseconds directly to UTC <see cref="DateTime"/> values.
    /// </summary>
    public static void ConvertEpochMicrosecondsToDateTime(
        ReadOnlySpan<long> source,
        Span<DateTime> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        Span<long> rawDst = MemoryMarshal.Cast<DateTime, long>(destination);
        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && length >= 8)
        {
            var vMultiplier = Vector256.Create(10L);
            var vEpoch = Vector256.Create(UtcEpochConstant);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(rawDst);
            int limit = length - 8;

            while (i <= limit)
            {
                var v0 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector256.LoadUnsafe(ref srcRef, (nuint)(i + 4));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i += 8;
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= 4)
        {
            var vMultiplier = Vector128.Create(10L);
            var vEpoch = Vector128.Create(UtcEpochConstant);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(rawDst);
            int limit = length - 4;

            while (i <= limit)
            {
                var v0 = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 2));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 2));
                i += 4;
            }
        }
#endif

        for (; i < length; i++)
        {
            rawDst[i] = (source[i] * 10L) + UtcEpochConstant;
        }
    }

    /// <summary>
    /// Converts an array or span of .NET DateTime ticks to Unix epoch microseconds.
    /// </summary>
    public static void ConvertTicksToEpochMicroseconds(
        ReadOnlySpan<long> source,
        Span<long> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        for (int i = 0; i < length; i++)
        {
            destination[i] = (source[i] - UnixEpochTicks) / 10L;
        }
    }

    /// <summary>
    /// Converts an array or span of <see cref="DateTime"/> values to Unix epoch microseconds.
    /// </summary>
    public static void ConvertDateTimeToEpochMicroseconds(
        ReadOnlySpan<DateTime> source,
        Span<long> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        ReadOnlySpan<long> rawSrc = MemoryMarshal.Cast<DateTime, long>(source);
        int length = source.Length;
        for (int i = 0; i < length; i++)
        {
            long ticks = rawSrc[i] & TicksMask;
            destination[i] = (ticks - UnixEpochTicks) / 10L;
        }
    }

    /// <summary>
    /// Converts an array or span of Unix epoch milliseconds to .NET DateTime ticks.
    /// </summary>
    public static void ConvertEpochMillisecondsToTicks(
        ReadOnlySpan<long> source,
        Span<long> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && length >= 8)
        {
            var vMultiplier = Vector256.Create(10_000L);
            var vEpoch = Vector256.Create(UnixEpochTicks);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 8;

            while (i <= limit)
            {
                var v0 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector256.LoadUnsafe(ref srcRef, (nuint)(i + 4));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i += 8;
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= 4)
        {
            var vMultiplier = Vector128.Create(10_000L);
            var vEpoch = Vector128.Create(UnixEpochTicks);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 4;

            while (i <= limit)
            {
                var v0 = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 2));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 2));
                i += 4;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = (source[i] * 10_000L) + UnixEpochTicks;
        }
    }

    /// <summary>
    /// Converts an array or span of Unix epoch milliseconds directly to UTC <see cref="DateTime"/> values.
    /// </summary>
    public static void ConvertEpochMillisecondsToDateTime(
        ReadOnlySpan<long> source,
        Span<DateTime> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        Span<long> rawDst = MemoryMarshal.Cast<DateTime, long>(destination);
        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && length >= 8)
        {
            var vMultiplier = Vector256.Create(10_000L);
            var vEpoch = Vector256.Create(UtcEpochConstant);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(rawDst);
            int limit = length - 8;

            while (i <= limit)
            {
                var v0 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector256.LoadUnsafe(ref srcRef, (nuint)(i + 4));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i += 8;
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= 4)
        {
            var vMultiplier = Vector128.Create(10_000L);
            var vEpoch = Vector128.Create(UtcEpochConstant);
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(rawDst);
            int limit = length - 4;

            while (i <= limit)
            {
                var v0 = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 2));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.StoreUnsafe(ref dstRef, (nuint)i);
                r1.StoreUnsafe(ref dstRef, (nuint)(i + 2));
                i += 4;
            }
        }
#endif

        for (; i < length; i++)
        {
            rawDst[i] = (source[i] * 10_000L) + UtcEpochConstant;
        }
    }

    /// <summary>
    /// Converts an array or span of .NET DateTime ticks to Unix epoch milliseconds.
    /// </summary>
    public static void ConvertTicksToEpochMilliseconds(
        ReadOnlySpan<long> source,
        Span<long> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        for (int i = 0; i < length; i++)
        {
            destination[i] = (source[i] - UnixEpochTicks) / 10_000L;
        }
    }

    /// <summary>
    /// Converts an array or span of <see cref="DateTime"/> values to Unix epoch milliseconds.
    /// </summary>
    public static void ConvertDateTimeToEpochMilliseconds(
        ReadOnlySpan<DateTime> source,
        Span<long> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        ReadOnlySpan<long> rawSrc = MemoryMarshal.Cast<DateTime, long>(source);
        int length = source.Length;
        for (int i = 0; i < length; i++)
        {
            long ticks = rawSrc[i] & TicksMask;
            destination[i] = (ticks - UnixEpochTicks) / 10_000L;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  NUMERIC SCALING
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Multiplies 64-bit floating-point column values by a uniform scaling factor using SIMD vectorization.
    /// </summary>
    public static void MultiplyScale(
        ReadOnlySpan<double> source,
        double factor,
        Span<double> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && length >= 8)
        {
            var vFactor = Vector256.Create(factor);
            ref double srcRef = ref MemoryMarshal.GetReference(source);
            ref double dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 8;

            while (i <= limit)
            {
                var v0 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector256.LoadUnsafe(ref srcRef, (nuint)(i + 4));
                (v0 * vFactor).StoreUnsafe(ref dstRef, (nuint)i);
                (v1 * vFactor).StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i += 8;
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= 4)
        {
            var vFactor = Vector128.Create(factor);
            ref double srcRef = ref MemoryMarshal.GetReference(source);
            ref double dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 4;

            while (i <= limit)
            {
                var v0 = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 2));
                (v0 * vFactor).StoreUnsafe(ref dstRef, (nuint)i);
                (v1 * vFactor).StoreUnsafe(ref dstRef, (nuint)(i + 2));
                i += 4;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = source[i] * factor;
        }
    }

    /// <summary>
    /// Multiplies 32-bit floating-point column values by a uniform scaling factor using SIMD vectorization.
    /// </summary>
    public static void MultiplyScale(
        ReadOnlySpan<float> source,
        float factor,
        Span<float> destination
    )
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && length >= 16)
        {
            var vFactor = Vector256.Create(factor);
            ref float srcRef = ref MemoryMarshal.GetReference(source);
            ref float dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 16;

            while (i <= limit)
            {
                var v0 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector256.LoadUnsafe(ref srcRef, (nuint)(i + 8));
                (v0 * vFactor).StoreUnsafe(ref dstRef, (nuint)i);
                (v1 * vFactor).StoreUnsafe(ref dstRef, (nuint)(i + 8));
                i += 16;
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= 8)
        {
            var vFactor = Vector128.Create(factor);
            ref float srcRef = ref MemoryMarshal.GetReference(source);
            ref float dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 8;

            while (i <= limit)
            {
                var v0 = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var v1 = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 4));
                (v0 * vFactor).StoreUnsafe(ref dstRef, (nuint)i);
                (v1 * vFactor).StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i += 8;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = source[i] * factor;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  INTEGER WIDENING & NARROWING
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Widens 32-bit signed integers to 64-bit signed integers using SIMD vectorization.
    /// </summary>
    public static void Widen(ReadOnlySpan<int> source, Span<long> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 4)
        {
            ref int srcRef = ref MemoryMarshal.GetReference(source);
            ref long dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 4;

            while (i <= limit)
            {
                var v = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var (lower, upper) = Vector128.Widen(v);
                lower.StoreUnsafe(ref dstRef, (nuint)i);
                upper.StoreUnsafe(ref dstRef, (nuint)(i + 2));
                i += 4;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = source[i];
        }
    }

    /// <summary>
    /// Narrows 64-bit signed integers to 32-bit signed integers with truncation using SIMD vectorization.
    /// </summary>
    public static void Narrow(ReadOnlySpan<long> source, Span<int> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 4)
        {
            ref long srcRef = ref MemoryMarshal.GetReference(source);
            ref int dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 4;

            while (i <= limit)
            {
                var lower = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var upper = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 2));
                var narrowed = Vector128.Narrow(lower, upper);
                narrowed.StoreUnsafe(ref dstRef, (nuint)i);
                i += 4;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = (int)source[i];
        }
    }

    /// <summary>
    /// Widens 16-bit signed integers to 32-bit signed integers using SIMD vectorization.
    /// </summary>
    public static void Widen(ReadOnlySpan<short> source, Span<int> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 8)
        {
            ref short srcRef = ref MemoryMarshal.GetReference(source);
            ref int dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 8;

            while (i <= limit)
            {
                var v = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var (lower, upper) = Vector128.Widen(v);
                lower.StoreUnsafe(ref dstRef, (nuint)i);
                upper.StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i += 8;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = source[i];
        }
    }

    /// <summary>
    /// Narrows 32-bit signed integers to 16-bit signed integers with truncation using SIMD vectorization.
    /// </summary>
    public static void Narrow(ReadOnlySpan<int> source, Span<short> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 8)
        {
            ref int srcRef = ref MemoryMarshal.GetReference(source);
            ref short dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 8;

            while (i <= limit)
            {
                var lower = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var upper = Vector128.LoadUnsafe(ref srcRef, (nuint)(i + 4));
                var narrowed = Vector128.Narrow(lower, upper);
                narrowed.StoreUnsafe(ref dstRef, (nuint)i);
                i += 8;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = (short)source[i];
        }
    }

    /// <summary>
    /// Widens 8-bit unsigned integers to 32-bit signed integers using SIMD vectorization.
    /// </summary>
    public static void Widen(ReadOnlySpan<byte> source, Span<int> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException(
                "Destination span is shorter than source span.",
                nameof(destination)
            );

        int length = source.Length;
        int i = 0;

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && length >= 16)
        {
            ref byte srcRef = ref MemoryMarshal.GetReference(source);
            ref int dstRef = ref MemoryMarshal.GetReference(destination);
            int limit = length - 16;

            while (i <= limit)
            {
                var v = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var (w0, w1) = Vector128.Widen(v);
                var (i0, i1) = Vector128.Widen(w0);
                var (i2, i3) = Vector128.Widen(w1);

                i0.AsInt32().StoreUnsafe(ref dstRef, (nuint)i);
                i1.AsInt32().StoreUnsafe(ref dstRef, (nuint)(i + 4));
                i2.AsInt32().StoreUnsafe(ref dstRef, (nuint)(i + 8));
                i3.AsInt32().StoreUnsafe(ref dstRef, (nuint)(i + 12));
                i += 16;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = source[i];
        }
    }
}
