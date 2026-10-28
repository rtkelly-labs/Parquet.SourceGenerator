using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Parquet.SourceGenerator;

/// <summary>
/// Hardware-accelerated (SIMD) column transformation utilities for Parquet column arithmetic,
/// timestamp conversions, numeric scaling, and integer widening/narrowing.
/// </summary>
internal static class VectorizedColumnTransforms
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
        if (Vector.IsHardwareAccelerated && length >= Vector<long>.Count)
        {
            var vMultiplier = new Vector<long>(10L);
            var vEpoch = new Vector<long>(UnixEpochTicks);
            int step = Vector<long>.Count;
            int step2 = step * 2;
            int limit2 = length - step2;

            while (i <= limit2)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var v1 = new Vector<long>(source.Slice(i + step));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.CopyTo(destination.Slice(i));
                r1.CopyTo(destination.Slice(i + step));
                i += step2;
            }

            int limit = length - step;
            while (i <= limit)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var r0 = (v0 * vMultiplier) + vEpoch;
                r0.CopyTo(destination.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<long>.Count)
        {
            var vMultiplier = new Vector<long>(10L);
            var vEpoch = new Vector<long>(UtcEpochConstant);
            int step = Vector<long>.Count;
            int step2 = step * 2;
            int limit2 = length - step2;

            while (i <= limit2)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var v1 = new Vector<long>(source.Slice(i + step));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.CopyTo(rawDst.Slice(i));
                r1.CopyTo(rawDst.Slice(i + step));
                i += step2;
            }

            int limit = length - step;
            while (i <= limit)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var r0 = (v0 * vMultiplier) + vEpoch;
                r0.CopyTo(rawDst.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<long>.Count)
        {
            var vMultiplier = new Vector<long>(10_000L);
            var vEpoch = new Vector<long>(UnixEpochTicks);
            int step = Vector<long>.Count;
            int step2 = step * 2;
            int limit2 = length - step2;

            while (i <= limit2)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var v1 = new Vector<long>(source.Slice(i + step));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.CopyTo(destination.Slice(i));
                r1.CopyTo(destination.Slice(i + step));
                i += step2;
            }

            int limit = length - step;
            while (i <= limit)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var r0 = (v0 * vMultiplier) + vEpoch;
                r0.CopyTo(destination.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<long>.Count)
        {
            var vMultiplier = new Vector<long>(10_000L);
            var vEpoch = new Vector<long>(UtcEpochConstant);
            int step = Vector<long>.Count;
            int step2 = step * 2;
            int limit2 = length - step2;

            while (i <= limit2)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var v1 = new Vector<long>(source.Slice(i + step));
                var r0 = (v0 * vMultiplier) + vEpoch;
                var r1 = (v1 * vMultiplier) + vEpoch;
                r0.CopyTo(rawDst.Slice(i));
                r1.CopyTo(rawDst.Slice(i + step));
                i += step2;
            }

            int limit = length - step;
            while (i <= limit)
            {
                var v0 = new Vector<long>(source.Slice(i));
                var r0 = (v0 * vMultiplier) + vEpoch;
                r0.CopyTo(rawDst.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<double>.Count)
        {
            var vFactor = new Vector<double>(factor);
            int step = Vector<double>.Count;
            int step2 = step * 2;
            int limit2 = length - step2;

            while (i <= limit2)
            {
                var v0 = new Vector<double>(source.Slice(i));
                var v1 = new Vector<double>(source.Slice(i + step));
                (v0 * vFactor).CopyTo(destination.Slice(i));
                (v1 * vFactor).CopyTo(destination.Slice(i + step));
                i += step2;
            }

            int limit = length - step;
            while (i <= limit)
            {
                var v0 = new Vector<double>(source.Slice(i));
                (v0 * vFactor).CopyTo(destination.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            var vFactor = new Vector<float>(factor);
            int step = Vector<float>.Count;
            int step2 = step * 2;
            int limit2 = length - step2;

            while (i <= limit2)
            {
                var v0 = new Vector<float>(source.Slice(i));
                var v1 = new Vector<float>(source.Slice(i + step));
                (v0 * vFactor).CopyTo(destination.Slice(i));
                (v1 * vFactor).CopyTo(destination.Slice(i + step));
                i += step2;
            }

            int limit = length - step;
            while (i <= limit)
            {
                var v0 = new Vector<float>(source.Slice(i));
                (v0 * vFactor).CopyTo(destination.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<int>.Count)
        {
            int step = Vector<int>.Count;
            int halfStep = Vector<long>.Count;
            int limit = length - step;

            while (i <= limit)
            {
                var v = new Vector<int>(source.Slice(i));
                Vector.Widen(v, out Vector<long> low, out Vector<long> high);
                low.CopyTo(destination.Slice(i));
                high.CopyTo(destination.Slice(i + halfStep));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<int>.Count)
        {
            int step = Vector<int>.Count;
            int halfStep = Vector<long>.Count;
            int limit = length - step;

            while (i <= limit)
            {
                var low = new Vector<long>(source.Slice(i));
                var high = new Vector<long>(source.Slice(i + halfStep));
                var narrowed = Vector.Narrow(low, high);
                narrowed.CopyTo(destination.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<short>.Count)
        {
            int step = Vector<short>.Count;
            int halfStep = Vector<int>.Count;
            int limit = length - step;

            while (i <= limit)
            {
                var v = new Vector<short>(source.Slice(i));
                Vector.Widen(v, out Vector<int> low, out Vector<int> high);
                low.CopyTo(destination.Slice(i));
                high.CopyTo(destination.Slice(i + halfStep));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<short>.Count)
        {
            int step = Vector<short>.Count;
            int halfStep = Vector<int>.Count;
            int limit = length - step;

            while (i <= limit)
            {
                var low = new Vector<int>(source.Slice(i));
                var high = new Vector<int>(source.Slice(i + halfStep));
                var narrowed = Vector.Narrow(low, high);
                narrowed.CopyTo(destination.Slice(i));
                i += step;
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
        if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            int step = Vector<byte>.Count;
            int intStep = Vector<int>.Count;
            int limit = length - step;

            while (i <= limit)
            {
                var v = new Vector<byte>(source.Slice(i));
                Vector.Widen(v, out Vector<ushort> lowUshort, out Vector<ushort> highUshort);
                Vector.Widen(lowUshort, out Vector<uint> u0, out Vector<uint> u1);
                Vector.Widen(highUshort, out Vector<uint> u2, out Vector<uint> u3);

                Vector.AsVectorInt32(u0).CopyTo(destination.Slice(i));
                Vector.AsVectorInt32(u1).CopyTo(destination.Slice(i + intStep));
                Vector.AsVectorInt32(u2).CopyTo(destination.Slice(i + (intStep * 2)));
                Vector.AsVectorInt32(u3).CopyTo(destination.Slice(i + (intStep * 3)));
                i += step;
            }
        }
#endif

        for (; i < length; i++)
        {
            destination[i] = source[i];
        }
    }
}
