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
/// The adapter applied to each element of a list member (docs/44 §A.4). Unlike a member-level
/// adapter there is no shadow: the list emitter calls the conversions inline, once per element,
/// so a collection is never copied to convert it. The element's own column model is the
/// surrogate's; <see cref="DomainTypeName"/> is what the reconstructed collection holds.
/// </summary>
internal sealed record ElementAdapterModel(
    string AdapterTypeName,
    string ToStorageMethod,
    string FromStorageMethod,
    string DomainTypeName,
    bool DomainIsValueType
)
{
    /// <summary>The write-side call converting <paramref name="value"/> to storage.</summary>
    public string ToStorage(string value) => $"{AdapterTypeName}.{ToStorageMethod}({value})";

    /// <summary>The read-side call converting <paramref name="storage"/> back to the domain.</summary>
    public string FromStorage(string storage) =>
        $"{AdapterTypeName}.{FromStorageMethod}({storage})";
}
