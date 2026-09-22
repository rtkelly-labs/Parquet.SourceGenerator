using Microsoft.CodeAnalysis;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Parser;

/// <summary>
/// Adapted collection elements (docs/44 §A.4): a list, array or other supported collection
/// member whose element type resolves to an adapter is planned as an ordinary list column of the
/// adapter's surrogate. The list emitter converts inline — <c>ToStorage</c> per element on write,
/// <c>FromStorage</c> per element on read — so no shadow member and no per-row collection copy is
/// needed, and the element may be any construction of a generic adapter's source.
/// </summary>
public static partial class TargetParser
{
    /// <returns>
    /// True when the member was handled — accepted or rejected with a diagnostic; false when it is
    /// not a collection or its element type needs no adapter.
    /// </returns>
    private static bool TryCollectAdaptedList(
        AdaptedMember member,
        ColumnOptions column,
        MemberScope scope,
        MemberSink sink
    )
    {
        if (
            !TypeAdapterResolver.TryGetCollectionElement(
                member.UnderlyingType,
                out ITypeSymbol declaredElement
            )
        )
        {
            return false;
        }

        ITypeSymbol element = TypeAdapterResolver.UnwrapNullable(declaredElement);
        if (element.TypeKind == TypeKind.Error)
            return false;

        bool builtIn =
            TryClassifyKind(element, declaredElement, out _) || TryClassifyCompound(element, out _);
        AdapterResolution resolution = TypeAdapterResolver.ResolveElement(
            member.Symbol,
            element,
            builtIn
        );
        if (resolution.Origin == AdapterOrigin.None)
            return false;

        if (resolution.Ambiguity is { } competing)
        {
            ReportAmbiguousAdapter(member, element, competing, scope);
            return Reject(sink);
        }

        AdapterDescriptor adapter = resolution.Adapter!;
        string? error =
            adapter.Error
            ?? (
                IsAssignable(member.Symbol)
                    ? null
                    : "the member has no setter the generated code can reach"
            );
        if (error is not null)
        {
            ReportInvalidAdapter(adapter, member, scope, error);
            return Reject(sink);
        }

        string? unsupported = ListScopeError(scope);
        if (unsupported is not null)
        {
            ReportUnsupportedSurrogate(adapter, member, scope, unsupported);
            return Reject(sink);
        }

        bool elementNullable = IsNullableColumn(declaredElement);
        PropertyModel? elementModel = PlanElementSurrogate(
            member,
            adapter,
            element,
            elementNullable,
            scope,
            out bool rejected
        );
        if (elementModel is null)
        {
            if (!rejected)
            {
                ReportUnsupportedSurrogate(
                    adapter,
                    member,
                    scope,
                    "list element surrogates must be a type the generator maps natively, or a struct or class whose public members it maps as single columns"
                );
            }
            return Reject(sink);
        }

        var list = new PropertyModel(
            member.Symbol.Name,
            column.Name,
            member.MemberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TimestampUnit: null,
            null,
            column.Order,
            DecimalPrecision: null,
            DecimalScale: null,
            PropertyKind.List,
            member.IsNullable,
            Deduplicate: false,
            ColumnEncoding.Default
        )
        {
            Element = elementModel,
        };

        ReportIneligibleSortKey(
            column.IsSortKey,
            list,
            member.Symbol,
            scope.ClassName,
            scope.FallbackLocation,
            scope.Diagnostics
        );

        sink.HasAdaptedMember = true;
        sink.Properties.Add(list);
        return true;
    }

    private static bool Reject(MemberSink sink)
    {
        sink.RejectedAnyMember = true;
        return true;
    }

    /// <summary>
    /// Why the current backend and position cannot carry a list at all, or null. Mirrors the
    /// rules an unadapted list member meets in <see cref="TryCollectCompoundMember"/>.
    /// </summary>
    private static string? ListScopeError(MemberScope scope)
    {
        if (!IsCompoundKindEmittable(PropertyKind.List, scope.CompoundKinds))
        {
            return "list columns need the compound-capable backend (Parquet.Net 6 at feature level 2 or later)";
        }

        bool pipelineScopedDial =
            (scope.CompoundKinds & (CompoundKinds.List | CompoundKinds.Map)) == CompoundKinds.List;
        return pipelineScopedDial && scope.CompoundDepth > 0
            ? "lists are supported as members of the serialized type itself, not inside nested types"
            : null;
    }

    /// <summary>
    /// The element model: the surrogate as a leaf, or as a group of single-column members,
    /// carrying the adapter the list emitter calls per element.
    /// </summary>
    private static PropertyModel? PlanElementSurrogate(
        AdaptedMember member,
        AdapterDescriptor adapter,
        ITypeSymbol element,
        bool elementNullable,
        MemberScope scope,
        out bool rejected
    )
    {
        rejected = false;
        ITypeSymbol surrogate = adapter.Surrogate!;
        string surrogateName = surrogate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string typeName =
            elementNullable && surrogate.IsValueType ? surrogateName + "?" : surrogateName;
        var call = new InlineAdapterModel(
            adapter.QualifiedName,
            adapter.ToStorageMethod,
            adapter.FromStorageMethod,
            element
                .WithNullableAnnotation(NullableAnnotation.None)
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            element.IsValueType,
            surrogateName
        );

        if (TryClassifyKind(surrogate, surrogate, out PropertyKind kind))
        {
            string? enumUnderlying =
                kind == PropertyKind.Enum && surrogate is INamedTypeSymbol enumSymbol
                    ? enumSymbol.EnumUnderlyingType?.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    )
                    : null;
            return new PropertyModel(
                "element",
                "element",
                typeName,
                TimestampUnit: null,
                enumUnderlying,
                -1,
                DecimalPrecision: null,
                DecimalScale: null,
                kind,
                elementNullable
            )
            {
                InlineAdapter = call,
            };
        }

        if (!TryClassifyCompound(surrogate, out PropertyKind compoundKind, allowUnattributed: true))
            return null;

        if (compoundKind != PropertyKind.Struct)
        {
            ReportUnsupportedSurrogate(
                adapter,
                member,
                scope,
                "collection surrogates are not supported as list elements"
            );
            rejected = true;
            return null;
        }

        PropertyModel? group = BuildCompoundModel(
            PropertyKind.Struct,
            surrogate,
            "element",
            surrogate,
            "element",
            -1,
            elementNullable,
            scope.ApiLevel,
            scope.ClassName,
            member.Location,
            scope.Diagnostics,
            scope.ContainmentPath,
            scope.CompoundDepth + 1,
            scope.CompoundKinds,
            out rejected,
            typeNameOverride: typeName,
            inSurrogate: true
        );
        if (group is null)
            return null;

        if (!ElementChildrenSupported(group))
        {
            ReportUnsupportedSurrogate(
                adapter,
                member,
                scope,
                "a list element's group surrogate may contain single columns and groups of single columns; deeper nesting inside list elements is not supported yet"
            );
            rejected = true;
            return null;
        }

        return group with
        {
            InlineAdapter = call,
        };
    }
}
