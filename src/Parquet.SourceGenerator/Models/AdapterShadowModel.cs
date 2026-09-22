using System;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// One adapted member of a target type: the storage-typed property the generator adds to the
/// partial declaration so every emitter reads and writes the surrogate through an ordinary member.
/// </summary>
/// <remarks>
/// The column model for an adapted member is the surrogate's model, named after
/// <see cref="ShadowName"/>. Nothing else in the emitter knows adapters exist: the shadow's
/// accessors are the only place the conversion calls appear, and they are direct static calls.
/// Value-equatable strings only — no Roslyn symbol survives parsing (docs/44 §17).
/// </remarks>
internal sealed record AdapterShadowModel(
    string MemberName,
    string ShadowName,
    string ShadowTypeName,
    string AdapterTypeName,
    string ToStorageMethod,
    string FromStorageMethod,
    bool IsNullable,
    bool DomainIsValueType,
    bool SurrogateIsValueType,
    bool InitOnly
);

/// <summary>
/// The shadow members one target type needs, and the partial declaration chain that reopens it.
/// Empty for every type without an adapted member, so existing models compare exactly as before.
/// </summary>
/// <param name="Namespace">The target's namespace, empty for the global namespace.</param>
/// <param name="DeclarationChain">
/// Outermost-first partial declarations ("partial class Outer", "partial record struct Row").
/// </param>
/// <param name="Members">The adapted members, in declaration order.</param>
internal sealed record AdapterShadowSet(
    string Namespace,
    EquatableArray<string> DeclarationChain,
    EquatableArray<AdapterShadowModel> Members
)
{
    public static AdapterShadowSet None { get; } =
        new(string.Empty, EquatableArray<string>.Empty, EquatableArray<AdapterShadowModel>.Empty);

    public bool IsEmpty => Members.Length == 0;
}

/// <summary>
/// An adapter the emitters call inline (docs/44 §A.4b, §A.4c): on a list element, or on a member
/// nested anywhere below the root — inside a <c>[ParquetSerializable]</c> child, a structural
/// surrogate or a list element. There is no shadow: the value is converted where it is read off
/// its parent on write, and where it is assigned to its parent on read, once per value. The
/// model carrying this is the surrogate's; <see cref="DomainTypeName"/> is what the member holds.
/// </summary>
internal sealed record InlineAdapterModel(
    string AdapterTypeName,
    string ToStorageMethod,
    string FromStorageMethod,
    string DomainTypeName,
    bool DomainIsValueType,
    string StorageTypeName
)
{
    /// <summary>The write-side call converting <paramref name="value"/> to storage.</summary>
    public string ToStorage(string value) => $"{AdapterTypeName}.{ToStorageMethod}({value})";

    /// <summary>The read-side call converting <paramref name="storage"/> back to the domain.</summary>
    public string FromStorage(string storage) =>
        $"{AdapterTypeName}.{FromStorageMethod}({storage})";

    /// <summary>
    /// Reads <paramref name="access"/> and converts it to storage. A nullable member converts
    /// only when present, yielding a nullable storage value the definition ladder then tests.
    /// </summary>
    public string ToStorageFrom(string access, bool nullable, string patternVariable) =>
        nullable
            ? $"{access} is {{ }} {patternVariable} ? ({StorageTypeName}?){ToStorage(patternVariable)} : null"
            : ToStorage(access);
}
