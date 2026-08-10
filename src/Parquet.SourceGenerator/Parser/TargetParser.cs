using System;
using System.Collections.Generic;
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
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
/// Extracts semantic models from Roslyn syntax contexts for decorated target types and validates compiler rules.
/// </summary>
public static class TargetParser
{
    private const string AttributeFullName = "Parquet.SourceGenerator.ParquetSerializableAttribute";
    private const string ColumnAttributeFullName = "Parquet.SourceGenerator.ParquetColumnAttribute";
    private const string IgnoreAttributeFullName = "Parquet.SourceGenerator.ParquetIgnoreAttribute";
    private const string DecimalAttributeFullName = "Parquet.SourceGenerator.ParquetDecimalAttribute";
    private const string TimestampAttributeFullName = "Parquet.SourceGenerator.ParquetTimestampAttribute";

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
    public static TargetParserResult GetTargetModel(GeneratorSyntaxContext context, ParquetApiLevel apiLevel)
    {
        SyntaxNode node = context.Node;
        if (node is not TypeDeclarationSyntax typeDeclaration)
            return new TargetParserResult(null, EquatableArray<DiagnosticInfo>.Empty);

        ISymbol? symbol = context.SemanticModel.GetDeclaredSymbol(typeDeclaration);
        if (symbol is not INamedTypeSymbol typeSymbol)
            return new TargetParserResult(null, EquatableArray<DiagnosticInfo>.Empty);

        AttributeData? serializableAttr = typeSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttributeFullName);

        if (serializableAttr is null)
            return new TargetParserResult(null, EquatableArray<DiagnosticInfo>.Empty);

        var diagnostics = new List<DiagnosticInfo>();

        // Rule PARQ001: Target type must be declared as partial
        bool isPartial = typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        if (!isPartial)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.MustBePartial,
                typeDeclaration.Identifier.GetLocation(),
                new[] { typeSymbol.Name }));
        }

        // Rules PARQ009 / PARQ010: shapes the emitter cannot express. Both previously produced code
        // that did not compile — a bare unqualified name for a nested type, and a name stripped of
        // its type parameters for a generic one.
        bool isNested = typeSymbol.ContainingType is not null;
        if (isNested)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.NestedTypeNotSupported,
                typeDeclaration.Identifier.GetLocation(),
                new[] { typeSymbol.Name, typeSymbol.ContainingType!.Name }));
        }

        bool isGeneric = typeSymbol.TypeParameters.Length > 0;
        if (isGeneric)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.GenericTypeNotSupported,
                typeDeclaration.Identifier.GetLocation(),
                new[] { typeSymbol.Name }));
        }

        string namespaceName = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : typeSymbol.ContainingNamespace.ToDisplayString();

        string className = typeSymbol.Name;

        var propertyModels = new List<PropertyModel>();
        var seenColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool rejectedAnyMember = false;
        IEnumerable<ISymbol> members = GetSerializableMembers(typeSymbol);

        foreach (ISymbol member in members)
        {
            if (member.IsStatic) continue;

            // Rule PARQ004: Non-public property decorated with [ParquetColumn] warning
            if (member.DeclaredAccessibility != Accessibility.Public)
            {
                bool hasColAttr = member.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == ColumnAttributeFullName ||
                    a.AttributeClass?.ToDisplayString() == "Parquet.Attributes.ParquetColumnAttribute");

                if (hasColAttr)
                {
                    Location loc = member.Locations.FirstOrDefault() ?? typeDeclaration.Identifier.GetLocation();
                    diagnostics.Add(new DiagnosticInfo(
                        DiagnosticDescriptors.NonPublicPropertyIgnored,
                        loc,
                        new[] { member.Name, className }));
                }
                continue;
            }

            ITypeSymbol? memberType = null;
            if (member is IPropertySymbol propSymbol)
                memberType = propSymbol.Type;
            else if (member is IFieldSymbol fieldSymbol)
                memberType = fieldSymbol.Type;

            if (memberType is null) continue;

            bool isIgnored = member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == IgnoreAttributeFullName);
            if (isIgnored) continue;

            AttributeData? columnAttr = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ColumnAttributeFullName ||
                                     a.AttributeClass?.ToDisplayString() == "Parquet.Attributes.ParquetColumnAttribute");

            string columnName = member.Name;
            int order = -1;

            if (columnAttr is not null)
            {
                if (columnAttr.ConstructorArguments.Length > 0 &&
                    columnAttr.ConstructorArguments[0].Value is string customColName)
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
                }
            }

            // Rule PARQ002: Duplicate column name check
            if (!seenColumnNames.Add(columnName))
            {
                Location loc = member.Locations.FirstOrDefault() ?? typeDeclaration.Identifier.GetLocation();
                diagnostics.Add(new DiagnosticInfo(
                    DiagnosticDescriptors.DuplicateColumnName,
                    loc,
                    new[] { columnName, className }));
            }

            AttributeData? decimalAttr = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == DecimalAttributeFullName);
            int? precision = null;
            int? scale = null;
            if (decimalAttr is not null && decimalAttr.ConstructorArguments.Length >= 2)
            {
                if (decimalAttr.ConstructorArguments[0].Value is int p && decimalAttr.ConstructorArguments[1].Value is int s)
                {
                    precision = p;
                    scale = s;

                    // Rule PARQ005: Decimal precision/scale validation
                    if (p < s || p > 38 || p <= 0 || s < 0)
                    {
                        Location loc = member.Locations.FirstOrDefault() ?? typeDeclaration.Identifier.GetLocation();
                        diagnostics.Add(new DiagnosticInfo(
                            DiagnosticDescriptors.InvalidDecimalPrecisionScale,
                            loc,
                            new[] { member.Name, p.ToString(CultureInfo.InvariantCulture), s.ToString(CultureInfo.InvariantCulture) }));
                    }
                }
            }

            AttributeData? timestampAttr = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == TimestampAttributeFullName);
            string? timestampUnit = null;
            if (timestampAttr is not null && timestampAttr.ConstructorArguments.Length > 0)
                timestampUnit = timestampAttr.ConstructorArguments[0].Value?.ToString();

            // Unwrap Nullable<T> to get the underlying type for kind classification
            ITypeSymbol underlyingType = memberType;
            bool isNullable = IsNullableColumn(memberType);

            if (memberType is INamedTypeSymbol { IsGenericType: true } genericType &&
                genericType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            {
                isNullable = true;
                underlyingType = genericType.TypeArguments[0];
            }

            // An unresolved type — a missing using, a half-typed name, a reference the project has
            // not added yet — must not be reported as unsupported. The compiler is already saying
            // something accurate about it, and PARQ006 on top would fire constantly while typing.
            if (memberType.TypeKind == TypeKind.Error || underlyingType.TypeKind == TypeKind.Error)
                continue;

            // Rule PARQ006: the type must have a Parquet column representation. Rejected members are
            // left out of the model — the diagnostic is an error, so the build stops either way, and
            // emitting a column for a type with no representation only buries the real message under
            // cascading errors inside generated code.
            if (!TryClassifyKind(underlyingType, memberType, out PropertyKind kind))
            {
                Location typeLoc = member.Locations.FirstOrDefault() ?? typeDeclaration.Identifier.GetLocation();
                diagnostics.Add(new DiagnosticInfo(
                    DiagnosticDescriptors.UnsupportedPropertyType,
                    typeLoc,
                    new[] { member.Name, memberType.ToDisplayString(), className }));
                rejectedAnyMember = true;
                continue;
            }

            // Rule PARQ011: the type has a representation in Parquet.Net 6 but not in the 4.x/5.x
            // API. Reported separately from PARQ006 because the answer is different: the member is
            // fine, the backend is not, and the fix is to use the v6 package rather than to change
            // the type.
            if (apiLevel == ParquetApiLevel.V4 && !IsSupportedOnClassicApi(underlyingType))
            {
                Location classicLoc = member.Locations.FirstOrDefault() ?? typeDeclaration.Identifier.GetLocation();
                diagnostics.Add(new DiagnosticInfo(
                    DiagnosticDescriptors.TypeUnsupportedOnClassicApi,
                    classicLoc,
                    new[] { member.Name, memberType.ToDisplayString(), className }));
                rejectedAnyMember = true;
                continue;
            }

            // Rule PARQ007: the read path materialises through an object initializer, so every
            // column needs a reachable setter. Without this the failure was CS0200/CS0191 reported
            // against generated source, with nothing pointing at the declaration responsible.
            if (!IsAssignable(member))
            {
                Location setLoc = member.Locations.FirstOrDefault() ?? typeDeclaration.Identifier.GetLocation();
                diagnostics.Add(new DiagnosticInfo(
                    DiagnosticDescriptors.MemberNotAssignable,
                    setLoc,
                    new[] { member.Name, className }));
                rejectedAnyMember = true;
                continue;
            }

            // For enum types, capture the underlying type name for correct array allocation
            string? enumUnderlyingTypeName = null;
            if (kind == PropertyKind.Enum && underlyingType is INamedTypeSymbol enumTypeSymbol)
                enumUnderlyingTypeName = enumTypeSymbol.EnumUnderlyingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            string typeName = memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            propertyModels.Add(new PropertyModel(
                Name: member.Name,
                ParquetColumnName: columnName,
                TypeName: typeName,
                TimestampUnit: timestampUnit,
                EnumUnderlyingTypeName: enumUnderlyingTypeName,
                Order: order,
                DecimalPrecision: precision,
                DecimalScale: scale,
                Kind: kind,
                IsNullable: isNullable));
        }

        // Rule PARQ003: Warning if no public serializable properties found. Suppressed when a
        // member was rejected above — "this type has no serializable members" is misleading when the
        // real answer is "its members were rejected", and PARQ006/PARQ007 already say why.
        if (propertyModels.Count == 0 && !rejectedAnyMember)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.NoPropertiesFound,
                typeDeclaration.Identifier.GetLocation(),
                new[] { className }));
        }

        // Rule PARQ008: the read path constructs through an object initializer, which needs an
        // accessible parameterless constructor. Value types always have one; a positional record or
        // a class whose only constructors take arguments does not, and previously failed with CS7036
        // reported against generated source.
        bool hasParameterlessConstructor =
            typeSymbol.IsValueType ||
            typeSymbol.InstanceConstructors.Any(ctor =>
                ctor.Parameters.Length == 0 && IsReachableFromGeneratedCode(ctor.DeclaredAccessibility));

        if (!hasParameterlessConstructor)
        {
            diagnostics.Add(new DiagnosticInfo(
                DiagnosticDescriptors.NoParameterlessConstructor,
                typeDeclaration.Identifier.GetLocation(),
                new[] { className }));
        }

        List<PropertyModel> orderedProperties = propertyModels
            .OrderBy(p => p.Order >= 0 ? p.Order : int.MaxValue)
            .ToList();

        // Emission is suppressed whenever a fatal diagnostic already explains the problem. Emitting
        // anyway buries that message under cascading errors from the generated file.
        bool canEmit = isPartial && hasParameterlessConstructor && !rejectedAnyMember && !isNested && !isGeneric;

        TargetClassModel? model = canEmit ? new TargetClassModel(
            Namespace: namespaceName,
            ClassName: className,
            Properties: new EquatableArray<PropertyModel>(orderedProperties.ToArray())) : null;

        return new TargetParserResult(model, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
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
    /// shadowed base one *in the base's position* — so adding an <c>override</c> or <c>new</c>
    /// member changes which declaration is used without reordering the schema.
    /// </para>
    /// </remarks>
    private static List<ISymbol> GetSerializableMembers(INamedTypeSymbol typeSymbol)
    {
        var chain = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? current = typeSymbol;
             current is not null && current.SpecialType == SpecialType.None;
             current = current.BaseType)
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
        "byte", "sbyte",
        "short", "ushort",
        "int", "uint",
        "long", "ulong",
        "float", "double",
        "string",
        "System.DateOnly",
        "System.TimeOnly",
        "System.Numerics.BigInteger",
        "System.ReadOnlyMemory<byte>",
        "System.ReadOnlyMemory<char>",
        "Parquet.File.Values.Primitives.BigDecimal",
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
        "Parquet.File.Values.Primitives.BigDecimal",
    };

    /// <summary>
    /// Whether a member type has a column representation in the Parquet.Net 4.x/5.x API.
    /// </summary>
    private static bool IsSupportedOnClassicApi(ITypeSymbol underlyingType) =>
        !ClassicApiUnsupportedTypes.Contains(
            underlyingType.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString());

    /// <summary>
    /// Classifies a member's Parquet field kind, returning false when the type has no representation.
    /// </summary>
    private static bool TryClassifyKind(ITypeSymbol underlyingType, ITypeSymbol memberType, out PropertyKind kind)
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
        string fqn = underlyingType.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();

        switch (fqn)
        {
            case "decimal":
                kind = PropertyKind.Decimal;
                return true;
            case "System.DateTime":
                kind = PropertyKind.DateTime;
                return true;
            case "System.TimeSpan":
                kind = PropertyKind.TimeSpan;
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
            return property.SetMethod is not null && IsReachableFromGeneratedCode(property.SetMethod.DeclaredAccessibility);

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
