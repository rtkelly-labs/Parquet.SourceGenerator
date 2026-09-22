using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parquet.SourceGenerator.Diagnostics;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Parser;

/// <summary>
/// Type-adapter planning (docs/44-TYPE-ADAPTERS.md): a member the generator cannot map itself, or
/// one that names an adapter explicitly, is modelled as its adapter's surrogate under a
/// storage-shadow name. The shadow is an internal property the generator adds to the partial
/// type, so every emitter reads and writes the surrogate through an ordinary member access and
/// none of them needs to know adapters exist.
/// </summary>
public static partial class TargetParser
{
    /// <summary>Suffix of the generated storage member for an adapted member.</summary>
    private const string ShadowSuffix = "ParquetStorage";

    /// <summary>The leaf annotations read from a member, carried to its surrogate leaf.</summary>
    private sealed record LeafOptions(int? Precision, int? Scale, string? TimestampUnit);

    /// <summary>The member being planned, with the type facts the parser already derived.</summary>
    private sealed record AdaptedMember(
        ISymbol Symbol,
        ITypeSymbol MemberType,
        ITypeSymbol UnderlyingType,
        bool IsNullable,
        Location Location
    );

    /// <summary>
    /// Resolves and, when an adapter applies, collects the member through it.
    /// </summary>
    /// <returns>
    /// True when the member was handled here — accepted or rejected with a diagnostic; false when
    /// no adapter applies and the ordinary classification path should run.
    /// </returns>
    private static bool TryCollectAdaptedMember(
        AdaptedMember member,
        ColumnOptions column,
        LeafOptions leaf,
        MemberScope scope,
        MemberSink sink
    )
    {
        bool builtIn =
            TryClassifyKind(member.UnderlyingType, member.MemberType, out _)
            || TryClassifyCompound(member.UnderlyingType, out _);

        AdapterResolution resolution = TypeAdapterResolver.Resolve(
            member.Symbol,
            member.UnderlyingType,
            builtIn
        );

        if (resolution.IgnoredBuiltInOverride is { } ignored)
        {
            scope.Diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.BuiltInAdapterRegistrationIgnored,
                    member.Location,
                    [
                        member.Symbol.Name,
                        scope.ClassName,
                        member.UnderlyingType.ToDisplayString(),
                        ignored.DisplayName,
                    ]
                )
            );
        }

        // No adapter for the member's own type: a collection member may still adapt its elements.
        if (resolution.Origin == AdapterOrigin.None)
            return TryCollectAdaptedList(member, column, scope, sink);

        if (resolution.Ambiguity is { } competing)
        {
            ReportAmbiguousAdapter(member, member.UnderlyingType, competing, scope);
            sink.RejectedAnyMember = true;
            return true;
        }

        AdapterDescriptor adapter = resolution.Adapter!;
        string? error = adapter.Error ?? CheckShadowable(member, scope);
        if (error is not null)
        {
            ReportInvalidAdapter(adapter, member, scope, error);
            sink.RejectedAnyMember = true;
            return true;
        }

        CollectThroughAdapter(member, adapter, column, leaf, scope, sink);
        return true;
    }

    /// <summary>
    /// Plans the surrogate as the member's column model and records the shadow that serves it.
    /// </summary>
    private static void CollectThroughAdapter(
        AdaptedMember member,
        AdapterDescriptor adapter,
        ColumnOptions column,
        LeafOptions leaf,
        MemberScope scope,
        MemberSink sink
    )
    {
        ITypeSymbol surrogate = adapter.Surrogate!;
        string shadowName = member.Symbol.Name + ShadowSuffix;
        string surrogateName = surrogate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Column-model spelling follows the parser's existing convention: `T?` only for a
        // nullable value type; reference types carry their optionality in IsNullable.
        string modelTypeName =
            member.IsNullable && surrogate.IsValueType ? surrogateName + "?" : surrogateName;

        PropertyModel? model = PlanSurrogate(
            member,
            adapter,
            shadowName,
            modelTypeName,
            column,
            leaf,
            scope,
            out bool rejected
        );
        if (model is null)
        {
            sink.RejectedAnyMember = true;
            if (!rejected)
            {
                ReportUnsupportedSurrogate(
                    adapter,
                    member,
                    scope,
                    "adapter surrogates must be a type the generator maps natively, or a struct or class whose public members it maps"
                );
            }
            return;
        }

        ReportIneligibleSortKey(
            column.IsSortKey,
            model,
            member.Symbol,
            scope.ClassName,
            scope.FallbackLocation,
            scope.Diagnostics
        );

        sink.HasAdaptedMember = true;
        sink.Properties.Add(model);

        // An inherited member whose declaring base is itself a [ParquetSerializable] target gets
        // its shadow from that base's own generated partial; declaring it again here would hide it.
        if (!ShadowInheritedFromTarget(member.Symbol, scope.DeclaringType))
        {
            sink.Shadows.Add(
                new AdapterShadowModel(
                    MemberName: member.Symbol.Name,
                    ShadowName: shadowName,
                    ShadowTypeName: member.IsNullable ? surrogateName + "?" : surrogateName,
                    AdapterTypeName: adapter.QualifiedName,
                    ToStorageMethod: adapter.ToStorageMethod,
                    FromStorageMethod: adapter.FromStorageMethod,
                    IsNullable: member.IsNullable,
                    DomainIsValueType: member.UnderlyingType.IsValueType,
                    SurrogateIsValueType: surrogate.IsValueType,
                    InitOnly: member.Symbol is IPropertySymbol { SetMethod.IsInitOnly: true }
                )
            );
        }
    }

    /// <summary>
    /// Classifies the surrogate: a leaf the generator maps natively, or a structural group whose
    /// members are planned recursively without needing <c>[ParquetSerializable]</c>.
    /// </summary>
    private static PropertyModel? PlanSurrogate(
        AdaptedMember member,
        AdapterDescriptor adapter,
        string shadowName,
        string modelTypeName,
        ColumnOptions column,
        LeafOptions leaf,
        MemberScope scope,
        out bool rejected
    )
    {
        rejected = false;
        ITypeSymbol surrogate = adapter.Surrogate!;

        if (TryClassifyKind(surrogate, surrogate, out PropertyKind kind))
        {
            if (scope.ApiLevel == ParquetApiLevel.V4 && !IsSupportedOnClassicApi(surrogate))
            {
                ReportUnsupportedSurrogate(
                    adapter,
                    member,
                    scope,
                    "the type has no representation in the Parquet.Net 4.x/5.x API"
                );
                rejected = true;
                return null;
            }

            PropertyModel leafModel = BuildLeafModel(
                member.Symbol,
                surrogate,
                surrogate,
                kind,
                column,
                member.IsNullable,
                leaf.Precision,
                leaf.Scale,
                leaf.TimestampUnit
            );
            return leafModel with { Name = shadowName, TypeName = modelTypeName };
        }

        if (!TryClassifyCompound(surrogate, out PropertyKind compoundKind, allowUnattributed: true))
            return null;

        if (
            compoundKind != PropertyKind.Struct
            || !IsCompoundKindEmittable(PropertyKind.Struct, scope.CompoundKinds)
        )
        {
            ReportUnsupportedSurrogate(
                adapter,
                member,
                scope,
                compoundKind == PropertyKind.Struct
                    ? "group surrogates need the compound-capable backend (Parquet.Net 6 at feature level 2 or later)"
                    : "collection surrogates are not supported"
            );
            rejected = true;
            return null;
        }

        return BuildCompoundModel(
            PropertyKind.Struct,
            surrogate,
            shadowName,
            surrogate,
            column.Name,
            column.Order,
            member.IsNullable,
            scope.ApiLevel,
            scope.ClassName,
            member.Location,
            scope.Diagnostics,
            scope.ContainmentPath,
            scope.CompoundDepth,
            scope.CompoundKinds,
            out rejected,
            typeNameOverride: modelTypeName,
            inSurrogate: true
        );
    }

    /// <summary>
    /// The member shapes the storage shadow cannot serve, checked before any planning so the
    /// failure is a diagnostic at the member rather than broken generated C#.
    /// </summary>
    private static string? CheckShadowable(AdaptedMember member, MemberScope scope)
    {
        // Rule PARQ007 applies to adapted members exactly as to any other: the shadow's setter
        // assigns the member.
        if (!IsAssignable(member.Symbol))
            return "the member has no setter the generated code can reach";

        if (IsRequiredMember(member.Symbol))
        {
            return "required members cannot be adapted, because the generated reader sets the storage member rather than the required one";
        }

        INamedTypeSymbol? declaring = scope.DeclaringType;
        if (declaring is null)
            return null;

        string shadowName = member.Symbol.Name + ShadowSuffix;
        for (INamedTypeSymbol? t = declaring; t is not null; t = t.BaseType)
        {
            if (!t.GetMembers(shadowName).IsEmpty)
            {
                return "the type already declares a member named '"
                    + shadowName
                    + "', which the generator needs for the storage representation";
            }
        }

        for (INamedTypeSymbol? t = declaring; t is not null; t = t.ContainingType)
        {
            if (!IsDeclaredPartial(t))
            {
                return "the type '"
                    + t.Name
                    + "' must be declared partial so the generator can add the storage member";
            }
        }

        return null;
    }

    private static bool ShadowInheritedFromTarget(ISymbol member, INamedTypeSymbol? declaring)
    {
        INamedTypeSymbol? owner = member.ContainingType;
        return declaring is not null
            && owner is not null
            && !SymbolEqualityComparer.Default.Equals(owner, declaring)
            && owner
                .GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == AttributeFullName);
    }

    /// <summary>
    /// Whether the member is <c>required</c>. Read from syntax: the generator binds against
    /// Roslyn 4.0, which predates <c>IPropertySymbol.IsRequired</c>.
    /// </summary>
    private static bool IsRequiredMember(ISymbol member)
    {
        foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
        {
            SyntaxNode node = reference.GetSyntax();
            SyntaxTokenList modifiers = node switch
            {
                PropertyDeclarationSyntax property => property.Modifiers,
                VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field } =>
                    field.Modifiers,
                _ => default,
            };

            foreach (SyntaxToken modifier in modifiers)
            {
                if (modifier.ValueText == "required")
                    return true;
            }
        }

        return false;
    }

    private static bool IsDeclaredPartial(INamedTypeSymbol type)
    {
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (
                reference.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The value-equatable shadow set for the root type: its namespace, the partial declaration
    /// chain that reopens it, and the members.
    /// </summary>
    private static AdapterShadowSet BuildShadowSet(
        INamedTypeSymbol type,
        string namespaceName,
        List<AdapterShadowModel> shadows
    )
    {
        if (shadows.Count == 0)
            return AdapterShadowSet.None;

        var chain = new List<string>();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            chain.Add("partial " + DeclarationKeyword(t) + " " + t.Name);
        chain.Reverse();

        return new AdapterShadowSet(
            namespaceName,
            new EquatableArray<string>(chain.ToArray()),
            new EquatableArray<AdapterShadowModel>(shadows.ToArray())
        );
    }

    private static string DeclarationKeyword(INamedTypeSymbol type)
    {
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is RecordDeclarationSyntax record)
            {
                return record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                    ? "record struct"
                    : "record";
            }
        }

        return type.TypeKind == TypeKind.Struct ? "struct" : "class";
    }

    private static void ReportAmbiguousAdapter(
        AdaptedMember member,
        ITypeSymbol type,
        string competing,
        MemberScope scope
    ) =>
        scope.Diagnostics.Add(
            new DiagnosticInfo(
                DiagnosticDescriptors.AmbiguousTypeAdapter,
                member.Location,
                [member.Symbol.Name, scope.ClassName, type.ToDisplayString(), competing]
            )
        );

    private static void ReportInvalidAdapter(
        AdapterDescriptor adapter,
        AdaptedMember member,
        MemberScope scope,
        string reason
    ) =>
        scope.Diagnostics.Add(
            new DiagnosticInfo(
                DiagnosticDescriptors.InvalidTypeAdapter,
                member.Location,
                [adapter.DisplayName, member.Symbol.Name, scope.ClassName, reason]
            )
        );

    private static void ReportUnsupportedSurrogate(
        AdapterDescriptor adapter,
        AdaptedMember member,
        MemberScope scope,
        string reason
    ) =>
        scope.Diagnostics.Add(
            new DiagnosticInfo(
                DiagnosticDescriptors.UnsupportedAdapterSurrogate,
                member.Location,
                [
                    adapter.Surrogate?.ToDisplayString() ?? "?",
                    adapter.DisplayName,
                    member.Symbol.Name,
                    scope.ClassName,
                    reason,
                ]
            )
        );
}
