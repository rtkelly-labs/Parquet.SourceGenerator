using System;
using System.Diagnostics.CodeAnalysis;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// Classifies a property's Parquet field kind at parse time, driving fast switch-based code emission
/// rather than repeated TypeName string comparisons. Inspired by System.Text.Json's ConverterStrategy.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Enum members represent semantic property primitive types."
)]
public enum PropertyKind
{
    /// <summary>int, long, double, float, bool, string — direct DataField passthrough.</summary>
    Primitive,

    /// <summary>decimal → DecimalDataField with optional precision/scale attributes.</summary>
    Decimal,

    /// <summary>DateTime / DateTimeOffset → DateTimeDataField.</summary>
    DateTime,

    /// <summary>TimeSpan → TimeSpanDataField.</summary>
    TimeSpan,

    /// <summary>Guid → DataField&lt;string&gt; with .ToString() / Guid.Parse() interchange.</summary>
    Guid,

    /// <summary>enum types → DataField&lt;int&gt; (underlying type) with cast interchange.</summary>
    Enum,

    /// <summary>byte[] → DataField&lt;byte[]&gt; passthrough.</summary>
    ByteArray,

    /// <summary>TimeOnly → TimeDataField (Micros).</summary>
    TimeOnly,

    /// <summary>DateOnly → DateTimeDataField with midnight conversion.</summary>
    DateOnly,

    /// <summary>Nested <c>[ParquetSerializable]</c> member → StructField group holding <see cref="PropertyModel.Children"/>.</summary>
    Struct,

    /// <summary><c>List&lt;T&gt;</c> / array member → ListField (3-level) holding <see cref="PropertyModel.Element"/>.</summary>
    List,

    /// <summary><c>Dictionary&lt;string, T&gt;</c> member → MapField; key is a required string leaf, value is <see cref="PropertyModel.MapValue"/>.</summary>
    Map,
}

/// <summary>
/// Specifies the physical column encoding hint for code generation.
/// </summary>
public enum ColumnEncoding
{
    /// <summary>Default encoding, chosen automatically based on data type.</summary>
    Default = 0,

    /// <summary>Dictionary encoding (PLAIN_DICTIONARY / RLE_DICTIONARY).</summary>
    Dictionary = 1,

    /// <summary>Delta binary packed encoding (DELTA_BINARY_PACKED).</summary>
    DeltaBinaryPacked = 2,

    /// <summary>Byte stream split encoding (BYTE_STREAM_SPLIT).</summary>
    ByteSplitStream = 3,
}

/// <summary>
/// Value-equatable model representing a single property or field binding.
/// Optimized memory layout: 8-byte reference pointers first, followed by 4-byte primitives, booleans at tail.
/// <para>
/// Compound kinds (<see cref="PropertyKind.Struct"/>, <see cref="PropertyKind.List"/>,
/// <see cref="PropertyKind.Map"/>) carry their subtree in <see cref="Children"/>,
/// <see cref="Element"/> and <see cref="MapValue"/>. Those members are themselves
/// <see cref="EquatableArray{T}"/>-wrapped or value-equal records, so a whole model tree
/// compares by value — which is what the incremental pipeline caches on. Never introduce
/// <c>List&lt;T&gt;</c> or a raw array for a nested member here.
/// </para>
/// </summary>
public sealed record PropertyModel(
    string Name,
    string ParquetColumnName,
    string TypeName,
    string? TimestampUnit,
    string? EnumUnderlyingTypeName,
    int Order,
    int? DecimalPrecision,
    int? DecimalScale,
    PropertyKind Kind,
    bool IsNullable,
    bool Deduplicate = false,
    ColumnEncoding Encoding = ColumnEncoding.Default
) : IEquatable<PropertyModel>
{
    /// <summary>
    /// Struct members: the child property models, in schema order. Value-equal via
    /// <see cref="EquatableArray{T}"/> — a <c>List</c> here would break model equality and
    /// with it the incremental pipeline's caching.
    /// </summary>
    public EquatableArray<PropertyModel> Children { get; init; }

    /// <summary>
    /// List members: the element subtree. May itself be a compound model (a list of lists,
    /// a list of structs); null for every other kind.
    /// </summary>
    public PropertyModel? Element { get; init; }

    /// <summary>
    /// Map members (<c>Dictionary&lt;string, T&gt;</c>): the value subtree; <see cref="Children"/>
    /// carries the single required string key. Null for every other kind.
    /// </summary>
    public PropertyModel? MapValue { get; init; }

    /// <summary>
    /// True when the member carries <c>[ParquetSortKey]</c>, opting it in to the emitted
    /// sorted row-group pruning lookups (issue #151). Left off the positional parameter list
    /// deliberately: it is an opt-in flag a handful of members set, and every existing
    /// construction site — including the golden-file models — must keep meaning "not a sort key".
    /// </summary>
    public bool IsSortKey { get; init; }

    /// <summary>
    /// Struct members: whether the nested C# type is a value type. Every Parquet group is
    /// optional (docs/15 §2.4) so the definition-level ladder counts the rung either way, but
    /// the write extraction emits the ancestor null test only for reference types — a struct
    /// member can never be null at runtime.
    /// </summary>
    public bool CompoundIsValueType { get; init; }

    /// <summary>
    /// Backwards-compatible constructor overload without deduplication or encoding flag.
    /// </summary>
    public PropertyModel(
        string Name,
        string ParquetColumnName,
        string TypeName,
        string? TimestampUnit,
        string? EnumUnderlyingTypeName,
        int Order,
        int? DecimalPrecision,
        int? DecimalScale,
        PropertyKind Kind,
        bool IsNullable
    )
        : this(
            Name,
            ParquetColumnName,
            TypeName,
            TimestampUnit,
            EnumUnderlyingTypeName,
            Order,
            DecimalPrecision,
            DecimalScale,
            Kind,
            IsNullable,
            Deduplicate: false,
            ColumnEncoding.Default
        )
    {
        // Backwards-compatible overload
    }

    /// <summary>
    /// Backwards-compatible constructor overload without encoding flag.
    /// </summary>
    public PropertyModel(
        string Name,
        string ParquetColumnName,
        string TypeName,
        string? TimestampUnit,
        string? EnumUnderlyingTypeName,
        int Order,
        int? DecimalPrecision,
        int? DecimalScale,
        PropertyKind Kind,
        bool IsNullable,
        bool Deduplicate
    )
        : this(
            Name,
            ParquetColumnName,
            TypeName,
            TimestampUnit,
            EnumUnderlyingTypeName,
            Order,
            DecimalPrecision,
            DecimalScale,
            Kind,
            IsNullable,
            Deduplicate,
            ColumnEncoding.Default
        )
    {
        // Backwards-compatible overload
    }
}
