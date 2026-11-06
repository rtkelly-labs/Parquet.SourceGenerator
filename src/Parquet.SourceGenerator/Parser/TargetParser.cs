using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parquet.SourceGenerator.Diagnostics;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Parser;

/// <summary>
/// Result container holding parsed target model and pipeline diagnostics.
/// </summary>
public sealed record TargetParserResult(
    TargetClassModel? Model,
    EquatableArray<DiagnosticInfo> Diagnostics
);

/// <summary>
/// Extracts semantic models from Roslyn syntax contexts for decorated target types and validates compiler rules.
/// </summary>
public static class TargetParser
{
    private const string AttributeFullName = "Parquet.SourceGenerator.ParquetSerializableAttribute";
    private const string ColumnAttributeFullName = "Parquet.SourceGenerator.ParquetColumnAttribute";
    private const string IgnoreAttributeFullName = "Parquet.SourceGenerator.ParquetIgnoreAttribute";
    private const string DecimalAttributeFullName =
        "Parquet.SourceGenerator.ParquetDecimalAttribute";
    private const string TimestampAttributeFullName =
        "Parquet.SourceGenerator.ParquetTimestampAttribute";
    private const string SortKeyAttributeFullName =
        "Parquet.SourceGenerator.ParquetSortKeyAttribute";

    /// <summary>
    /// Parses a Roslyn syntax context and returns a value-equatable <see cref="TargetParserResult"/> containing the target model and diagnostics.
    /// </summary>
    public static TargetParserResult GetTargetModel(GeneratorSyntaxContext context) =>
        GetTargetModel(context, ParquetApiLevel.V6);

    /// <summary>
    /// Parses a Roslyn syntax context for a specific Parquet.Net API generation.
    /// </summary>
    /// <param name="context">The syntax context to parse.</param>
    /// <param name="apiLevel">
    /// Which backend will consume the model. This narrows the accepted member types — see
    /// <see cref="ParquetApiLevel"/>.
    /// </param>
    public static TargetParserResult GetTargetModel(
        GeneratorSyntaxContext context,
        ParquetApiLevel apiLevel
    ) => GetTargetModel(context, apiLevel, compoundKinds: CompoundKinds.None);

    /// <summary>
    /// Parses a Roslyn syntax context with a specific set of emittable compound kinds. This is
    /// the overload the generators call; the dial says how far the consuming emitter has got
    /// (see <see cref="CompoundKinds"/>).
    /// </summary>
    public static TargetParserResult GetTargetModel(
        GeneratorSyntaxContext context,
        ParquetApiLevel apiLevel,
        CompoundKinds compoundKinds
    )
    {
        SyntaxNode node = context.Node;
        if (node is not TypeDeclarationSyntax typeDeclaration)
            return new TargetParserResult(Model: null, EquatableArray<DiagnosticInfo>.Empty);

        ISymbol? symbol = context.SemanticModel.GetDeclaredSymbol(typeDeclaration);
        if (symbol is not INamedTypeSymbol typeSymbol)
            return new TargetParserResult(Model: null, EquatableArray<DiagnosticInfo>.Empty);

        return GetTargetModelCore(typeSymbol, typeDeclaration, apiLevel, compoundKinds);
    }

    /// <summary>
    /// Parses a Roslyn syntax context, optionally permitting compound members.
    /// </summary>
    /// <param name="context">The syntax context to parse.</param>
    /// <param name="apiLevel">Which backend will consume the model.</param>
    /// <param name="allowCompoundTypes">
    /// When true, struct/list/map members are parsed into recursive <see cref="PropertyModel"/>
    /// trees. The shipping pipeline passes false while the compound emitters land (issue #176);
    /// the flag flips to true with the milestone that first consumes a compound model.
    /// </param>
    public static TargetParserResult GetTargetModel(
        GeneratorSyntaxContext context,
        ParquetApiLevel apiLevel,
        bool allowCompoundTypes
    ) =>
        GetTargetModel(
            context,
            apiLevel,
            allowCompoundTypes
                ? CompoundKinds.Struct | CompoundKinds.List | CompoundKinds.Map
                : CompoundKinds.None
        );

    /// <summary>
    /// Parses a symbol directly, resolving its declaring syntax when one is available.
    /// </summary>
    /// <remarks>
    /// Test seam for the compound pipeline (#176): the syntax-context overload is the one wired
    /// into the incremental pipeline, but recursion through it needs a driver run per shape. The
    /// tests build a compilation once and call this directly with <c>allowCompoundTypes</c> set.
    /// </remarks>
    public static TargetParserResult GetTargetModel(
        INamedTypeSymbol typeSymbol,
        ParquetApiLevel apiLevel,
        bool allowCompoundTypes
    ) =>
        GetTargetModel(
            typeSymbol,
            apiLevel,
            allowCompoundTypes
                ? CompoundKinds.Struct | CompoundKinds.List | CompoundKinds.Map
                : CompoundKinds.None
        );

    /// <summary>
    /// Parses a symbol directly with an explicit set of emittable compound kinds.
    /// </summary>
    public static TargetParserResult GetTargetModel(
        INamedTypeSymbol typeSymbol,
        ParquetApiLevel apiLevel,
        CompoundKinds compoundKinds
    )
    {
        TypeDeclarationSyntax? syntax = null;
        foreach (SyntaxReference reference in typeSymbol.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax typeDeclaration)
            {
                syntax = typeDeclaration;
                break;
            }
        }

        return GetTargetModelCore(typeSymbol, syntax, apiLevel, compoundKinds);
    }

    private static TargetParserResult GetTargetModelCore(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax? typeDeclaration,
        ParquetApiLevel apiLevel,
        CompoundKinds compoundKinds
    )
    {
        AttributeData? serializableAttr = typeSymbol
            .GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttributeFullName);

        if (serializableAttr is null)
            return new TargetParserResult(Model: null, EquatableArray<DiagnosticInfo>.Empty);

        var diagnostics = new List<DiagnosticInfo>();

        (bool isPartial, bool nestedUnreachable, bool isGeneric) = ValidateTargetDeclaration(
            typeSymbol,
            typeDeclaration,
            diagnostics
        );

        string namespaceName = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : typeSymbol.ContainingNamespace.ToDisplayString();

        // Dotted for nested targets ("Outer.Inner") so every type reference the emitter writes
        // resolves at namespace scope; identical to Name for the top-level case, which keeps
        // existing emitted output byte-for-byte unchanged.
        string className = GetNestedQualifiedTypeName(typeSymbol);

        Location fallbackLocation =
            typeDeclaration?.Identifier.GetLocation()
            ?? typeSymbol.Locations.FirstOrDefault()
            ?? Location.None;

        var scope = new MemberScope(
            ClassName: className,
            FallbackLocation: fallbackLocation,
            ApiLevel: apiLevel,
            CompoundKinds: compoundKinds,
            Diagnostics: diagnostics,
            ContainmentPath: new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            {
                typeSymbol,
            },
            CompoundDepth: 0
        );
        var sink = new MemberSink();

        CollectMembers(typeSymbol, scope, sink);

        // Rule PARQ003: Warning if no public serializable properties found. Suppressed when a
        // member was rejected above — "this type has no serializable members" is misleading when the
        // real answer is "its members were rejected", and PARQ006/PARQ007 already say why.
        bool rejectedAnyMember = sink.RejectedAnyMember;
        if (sink.Properties.Count == 0 && !rejectedAnyMember)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.NoPropertiesFound,
                    fallbackLocation,
                    new[] { className }
                )
            );
        }

        // Rule PARQ008: the read path constructs through an object initializer, which needs an
        // accessible parameterless constructor. Value types always have one; a positional record or
        // a class whose only constructors take arguments does not, and previously failed with CS7036
        // reported against generated source.
        bool hasParameterlessConstructor = HasParameterlessConstructor(typeSymbol);
        if (!hasParameterlessConstructor)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.NoParameterlessConstructor,
                    fallbackLocation,
                    new[] { className }
                )
            );
        }

        List<PropertyModel> orderedProperties = sink
            .Properties.OrderBy(p => p.Order >= 0 ? p.Order : int.MaxValue)
            .ToList();

        // Emission is suppressed whenever a fatal diagnostic already explains the problem. Emitting
        // anyway buries that message under cascading errors from the generated file.
        bool canEmit =
            isPartial
            && hasParameterlessConstructor
            && !rejectedAnyMember
            && !nestedUnreachable
            && !isGeneric;

        bool hasSingleInstanceField = ComputeHasSingleInstanceField(typeSymbol);

        TargetClassModel? model = canEmit
            ? new TargetClassModel(
                Namespace: namespaceName,
                ClassName: className,
                Properties: new EquatableArray<PropertyModel>(orderedProperties.ToArray()),
                IsValueType: typeSymbol.IsValueType,
                IsUnmanaged: typeSymbol.IsUnmanagedType,
                HasSingleInstanceField: hasSingleInstanceField
            )
            : null;

        return new TargetParserResult(
            model,
            new EquatableArray<DiagnosticInfo>(diagnostics.ToArray())
        );
    }

    /// <summary>
    /// Declaration-level rules PARQ001 (must be partial), PARQ009 (a nested target must be
    /// reachable from the generated extension class) and PARQ010 (no open generic types),
    /// reporting to the shared diagnostics list and returning the flags that gate emission.
    /// </summary>
    private static (
        bool IsPartial,
        bool NestedUnreachable,
        bool IsGeneric
    ) ValidateTargetDeclaration(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax? typeDeclaration,
        List<DiagnosticInfo> diagnostics
    )
    {
        // Rule PARQ001: Target type must be declared as partial
        bool isPartial =
            typeDeclaration is null
            || typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        if (!isPartial && typeDeclaration is not null)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.MustBePartial,
                    typeDeclaration.Identifier.GetLocation(),
                    new[] { typeSymbol.Name }
                )
            );
        }

        // Rule PARQ009 (reversed by #176): nested [ParquetSerializable] declarations are legal
        // targets — the emitted extension class names the target through its containing-type
        // path ("Outer.Inner") and the identifier flattens it ("OuterInnerParquetExtensions").
        // What stays rejected is a declaration the namespace-scope extension class cannot name:
        // a private nested type.
        bool isNested = typeSymbol.ContainingType is not null;
        bool nestedUnreachable =
            isNested && !IsReachableFromGeneratedCode(typeSymbol.DeclaredAccessibility);
        if (nestedUnreachable)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.NestedTypeNotSupported,
                    typeDeclaration?.Identifier.GetLocation() ?? Location.None,
                    new[]
                    {
                        typeSymbol.Name,
                        GetNestedQualifiedTypeName(typeSymbol.ContainingType!),
                        typeSymbol.DeclaredAccessibility.ToString(),
                    }
                )
            );
        }

        bool isGeneric = typeSymbol.TypeParameters.Length > 0;
        if (isGeneric)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.GenericTypeNotSupported,
                    typeDeclaration?.Identifier.GetLocation() ?? Location.None,
                    new[] { typeSymbol.Name }
                )
            );
        }

        return (isPartial, nestedUnreachable, isGeneric);
    }

    /// <summary>
    /// Whether the read path can construct this type: value types always have an accessible
    /// parameterless constructor; reference types need one declared (or defaulted).
    /// </summary>
    private static bool HasParameterlessConstructor(INamedTypeSymbol typeSymbol) =>
        typeSymbol.IsValueType
        || typeSymbol.InstanceConstructors.Any(ctor =>
            ctor.Parameters.Length == 0 && IsReachableFromGeneratedCode(ctor.DeclaredAccessibility)
        );

    /// <summary>
    /// Whether an unmanaged struct is a single-instance-field wrapper — the shape the emitter
    /// can copy field-for-field. Explicit layout or a custom Size alters the memory picture and
    /// disqualifies it.
    /// </summary>
    private static bool ComputeHasSingleInstanceField(INamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.IsValueType || !typeSymbol.IsUnmanagedType)
            return false;

        var instanceFields = typeSymbol
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic)
            .ToList();

        bool hasExplicitOrCustomSizeLayout = typeSymbol
            .GetAttributes()
            .Any(a =>
            {
                if (
                    a.AttributeClass?.ToDisplayString()
                    != "System.Runtime.InteropServices.StructLayoutAttribute"
                )
                    return false;
                if (
                    a.ConstructorArguments.Length > 0
                    && a.ConstructorArguments[0].Value is int layoutKind
                    && layoutKind == 2 /* LayoutKind.Explicit */
                )
                    return true;
                return a.NamedArguments.Any(na =>
                    na.Key == "Size" && na.Value.Value is int size && size > 0
                );
            });

        return instanceFields.Count == 1 && !hasExplicitOrCustomSizeLayout;
    }

    /// <summary>
    /// The per-target context <see cref="CollectMembers"/> reads: identical for every member of
    /// one type, and rebuilt per compound child so the containment path and depth stay
    /// path-relative. A parameter object, not new state: each field was already a parameter of
    /// the CC-105 method it came from (#263).
    /// </summary>
    private sealed record MemberScope(
        string ClassName,
        Location FallbackLocation,
        ParquetApiLevel ApiLevel,
        CompoundKinds CompoundKinds,
        List<DiagnosticInfo> Diagnostics,
        HashSet<INamedTypeSymbol> ContainmentPath,
        int CompoundDepth
    );

    /// <summary>
    /// Per-type collection state: the columns found so far, the names claimed so far, and the
    /// rejection flag the caller needs for PARQ003 suppression. Names collide per group and
    /// cycles are path-relative, so each level of a compound tree gets exactly one of these.
    /// </summary>
    private sealed class MemberSink
    {
        public HashSet<string> SeenColumnNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PropertyModel> Properties { get; } = [];
        public bool RejectedAnyMember { get; set; }
    }

    /// <summary>
    /// The column shape resolved from <c>[ParquetColumn]</c> and the <c>System.Text.Json</c>
    /// equivalents, plus the <c>[ParquetSortKey]</c> opt-in carried alongside it.
    /// </summary>
    private sealed record ColumnOptions(
        string Name,
        int Order,
        bool Deduplicate,
        ColumnEncoding Encoding,
        bool IsSortKey
    );

    /// <summary>
    /// Outcome of attempting to expand a leaf-classification failure into a compound member.
    /// <see cref="NotRepresentable"/> means the caller must report PARQ006 — either the type has
    /// no compound shape at all, or it has one the model declined to build (a non-string map key),
    /// and in both cases the member-level message names the member and its declared type.
    /// </summary>
    private enum CompoundOutcome
    {
        NotRepresentable,
        Rejected,
        Accepted,
    }

    // Attribute identities accepted for each member-level annotation. The extra spellings are the
    // legacy namespaces (#48's V5 lineage) and the short names matched without a namespace, so a
    // consumer's using-director choice never changes what the parser sees.
    private static readonly string[] ColumnFullNames =
    [
        ColumnAttributeFullName,
        "Parquet.Attributes.ParquetColumnAttribute",
        "Parquet.Serialization.Attributes.ParquetColumnAttribute",
    ];

    private static readonly string[] ColumnShortNames = ["ParquetColumnAttribute", "ParquetColumn"];

    // PARQ004 counts a JsonPropertyName as "decorated" too — it, like ParquetColumn, says the
    // author meant this member to be a column and will be surprised when a non-public one is not.
    private static readonly string[] ColumnOrJsonNameFullNames =
    [
        .. ColumnFullNames,
        "System.Text.Json.Serialization.JsonPropertyNameAttribute",
    ];

    private static readonly string[] ColumnOrJsonNameShortNames =
    [
        .. ColumnShortNames,
        "JsonPropertyNameAttribute",
        "JsonPropertyName",
    ];

    private static readonly string[] IgnoreFullNames =
    [
        IgnoreAttributeFullName,
        "Parquet.Attributes.ParquetIgnoreAttribute",
        "Parquet.Serialization.Attributes.ParquetIgnoreAttribute",
        "System.Text.Json.Serialization.JsonIgnoreAttribute",
    ];

    private static readonly string[] IgnoreShortNames =
    [
        "ParquetIgnoreAttribute",
        "ParquetIgnore",
        "JsonIgnoreAttribute",
        "JsonIgnore",
    ];

    private static readonly string[] SortKeyFullNames = [SortKeyAttributeFullName];

    private static readonly string[] SortKeyShortNames =
    [
        "ParquetSortKeyAttribute",
        "ParquetSortKey",
    ];

    private static readonly string[] JsonPropertyNameFullNames =
    [
        "System.Text.Json.Serialization.JsonPropertyNameAttribute",
    ];

    private static readonly string[] JsonPropertyNameShortNames =
    [
        "JsonPropertyNameAttribute",
        "JsonPropertyName",
    ];

    private static readonly string[] JsonPropertyOrderFullNames =
    [
        "System.Text.Json.Serialization.JsonPropertyOrderAttribute",
    ];

    private static readonly string[] JsonPropertyOrderShortNames =
    [
        "JsonPropertyOrderAttribute",
        "JsonPropertyOrder",
    ];

    private static readonly string[] DecimalFullNames =
    [
        DecimalAttributeFullName,
        "Parquet.Attributes.ParquetDecimalAttribute",
        "Parquet.Serialization.Attributes.ParquetDecimalAttribute",
    ];

    private static readonly string[] DecimalShortNames =
    [
        "ParquetDecimalAttribute",
        "ParquetDecimal",
    ];

    private static readonly string[] TimestampFullNames =
    [
        TimestampAttributeFullName,
        "Parquet.Attributes.ParquetTimestampAttribute",
        "Parquet.Serialization.Attributes.ParquetTimestampAttribute",
    ];

    private static readonly string[] TimestampShortNames =
    [
        "ParquetTimestampAttribute",
        "ParquetTimestamp",
    ];

    private static bool MatchesAttributeName(
        INamedTypeSymbol? attributeClass,
        string[] fullNames,
        string[] shortNames
    )
    {
        string? fullName = attributeClass?.ToDisplayString();
        if (fullName is not null && Array.IndexOf(fullNames, fullName) >= 0)
            return true;

        string? name = attributeClass?.Name;
        return name is not null && Array.IndexOf(shortNames, name) >= 0;
    }

    private static AttributeData? FindAttribute(
        ISymbol member,
        string[] fullNames,
        string[] shortNames
    )
    {
        foreach (AttributeData a in member.GetAttributes())
        {
            if (MatchesAttributeName(a.AttributeClass, fullNames, shortNames))
                return a;
        }
        return null;
    }

    private static bool HasAttribute(ISymbol member, string[] fullNames, string[] shortNames) =>
        FindAttribute(member, fullNames, shortNames) is not null;

    /// <summary>
    /// Parses every serializable member of <paramref name="declaringType"/> into the
    /// <paramref name="sink"/>, recursing through compound members whose kind is in the scope's
    /// <see cref="CompoundKinds"/>.
    /// </summary>
    /// <remarks>
    /// One method serves both the top-level type and any nested <c>[ParquetSerializable]</c>
    /// member's children, so attribute handling, ordering, and the PARQ002/PARQ004/PARQ005/PARQ007
    /// rules apply uniformly down the tree. Per-type state now lives in a <see cref="MemberSink"/>
    /// and the read-only context in a <see cref="MemberScope"/> — the same values the eleven
    /// parameters of the pre-#263 method carried, grouped by what actually scopes them.
    /// </remarks>
    private static void CollectMembers(
        INamedTypeSymbol declaringType,
        MemberScope scope,
        MemberSink sink
    )
    {
        foreach (ISymbol member in GetSerializableMembers(declaringType))
        {
            CollectMember(member, scope, sink);
        }
    }

    /// <summary>
    /// The per-member pipeline: statics, non-publics, ignoreds and error types are skipped;
    /// column options and decimal/timestamp annotations are read; then the member is classified
    /// as a leaf (and modelled) or a compound (and expanded), with one diagnostic rule decided
    /// per reject.
    /// </summary>
    private static void CollectMember(ISymbol member, MemberScope scope, MemberSink sink)
    {
        if (member.IsStatic)
            return;

        Location memberLocation = member.Locations.FirstOrDefault() ?? scope.FallbackLocation;

        // Rule PARQ004: Non-public property decorated with [ParquetColumn] warning
        if (member.DeclaredAccessibility != Accessibility.Public)
        {
            ReportNonPublicColumn(member, scope, memberLocation);
            return;
        }

        ITypeSymbol? memberType = GetMemberType(member);
        if (memberType is null)
            return;

        if (HasAttribute(member, IgnoreFullNames, IgnoreShortNames))
            return;

        ColumnOptions column = ReadColumnOptions(member);

        // Rule PARQ002: Duplicate column name check. Recorded, not a reject — the member keeps
        // its position in the model and the duplicate name is what the diagnostic talks about.
        if (!sink.SeenColumnNames.Add(column.Name))
        {
            scope.Diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.DuplicateColumnName,
                    memberLocation,
                    new[] { column.Name, scope.ClassName }
                )
            );
        }

        (int? precision, int? scale) = ReadDecimalOptions(member, scope, memberLocation);
        string? timestampUnit = ReadTimestampUnit(member);

        // Unwrap Nullable<T> to get the underlying type for kind classification
        (ITypeSymbol underlyingType, bool isNullable) = UnwrapNullable(memberType);

        // An unresolved type — a missing using, a half-typed name, a reference the project has
        // not added yet — must not be reported as unsupported. The compiler is already saying
        // something accurate about it, and PARQ006 on top would fire constantly while typing.
        if (memberType.TypeKind == TypeKind.Error || underlyingType.TypeKind == TypeKind.Error)
            return;

        if (!TryClassifyKind(underlyingType, memberType, out PropertyKind kind))
        {
            // Rule PARQ006: the type must have a Parquet column representation — leaf or, when
            // the kind is compound and the consuming emitter supports it (#176), a struct/list/map
            // the parser can expand. Rejected members are left out of the model — the diagnostic
            // is an error, so the build stops either way, and emitting a column for a type with no
            // representation only buries the real message under cascading errors in generated code.
            switch (
                TryCollectCompoundMember(
                    member,
                    memberType,
                    underlyingType,
                    column,
                    isNullable,
                    memberLocation,
                    scope,
                    sink
                )
            )
            {
                case CompoundOutcome.Accepted:
                    return;
                case CompoundOutcome.Rejected:
                    // The builder already reported why (cycle, depth, a child-level rule).
                    sink.RejectedAnyMember = true;
                    return;
            }

            scope.Diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.UnsupportedPropertyType,
                    memberLocation,
                    new[] { member.Name, memberType.ToDisplayString(), scope.ClassName }
                )
            );
            sink.RejectedAnyMember = true;
            return;
        }

        // Rule PARQ011: the type has a representation in Parquet.Net 6 but not in the 4.x/5.x
        // API. Reported separately from PARQ006 because the answer is different: the member is
        // fine, the backend is not, and the fix is to use the v6 package rather than to change
        // the type.
        if (scope.ApiLevel == ParquetApiLevel.V4 && !IsSupportedOnClassicApi(underlyingType))
        {
            scope.Diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.TypeUnsupportedOnClassicApi,
                    memberLocation,
                    new[] { member.Name, memberType.ToDisplayString(), scope.ClassName }
                )
            );
            sink.RejectedAnyMember = true;
            return;
        }

        // Rule PARQ007: the read path materialises through an object initializer, so every
        // column needs a reachable setter. Without this the failure was CS0200/CS0191 reported
        // against generated source, with nothing pointing at the declaration responsible.
        if (!IsAssignable(member))
        {
            scope.Diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.MemberNotAssignable,
                    memberLocation,
                    new[] { member.Name, scope.ClassName }
                )
            );
            sink.RejectedAnyMember = true;
            return;
        }

        PropertyModel propertyModel = BuildLeafModel(
            member,
            memberType,
            underlyingType,
            kind,
            column,
            isNullable,
            precision,
            scale,
            timestampUnit
        );

        // Rule PARQ014: an opt-in marker the pruning rules reject is reported rather than
        // silently dropped, so the author is not left waiting for a method that never appears.
        ReportIneligibleSortKey(
            column.IsSortKey,
            propertyModel,
            member,
            scope.ClassName,
            scope.FallbackLocation,
            scope.Diagnostics
        );

        sink.Properties.Add(propertyModel);
    }

    private static void ReportNonPublicColumn(
        ISymbol member,
        MemberScope scope,
        Location memberLocation
    )
    {
        if (!HasAttribute(member, ColumnOrJsonNameFullNames, ColumnOrJsonNameShortNames))
            return;

        scope.Diagnostics.Add(
            new DiagnosticInfo(
                DiagnosticDescriptors.NonPublicPropertyIgnored,
                memberLocation,
                new[] { member.Name, scope.ClassName }
            )
        );
    }

    private static ITypeSymbol? GetMemberType(ISymbol member) =>
        member switch
        {
            IPropertySymbol propertySymbol => propertySymbol.Type,
            IFieldSymbol fieldSymbol => fieldSymbol.Type,
            _ => null,
        };

    private static ColumnOptions ReadColumnOptions(ISymbol member)
    {
        AttributeData? columnAttr = FindAttribute(member, ColumnFullNames, ColumnShortNames);

        string columnName = member.Name;
        int order = -1;
        bool deduplicate = false;
        ColumnEncoding encoding = ColumnEncoding.Default;

        if (columnAttr is not null)
        {
            if (
                columnAttr.ConstructorArguments.Length > 0
                && columnAttr.ConstructorArguments[0].Value is string customColName
            )
            {
                columnName = customColName;
            }

            // Named arguments used to be read only when a constructor argument was also present,
            // which made [ParquetColumn(Order = 2)] — reorder without rename — impossible to
            // express: there was no parameterless constructor, and even with one the Order would
            // have been ignored. Name is accepted as a named argument for the same reason.
            foreach (KeyValuePair<string, TypedConstant> namedArg in columnAttr.NamedArguments)
            {
                if (namedArg.Key == "Order" && namedArg.Value.Value is int customOrder)
                    order = customOrder;
                else if (namedArg.Key == "Name" && namedArg.Value.Value is string namedColName)
                    columnName = namedColName;
                else if (namedArg.Key == "Deduplicate" && namedArg.Value.Value is bool dedupe)
                    deduplicate = dedupe;
                else if (namedArg.Key == "Encoding" && namedArg.Value.Value is int encodingInt)
                    encoding = (ColumnEncoding)encodingInt;
            }
        }

        // Fallback to JsonPropertyNameAttribute if column name was not explicitly specified via ParquetColumn
        AttributeData? jsonPropertyAttr = FindAttribute(
            member,
            JsonPropertyNameFullNames,
            JsonPropertyNameShortNames
        );
        if (
            columnName == member.Name
            && jsonPropertyAttr is not null
            && jsonPropertyAttr.ConstructorArguments.Length > 0
            && jsonPropertyAttr.ConstructorArguments[0].Value is string jsonColName
        )
        {
            columnName = jsonColName;
        }

        // Fallback to JsonPropertyOrderAttribute if order was not explicitly specified via ParquetColumn
        AttributeData? jsonOrderAttr = FindAttribute(
            member,
            JsonPropertyOrderFullNames,
            JsonPropertyOrderShortNames
        );
        if (
            order == -1
            && jsonOrderAttr is not null
            && jsonOrderAttr.ConstructorArguments.Length > 0
            && jsonOrderAttr.ConstructorArguments[0].Value is int jsonOrder
        )
        {
            order = jsonOrder;
        }

        // Sorted row-group pruning is opt-in (issue #151): the lookup overloads are public API on
        // the generated class, so the model author names the keys rather than getting two methods
        // per eligible column whether or not anything looks them up.
        bool isSortKey = HasAttribute(member, SortKeyFullNames, SortKeyShortNames);

        return new ColumnOptions(columnName, order, deduplicate, encoding, isSortKey);
    }

    private static (int? Precision, int? Scale) ReadDecimalOptions(
        ISymbol member,
        MemberScope scope,
        Location memberLocation
    )
    {
        AttributeData? decimalAttr = FindAttribute(member, DecimalFullNames, DecimalShortNames);

        if (
            decimalAttr is null
            || decimalAttr.ConstructorArguments.Length < 2
            || decimalAttr.ConstructorArguments[0].Value is not int p
            || decimalAttr.ConstructorArguments[1].Value is not int s
        )
        {
            return (null, null);
        }

        // Rule PARQ005: Decimal precision/scale validation. The values are still carried — a
        // precision error stops the build on the diagnostic, and PARQ005 is the only message.
        if (p < s || p > 38 || p <= 0 || s < 0)
        {
            scope.Diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.InvalidDecimalPrecisionScale,
                    memberLocation,
                    new[]
                    {
                        member.Name,
                        p.ToString(CultureInfo.InvariantCulture),
                        s.ToString(CultureInfo.InvariantCulture),
                    }
                )
            );
        }

        return (p, s);
    }

    private static string? ReadTimestampUnit(ISymbol member)
    {
        AttributeData? timestampAttr = FindAttribute(
            member,
            TimestampFullNames,
            TimestampShortNames
        );
        if (timestampAttr is not null && timestampAttr.ConstructorArguments.Length > 0)
            return timestampAttr.ConstructorArguments[0].Value?.ToString();

        return null;
    }

    private static (ITypeSymbol UnderlyingType, bool IsNullable) UnwrapNullable(
        ITypeSymbol memberType
    )
    {
        bool isNullable = IsNullableColumn(memberType);

        if (
            memberType is INamedTypeSymbol { IsGenericType: true } genericType
            && genericType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
        )
        {
            return (genericType.TypeArguments[0], true);
        }

        return (memberType, isNullable);
    }

    /// <summary>
    /// Expands a member that failed leaf classification into a compound model when its shape,
    /// the milestone dial and the pipeline scope allow it. Returns
    /// <see cref="CompoundOutcome.NotRepresentable"/> for anything the caller must reject with
    /// PARQ006 — an unsupported shape, a kind the backend cannot emit yet, a list outside the
    /// M3a pipeline scope, or a compound shape the builder declined (a non-string map key) so the
    /// member's own name and type appear in the message.
    /// </summary>
    private static CompoundOutcome TryCollectCompoundMember(
        ISymbol member,
        ITypeSymbol memberType,
        ITypeSymbol underlyingType,
        ColumnOptions column,
        bool isNullable,
        Location memberLocation,
        MemberScope scope,
        MemberSink sink
    )
    {
        if (!TryClassifyCompound(underlyingType, out PropertyKind compoundKind))
            return CompoundOutcome.NotRepresentable;

        // A compound shape the backend cannot emit yet is rejected exactly like an unsupported
        // type: the milestone dial (CompoundKinds) says how far the emitter has got, and until
        // then the member must fail the build where it is declared.
        if (!IsCompoundKindEmittable(compoundKind, scope.CompoundKinds))
            return CompoundOutcome.NotRepresentable;

        // M3b scope under the v6 pipeline dial (Struct|List, no Map): row-level lists only;
        // element shapes are screened against the built model below. The full-capability dial
        // stays unfiltered so parser tests and later milestones can build whole trees.
        bool pipelineScopedDial =
            (scope.CompoundKinds & (CompoundKinds.List | CompoundKinds.Map)) == CompoundKinds.List;
        if (compoundKind == PropertyKind.List && pipelineScopedDial && scope.CompoundDepth > 0)
            return CompoundOutcome.NotRepresentable;

        PropertyModel? compoundModel = BuildCompoundModel(
            compoundKind,
            underlyingType,
            member.Name,
            memberType,
            column.Name,
            column.Order,
            isNullable,
            scope.ApiLevel,
            scope.ClassName,
            memberLocation,
            scope.Diagnostics,
            scope.ContainmentPath,
            scope.CompoundDepth,
            scope.CompoundKinds,
            out bool compoundRejected
        );

        if (compoundRejected)
            return CompoundOutcome.Rejected;

        if (compoundModel is null)
            return CompoundOutcome.NotRepresentable;

        if (
            compoundKind == PropertyKind.List
            && pipelineScopedDial
            && !ListElementShapeSupported(compoundModel)
        )
        {
            // Decline shapes the current stacks defer: value-type elements or compound
            // children inside list elements.
            return CompoundOutcome.NotRepresentable;
        }

        // Rule PARQ014: a compound member has no row-group statistics of its own.
        ReportIneligibleSortKey(
            column.IsSortKey,
            compoundModel,
            member,
            scope.ClassName,
            scope.FallbackLocation,
            scope.Diagnostics
        );
        sink.Properties.Add(compoundModel);
        return CompoundOutcome.Accepted;
    }

    private static PropertyModel BuildLeafModel(
        ISymbol member,
        ITypeSymbol memberType,
        ITypeSymbol underlyingType,
        PropertyKind kind,
        ColumnOptions column,
        bool isNullable,
        int? precision,
        int? scale,
        string? timestampUnit
    )
    {
        // For enum types, capture the underlying type name for correct array allocation
        string? enumUnderlyingTypeName = null;
        if (kind == PropertyKind.Enum && underlyingType is INamedTypeSymbol enumTypeSymbol)
            enumUnderlyingTypeName = enumTypeSymbol.EnumUnderlyingType?.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );

        string typeName = memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new PropertyModel(
            Name: member.Name,
            ParquetColumnName: column.Name,
            TypeName: typeName,
            TimestampUnit: timestampUnit,
            EnumUnderlyingTypeName: enumUnderlyingTypeName,
            Order: column.Order,
            DecimalPrecision: precision,
            DecimalScale: scale,
            Kind: kind,
            IsNullable: isNullable,
            Deduplicate: column.Deduplicate,
            Encoding: column.Encoding
        )
        {
            IsSortKey = column.IsSortKey,
        };
    }

    /// <summary>
    /// Reports PARQ014 when a member carries <c>[ParquetSortKey]</c> but cannot drive sorted
    /// row-group pruning, naming the reason from <see cref="SortKeyEligibility"/>.
    /// </summary>
    private static void ReportIneligibleSortKey(
        bool isSortKey,
        PropertyModel model,
        ISymbol member,
        string className,
        Location fallbackLocation,
        List<DiagnosticInfo> diagnostics
    )
    {
        if (!isSortKey || SortKeyEligibility.IsEligible(model, out string reason))
            return;

        Location loc = member.Locations.FirstOrDefault() ?? fallbackLocation;
        diagnostics.Add(
            new DiagnosticInfo(
                DiagnosticDescriptors.SortKeyNotEligible,
                loc,
                new[] { member.Name, className, reason }
            )
        );
    }

    /// <summary>
    /// Maximum compound nesting depth the parser will expand (issue #176, PARQ013).
    /// </summary>
    /// <remarks>
    /// Record-shredding is unrolled per leaf at generation time, so emitted-code size grows with
    /// depth. Six levels covers any realistic domain model while bounding pathological recursion.
    /// </remarks>
    public const int MaxCompoundDepth = 6;

    /// <summary>
    /// Generic definitions accepted as <see cref="PropertyKind.List"/> members. Value types
    /// (<c>Nullable&lt;T&gt;</c>) are unwrapped before this check, so a <c>List&lt;int?&gt;</c>
    /// reaches it as the collection itself with a nullable element inside.
    /// </summary>
    private static readonly string[] SupportedCollectionDefinitions =
    [
        "System.Collections.Generic.List",
        "System.Collections.Generic.IList",
        "System.Collections.Generic.ICollection",
        "System.Collections.Generic.IReadOnlyCollection",
        "System.Collections.Generic.IReadOnlyList",
        "System.Collections.Generic.IEnumerable",
    ];

    /// <summary>
    /// Generic definitions accepted as <see cref="PropertyKind.Map"/> members. The key must be
    /// <c>string</c>: the Parquet MAP key is REQUIRED, so any key type whose values could be null
    /// would produce files no reader is obliged to accept.
    /// </summary>
    private static readonly string[] SupportedDictionaryDefinitions =
    [
        "System.Collections.Generic.Dictionary",
        "System.Collections.Generic.IDictionary",
        "System.Collections.Generic.IReadOnlyDictionary",
    ];

    /// <summary>
    /// Whether the consuming emitter can currently express this compound kind
    /// (the <see cref="CompoundKinds"/> milestone dial, #176).
    /// </summary>
    /// <summary>
    /// M3b stack 2 scope for row-level lists under the pipeline dial: leaf elements (M3a) or
    /// reference-POCO elements whose collectable members are all leaves. Value-type elements,
    /// compound children inside elements, and lists inside compounds live in later stacks;
    /// the parser builds the full tree under its test dial regardless.
    /// </summary>
    private static bool ListElementShapeSupported(PropertyModel listModel)
    {
        if (listModel.Element is not { Kind: PropertyKind.Struct } element)
            return true;
        if (element.CompoundIsValueType)
            return false;
        foreach (PropertyModel child in element.Children)
        {
            if (child.Kind is PropertyKind.Struct or PropertyKind.List or PropertyKind.Map)
                return false;
        }
        return true;
    }

    private static bool IsCompoundKindEmittable(PropertyKind kind, CompoundKinds allowed) =>
        kind switch
        {
            PropertyKind.Struct => allowed.HasFlag(CompoundKinds.Struct),
            PropertyKind.List => allowed.HasFlag(CompoundKinds.List),
            PropertyKind.Map => allowed.HasFlag(CompoundKinds.Map),
            _ => true,
        };

    /// <summary>
    /// Classifies a type that failed <see cref="TryClassifyKind"/> as a compound member kind.
    /// Returns false for anything with no compound representation (a POCO without the attribute,
    /// <c>Dictionary&lt;int, string&gt;</c> — whose key type is only checked when building the
    /// model, so the rejection surfaces as a member-level PARQ006 with the member's own name).
    /// </summary>
    private static bool TryClassifyCompound(ITypeSymbol underlyingType, out PropertyKind kind)
    {
        // byte[] reached PropertyKind.ByteArray as a leaf before this point; any other array is
        // a list of its element type.
        if (underlyingType is IArrayTypeSymbol)
        {
            kind = PropertyKind.List;
            return true;
        }

        if (underlyingType is INamedTypeSymbol { IsGenericType: true } generic)
        {
            string definition = generic.ConstructedFrom.ToDisplayString().Split('<')[0];

            if (Array.IndexOf(SupportedCollectionDefinitions, definition) >= 0)
            {
                kind = PropertyKind.List;
                return true;
            }

            if (Array.IndexOf(SupportedDictionaryDefinitions, definition) >= 0)
            {
                kind = PropertyKind.Map;
                return true;
            }
        }

        // Nested POCO: must opt in with [ParquetSerializable] (issue #176's contract — the
        // parent inlines the child's schema, so no other declaration is accepted). Generic
        // children reach BuildCompoundModel, which rejects them with PARQ010 — more precise
        // than the generic PARQ006 wording.
        if (
            underlyingType is INamedTypeSymbol pojo
            && (pojo.TypeKind == TypeKind.Class || pojo.TypeKind == TypeKind.Struct)
            && pojo.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == AttributeFullName)
        )
        {
            kind = PropertyKind.Struct;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>
    /// Expands a compound member into a recursive <see cref="PropertyModel"/> subtree, reporting
    /// compound-specific diagnostics (PARQ012 cycles, PARQ013 depth, and the per-type rules that
    /// the child type itself violates) on the shared diagnostics list.
    /// </summary>
    /// <returns>
    /// The subtree, or null when the member is not actually buildable. <paramref name="rejected"/>
    /// distinguishes "a diagnostic already explains it" (true) from "fall through to PARQ006"
    /// (false — currently only the non-string map key).
    /// </returns>
    private static PropertyModel? BuildCompoundModel(
        PropertyKind kind,
        ITypeSymbol underlyingType,
        string memberName,
        ITypeSymbol declaredType,
        string columnName,
        int order,
        bool isNullable,
        ParquetApiLevel apiLevel,
        string declaringTypeName,
        Location location,
        List<DiagnosticInfo> diagnostics,
        HashSet<INamedTypeSymbol> containmentPath,
        int compoundDepth,
        CompoundKinds compoundKinds,
        out bool rejected
    )
    {
        rejected = false;

        if (compoundDepth + 1 > MaxCompoundDepth)
        {
            diagnostics.Add(
                new DiagnosticInfo(
                    DiagnosticDescriptors.NestedTypeTooDeep,
                    location,
                    [
                        memberName,
                        declaringTypeName,
                        (compoundDepth + 1).ToString(CultureInfo.InvariantCulture),
                        MaxCompoundDepth.ToString(CultureInfo.InvariantCulture),
                    ]
                )
            );
            rejected = true;
            return null;
        }

        string typeName = declaredType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        switch (kind)
        {
            case PropertyKind.Struct:
                var childSymbol = (INamedTypeSymbol)underlyingType;
                string childName = GetNestedQualifiedTypeName(childSymbol);

                if (childSymbol.TypeParameters.Length > 0)
                {
                    diagnostics.Add(
                        new DiagnosticInfo(
                            DiagnosticDescriptors.GenericTypeNotSupported,
                            location,
                            [childSymbol.Name]
                        )
                    );
                    rejected = true;
                    return null;
                }

                if (!IsReachableFromGeneratedCode(childSymbol.DeclaredAccessibility))
                {
                    string containingName = childSymbol.ContainingType is null
                        ? childSymbol.Name
                        : GetNestedQualifiedTypeName(childSymbol.ContainingType);
                    diagnostics.Add(
                        new DiagnosticInfo(
                            DiagnosticDescriptors.NestedTypeNotSupported,
                            location,
                            [
                                childSymbol.Name,
                                containingName,
                                childSymbol.DeclaredAccessibility.ToString(),
                            ]
                        )
                    );
                    rejected = true;
                    return null;
                }

                // Cycle: the type is already being expanded further up this path. Parquet
                // schemas are trees; there is no finite column layout past this point.
                if (!containmentPath.Add(childSymbol))
                {
                    diagnostics.Add(
                        new DiagnosticInfo(
                            DiagnosticDescriptors.NestedTypeCycleDetected,
                            location,
                            [memberName, declaringTypeName, childName]
                        )
                    );
                    rejected = true;
                    return null;
                }

                bool childHasParameterlessConstructor =
                    childSymbol.IsValueType
                    || childSymbol.InstanceConstructors.Any(ctor =>
                        ctor.Parameters.Length == 0
                        && IsReachableFromGeneratedCode(ctor.DeclaredAccessibility)
                    );
                if (!childHasParameterlessConstructor)
                {
                    containmentPath.Remove(childSymbol);
                    diagnostics.Add(
                        new DiagnosticInfo(
                            DiagnosticDescriptors.NoParameterlessConstructor,
                            location,
                            [childName]
                        )
                    );
                    rejected = true;
                    return null;
                }

                Location childFallback = childSymbol.Locations.FirstOrDefault() ?? location;
                var childScope = new MemberScope(
                    ClassName: childName,
                    FallbackLocation: childFallback,
                    ApiLevel: apiLevel,
                    CompoundKinds: compoundKinds,
                    Diagnostics: diagnostics,
                    ContainmentPath: containmentPath,
                    CompoundDepth: compoundDepth + 1
                );
                var childSink = new MemberSink();

                CollectMembers(childSymbol, childScope, childSink);
                containmentPath.Remove(childSymbol);

                if (childSink.RejectedAnyMember)
                {
                    rejected = true;
                    return null;
                }

                if (childSink.Properties.Count == 0)
                {
                    // An empty group serializes to nothing — the member would silently
                    // disappear from the file. Same reasoning as PARQ003 at top level.
                    diagnostics.Add(
                        new DiagnosticInfo(
                            DiagnosticDescriptors.NoPropertiesFound,
                            location,
                            [childName]
                        )
                    );
                    rejected = true;
                    return null;
                }

                List<PropertyModel> children = childSink
                    .Properties.OrderBy(p => p.Order >= 0 ? p.Order : int.MaxValue)
                    .ToList();

                return new PropertyModel(
                    memberName,
                    columnName,
                    typeName,
                    TimestampUnit: null,
                    null,
                    order,
                    DecimalPrecision: null,
                    DecimalScale: null,
                    PropertyKind.Struct,
                    isNullable,
                    Deduplicate: false,
                    ColumnEncoding.Default
                )
                {
                    Children = new EquatableArray<PropertyModel>(children.ToArray()),
                    CompoundIsValueType = childSymbol.IsValueType,
                };

            case PropertyKind.List:
                ITypeSymbol elementType = underlyingType is IArrayTypeSymbol array
                    ? array.ElementType
                    : ((INamedTypeSymbol)underlyingType).TypeArguments[0];

                PropertyModel? element = BuildNestedElementModel(
                    elementType,
                    "element",
                    apiLevel,
                    memberName,
                    declaringTypeName,
                    location,
                    diagnostics,
                    containmentPath,
                    compoundDepth + 1,
                    compoundKinds,
                    out bool elementRejected
                );
                if (elementRejected || element is null)
                {
                    rejected = elementRejected;
                    return null;
                }

                return new PropertyModel(
                    memberName,
                    columnName,
                    typeName,
                    TimestampUnit: null,
                    null,
                    order,
                    DecimalPrecision: null,
                    DecimalScale: null,
                    PropertyKind.List,
                    isNullable,
                    Deduplicate: false,
                    ColumnEncoding.Default
                )
                {
                    Element = element,
                };

            case PropertyKind.Map:
                var mapSymbol = (INamedTypeSymbol)underlyingType;
                string keyFqn = mapSymbol
                    .TypeArguments[0]
                    .WithNullableAnnotation(NullableAnnotation.None)
                    .ToDisplayString();
                if (keyFqn != "string")
                {
                    // Deliberately *not* rejected here: the caller's PARQ006 message names the
                    // member and shows its declared type, which beats a synthetic "key" error.
                    return null;
                }

                PropertyModel? value = BuildNestedElementModel(
                    mapSymbol.TypeArguments[1],
                    "value",
                    apiLevel,
                    memberName,
                    declaringTypeName,
                    location,
                    diagnostics,
                    containmentPath,
                    compoundDepth + 1,
                    compoundKinds,
                    out bool valueRejected
                );
                if (valueRejected || value is null)
                {
                    rejected = valueRejected;
                    return null;
                }

                var key = new PropertyModel(
                    "key",
                    "key",
                    "string",
                    TimestampUnit: null,
                    EnumUnderlyingTypeName: null,
                    Order: -1,
                    DecimalPrecision: null,
                    DecimalScale: null,
                    Kind: PropertyKind.Primitive,
                    IsNullable: false
                );

                return new PropertyModel(
                    memberName,
                    columnName,
                    typeName,
                    TimestampUnit: null,
                    null,
                    order,
                    DecimalPrecision: null,
                    DecimalScale: null,
                    PropertyKind.Map,
                    isNullable,
                    Deduplicate: false,
                    ColumnEncoding.Default
                )
                {
                    Children = new EquatableArray<PropertyModel>([key]),
                    MapValue = value,
                };
        }

        return null;
    }

    /// <summary>
    /// Builds the model for a list element or map value: leaf types go straight through the same
    /// classifier a declared member uses (including the v4 API check, since a <c>List&lt;
    /// System.ReadOnlyMemory&lt;byte&gt;&gt;</c> is unsupported by 4.x exactly like the bare type);
    /// compound types recurse through <see cref="BuildCompoundModel"/>.
    /// </summary>
    /// <param name="declaredType"></param>
    /// <param name="elementName">Schema segment name — "element" or "value".</param>
    /// <param name="apiLevel"></param>
    /// <param name="memberName">The enclosing member's name, for diagnostic messages.</param>
    /// <param name="declaringTypeName"></param>
    /// <param name="location"></param>
    /// <param name="diagnostics"></param>
    /// <param name="containmentPath"></param>
    /// <param name="compoundDepth"></param>
    /// <param name="compoundKinds">The milestone dial, threaded through to nested builders.</param>
    /// <param name="rejected"></param>
    private static PropertyModel? BuildNestedElementModel(
        ITypeSymbol declaredType,
        string elementName,
        ParquetApiLevel apiLevel,
        string memberName,
        string declaringTypeName,
        Location location,
        List<DiagnosticInfo> diagnostics,
        HashSet<INamedTypeSymbol> containmentPath,
        int compoundDepth,
        CompoundKinds compoundKinds,
        out bool rejected
    )
    {
        rejected = false;

        ITypeSymbol underlying = declaredType;
        bool nullable = IsNullableColumn(declaredType);
        if (
            declaredType is INamedTypeSymbol { IsGenericType: true } generic
            && generic.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
        )
        {
            nullable = true;
            underlying = generic.TypeArguments[0];
        }

        if (underlying.TypeKind == TypeKind.Error)
        {
            rejected = true; // compilation already reports it
            return null;
        }

        string display = memberName + " " + elementName;

        if (TryClassifyKind(underlying, declaredType, out PropertyKind leafKind))
        {
            if (apiLevel == ParquetApiLevel.V4 && !IsSupportedOnClassicApi(underlying))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        DiagnosticDescriptors.TypeUnsupportedOnClassicApi,
                        location,
                        [display, declaredType.ToDisplayString(), declaringTypeName]
                    )
                );
                rejected = true;
                return null;
            }

            string? enumUnderlyingTypeName = null;
            if (leafKind == PropertyKind.Enum && underlying is INamedTypeSymbol enumSymbol)
            {
                enumUnderlyingTypeName = enumSymbol.EnumUnderlyingType?.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                );
            }

            return new PropertyModel(
                elementName,
                elementName,
                declaredType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TimestampUnit: null,
                enumUnderlyingTypeName,
                -1,
                DecimalPrecision: null,
                DecimalScale: null,
                leafKind,
                nullable
            );
        }

        if (TryClassifyCompound(underlying, out PropertyKind compoundKind))
        {
            PropertyModel? nested = BuildCompoundModel(
                compoundKind,
                underlying,
                elementName,
                declaredType,
                elementName,
                -1,
                nullable,
                apiLevel,
                declaringTypeName,
                location,
                diagnostics,
                containmentPath,
                compoundDepth,
                compoundKinds,
                out rejected
            );
            return nested;
        }

        diagnostics.Add(
            new DiagnosticInfo(
                DiagnosticDescriptors.UnsupportedPropertyType,
                location,
                [display, declaredType.ToDisplayString(), declaringTypeName]
            )
        );
        rejected = true;
        return null;
    }

    /// <summary>
    /// The type name as generated code must spell it from namespace scope: dotted through any
    /// containing types ("Outer.Inner"), identical to <c>Name</c> for a top-level type.
    /// </summary>
    private static string GetNestedQualifiedTypeName(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.ContainingType is null)
            return typeSymbol.Name;

        var parts = new List<string>();
        for (
            ITypeSymbol? current = typeSymbol;
            current is not null;
            current = current.ContainingType
        )
            parts.Add(current.Name);

        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>
    /// Collects the properties and fields of a type together with those it inherits.
    /// </summary>
    /// <remarks>
    /// <c>GetMembers()</c> returns declared members only, so a type deriving from a base that
    /// carried columns silently lost every one of them — no diagnostic, just missing columns.
    /// <para>
    /// Two deliberate choices. The walk stops at the first base type not declared in source, so a
    /// model deriving from a framework type does not drag in <c>Exception.Data</c> and friends as
    /// columns. And members are collected base-first, with a derived declaration replacing a
    /// shadowed base one *in the base's position* — so adding an <see langword="override"/> or <c>new</c>
    /// member changes which declaration is used without reordering the schema.
    /// </para>
    /// </remarks>
    private static List<ISymbol> GetSerializableMembers(INamedTypeSymbol typeSymbol)
    {
        var chain = new List<INamedTypeSymbol>();
        for (
            INamedTypeSymbol? current = typeSymbol;
            current is not null && current.SpecialType == SpecialType.None;
            current = current.BaseType
        )
        {
            chain.Add(current);

            INamedTypeSymbol? next = current.BaseType;
            if (next is null || next.DeclaringSyntaxReferences.IsEmpty)
                break;
        }

        chain.Reverse();

        var ordered = new List<ISymbol>();
        var positionByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (INamedTypeSymbol type in chain)
        {
            foreach (ISymbol member in type.GetMembers())
            {
                if (member is not IPropertySymbol && member is not IFieldSymbol)
                    continue;

                if (positionByName.TryGetValue(member.Name, out int existing))
                    ordered[existing] = member;
                else
                {
                    positionByName[member.Name] = ordered.Count;
                    ordered.Add(member);
                }
            }
        }

        return ordered;
    }

    /// <summary>
    /// Types that pass straight through as a <see cref="PropertyKind.Primitive"/> <c>DataField</c>.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>Parquet.Encodings.SchemaEncoder.SupportedTypes</c> in Parquet.Net 6, minus the
    /// types handled by a dedicated <see cref="PropertyKind"/> below. Keeping it aligned with that
    /// list is deliberate: PARQ006 should reject exactly what Parquet.Net rejects and nothing more,
    /// so the diagnostic can never fail a build that would otherwise have worked.
    /// <para>
    /// Notably absent, and so reported by PARQ006: <c>char</c>, <c>DateTimeOffset</c>, arrays other
    /// than <c>byte[]</c>, collections, and nested user types.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> SupportedPassthroughTypes = new(StringComparer.Ordinal)
    {
        "bool",
        "byte",
        "sbyte",
        "short",
        "ushort",
        "int",
        "uint",
        "long",
        "ulong",
        "float",
        "double",
        "string",
        "System.DateOnly",
        "System.ReadOnlyMemory<byte>",
        "System.ReadOnlyMemory<char>",
        "Parquet.File.Values.Primitives.Interval",
    };

    /// <summary>
    /// Types in Parquet.Net 6's supported set that the 4.x/5.x line has no encoder for.
    /// </summary>
    /// <remarks>
    /// Taken from the difference between <c>SchemaEncoder.SupportedTypes</c> in 6.0.3 and in 4.25.0.
    /// <c>DateOnly</c> and <c>TimeOnly</c> are deliberately absent: 4.25.0 guards them behind
    /// <c>NET6_0_OR_GREATER</c>, and a consumer old enough to miss them cannot name the types either,
    /// so the compiler has already rejected such a member before this rule is reached.
    /// </remarks>
    private static readonly HashSet<string> ClassicApiUnsupportedTypes = new(StringComparer.Ordinal)
    {
        "System.ReadOnlyMemory<byte>",
        "System.ReadOnlyMemory<char>",
    };

    /// <summary>
    /// Whether a member type has a column representation in the Parquet.Net 4.x/5.x API.
    /// </summary>
    private static bool IsSupportedOnClassicApi(ITypeSymbol underlyingType) =>
        !ClassicApiUnsupportedTypes.Contains(
            underlyingType.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString()
        );

    /// <summary>
    /// Classifies a member's Parquet field kind, returning false when the type has no representation.
    /// </summary>
    private static bool TryClassifyKind(
        ITypeSymbol underlyingType,
        ITypeSymbol memberType,
        out PropertyKind kind
    )
    {
        // Enum — any underlying integral type is fine, Parquet.Net accepts every enum.
        if (underlyingType.TypeKind == TypeKind.Enum)
        {
            kind = PropertyKind.Enum;
            return true;
        }

        // byte[] — must be checked before any other array reaches the passthrough set.
        if (memberType is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            kind = PropertyKind.ByteArray;
            return true;
        }

        // The annotation has to come off before the name is matched. `ToDisplayString()` renders an
        // annotated reference type as "string?", not "string", and the Nullable<T> unwrap at the
        // call site only handles value types — so every `string?` column in the repository was
        // reported as an unsupported type by PARQ006 the moment the rule was switched on.
        string fqn = underlyingType
            .WithNullableAnnotation(NullableAnnotation.None)
            .ToDisplayString();

        switch (fqn)
        {
            case "System.DateOnly":
                kind = PropertyKind.DateOnly;
                return true;
            case "decimal":
                kind = PropertyKind.Decimal;
                return true;
            case "System.DateTime":
                kind = PropertyKind.DateTime;
                return true;
            case "System.TimeSpan":
                kind = PropertyKind.TimeSpan;
                return true;
            case "System.TimeOnly":
                kind = PropertyKind.TimeOnly;
                return true;
            case "System.Guid":
                kind = PropertyKind.Guid;
                return true;
        }

        // System.DateTimeOffset used to map to PropertyKind.DateTime. That emitted a
        // DateTimeDataField, whose CLR type is DateTime, and then wrote ReadOnlyMemory<DateTimeOffset>
        // into it — the types disagreed, and the offset would have been lost even had it bound.
        // Parquet.Net has no DateTimeOffset representation, so it now reports as unsupported.
        kind = PropertyKind.Primitive;
        return SupportedPassthroughTypes.Contains(fqn);
    }

    /// <summary>
    /// Decides whether a member's column should be written as optional.
    /// </summary>
    /// <remarks>
    /// Previously this was <c>IsReferenceType || annotation == Annotated</c>, so every reference
    /// type produced an optional column — a <c>string Name</c> under <c>#nullable enable</c> was
    /// indistinguishable from a <c>string? Name</c>, and the required/optional distinction that
    /// Spark, Athena and PyArrow all read was lost on the way out.
    /// <para>
    /// The annotation is authoritative wherever the compilation has nullable analysis switched on.
    /// Where it does not — <c>NullableAnnotation.None</c>, an oblivious context — nothing can
    /// be inferred about a reference type, so the conservative optional column is kept. Value types
    /// answer correctly from the annotation alone, and <c>Nullable&lt;T&gt;</c> is handled by the
    /// unwrap at the call site regardless of context.
    /// </para>
    /// </remarks>
    private static bool IsNullableColumn(ITypeSymbol memberType)
    {
        switch (memberType.NullableAnnotation)
        {
            case NullableAnnotation.Annotated:
                return true;
            case NullableAnnotation.NotAnnotated:
                return false;
            default:
                return memberType.IsReferenceType;
        }
    }

    /// <summary>
    /// Determines whether the generated deserializer can assign this member in an object initializer.
    /// </summary>
    private static bool IsAssignable(ISymbol member)
    {
        // `init` accessors surface as a SetMethod with IsInitOnly, which an object initializer can
        // use, so no special case is needed for them.
        if (member is IPropertySymbol property)
            return property.SetMethod is not null
                && IsReachableFromGeneratedCode(property.SetMethod.DeclaredAccessibility);

        if (member is IFieldSymbol field)
            return !field.IsReadOnly && !field.IsConst;

        return false;
    }

    /// <summary>
    /// Whether generated code — a sibling type in the same assembly, not a nested one — can reach this.
    /// </summary>
    private static bool IsReachableFromGeneratedCode(Accessibility accessibility)
    {
        // The extension class sits alongside the target type rather than inside it, so `private` and
        // `protected` members are out of reach even though they are in the same assembly.
        return accessibility == Accessibility.Public
            || accessibility == Accessibility.Internal
            || accessibility == Accessibility.ProtectedOrInternal;
    }
}
