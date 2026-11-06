using System;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// Value-equatable model representing a class, record, or struct marked for Parquet code generation.
/// </summary>
/// <remarks>
/// Kept to exactly what the emitter reads. <c>SchemaName</c>, <c>IsRecord</c> and <c>IsValueType</c>
/// were all carried here and never used — dead state that still participated in the incremental
/// pipeline's equality comparisons, so a change to any of them would have invalidated the cache and
/// re-run generation for no output difference.
/// </remarks>
public sealed record TargetClassModel(
    string Namespace,
    string ClassName,
    EquatableArray<PropertyModel> Properties,
    bool IsValueType = false,
    bool IsUnmanaged = false,
    bool HasSingleInstanceField = false
) : IEquatable<TargetClassModel>
{
    /// <summary>
    /// Backwards-compatible constructor overload without single instance field metadata.
    /// </summary>
    public TargetClassModel(
        string Namespace,
        string ClassName,
        EquatableArray<PropertyModel> Properties,
        bool IsValueType,
        bool IsUnmanaged
    )
        : this(Namespace, ClassName, Properties, IsValueType, IsUnmanaged, false)
    {
        // Backwards-compatible overload
    }

    /// <summary>
    /// Backwards-compatible constructor overload without value type / unmanaged metadata.
    /// </summary>
    public TargetClassModel(
        string Namespace,
        string ClassName,
        EquatableArray<PropertyModel> Properties
    )
        : this(Namespace, ClassName, Properties, false, false, false)
    {
        // Backwards-compatible overload
    }
}
