using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// Immutable, value-equatable array wrapper designed for Roslyn incremental generator caching.
/// </summary>
/// <typeparam name="T">Element type, must implement <see cref="IEquatable{T}"/>.</typeparam>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array;

    /// <summary>
    /// Initializes a new instance of <see cref="EquatableArray{T}"/>.
    /// </summary>
    /// <param name="array">Underlying array elements.</param>
    public EquatableArray(T[] array)
    {
        _array = array;
    }

    /// <summary>
    /// Gets an empty <see cref="EquatableArray{T}"/>.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "Standard pattern for generic equatable arrays in Roslyn generators"
    )]
    public static EquatableArray<T> Empty { get; } = new(Array.Empty<T>());

    /// <summary>
    /// Gets the number of elements in the array.
    /// </summary>
    public int Length => _array?.Length ?? 0;

    /// <summary>
    /// Gets the element at the specified index.
    /// </summary>
    /// <param name="index">Zero-based index.</param>
    public T this[int index] => (_array ?? Array.Empty<T>())[index];

    /// <summary>
    /// Determines whether the specified <see cref="EquatableArray{T}"/> is equal to the current instance.
    /// </summary>
    public bool Equals(EquatableArray<T> other)
    {
        return AsSpan().SequenceEqual(other.AsSpan());
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj)
    {
        return obj is EquatableArray<T> other && Equals(other);
    }

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    public override int GetHashCode()
    {
        if (_array is null)
            return 0;
        int hashCode = 17;
        foreach (T item in _array)
        {
            hashCode = unchecked((hashCode * 31) + item.GetHashCode());
        }
        return hashCode;
    }

    /// <summary>
    /// Returns a read-only span over the array elements.
    /// </summary>
    public ReadOnlySpan<T> AsSpan() =>
        _array is null ? ReadOnlySpan<T>.Empty : new ReadOnlySpan<T>(_array);

    /// <summary>
    /// Returns an enumerator that iterates through the array.
    /// </summary>
    public IEnumerator<T> GetEnumerator()
    {
        return ((IEnumerable<T>)(_array ?? Array.Empty<T>())).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Equality operator for <see cref="EquatableArray{T}"/>.
    /// </summary>
    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) =>
        left.Equals(right);

    /// <summary>
    /// Inequality operator for <see cref="EquatableArray{T}"/>.
    /// </summary>
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) =>
        !left.Equals(right);
}
