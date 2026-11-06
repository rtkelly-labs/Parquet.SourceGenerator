using Microsoft.CodeAnalysis;

namespace Parquet.SourceGenerator.Diagnostics;

/// <summary>
/// Defines Roslyn diagnostic rules for compile-time validation of [ParquetSerializable] types.
/// </summary>
public static class DiagnosticDescriptors
{
    /// <summary>
    /// PARQ001: Target type decorated with [ParquetSerializable] must be partial.
    /// </summary>
    public static readonly DiagnosticDescriptor MustBePartial = new(
        id: "PARQ001",
        title: "Type decorated with [ParquetSerializable] must be partial",
        messageFormat: "The type '{0}' is decorated with [ParquetSerializable] but is not declared as partial",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ002: Duplicate Parquet column name detected.
    /// </summary>
    public static readonly DiagnosticDescriptor DuplicateColumnName = new(
        id: "PARQ002",
        title: "Duplicate Parquet column name detected",
        messageFormat: "The Parquet column name '{0}' is specified multiple times on type '{1}'",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ003: No public serializable properties found.
    /// </summary>
    public static readonly DiagnosticDescriptor NoPropertiesFound = new(
        id: "PARQ003",
        title: "No public serializable properties found",
        messageFormat: "The type '{0}' is decorated with [ParquetSerializable] but has no public serializable properties or fields",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ004: Non-public property decorated with [ParquetColumn] will be ignored.
    /// </summary>
    public static readonly DiagnosticDescriptor NonPublicPropertyIgnored = new(
        id: "PARQ004",
        title: "Non-public property ignored",
        messageFormat: "The property '{0}' on type '{1}' is decorated with [ParquetColumn] but is not public and will be ignored",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ005: Invalid ParquetDecimal precision or scale configuration.
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidDecimalPrecisionScale = new(
        id: "PARQ005",
        title: "Invalid ParquetDecimal precision or scale",
        messageFormat: "Invalid ParquetDecimal on property '{0}': precision ({1}) must be >= scale ({2}) and <= 38",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ006: Property or field type has no Parquet column representation.
    /// </summary>
    /// <remarks>
    /// The allowed set mirrors <c>Parquet.Encodings.SchemaEncoder.SupportedTypes</c>, so this only
    /// rejects what Parquet.Net itself rejects. Without it, an unsupported type produced a schema
    /// the library refused at runtime — a stack trace from inside Parquet.Net, a long way from the
    /// property that caused it.
    /// </remarks>
    public static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        id: "PARQ006",
        title: "Unsupported Parquet property type",
        messageFormat: "The member '{0}' on type '{2}' has type '{1}', which has no Parquet column representation. Remove it, mark it [ParquetIgnore], or change its type",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ007: Member cannot be assigned by the generated deserializer.
    /// </summary>
    /// <remarks>
    /// The read path materialises through an object initializer, so every column needs a setter (or
    /// <c>init</c>) reachable from the generated extension class. A get-only property or readonly
    /// field previously produced CS0200/CS0191 inside generated code, with nothing pointing back at
    /// the declaration responsible.
    /// </remarks>
    public static readonly DiagnosticDescriptor MemberNotAssignable = new(
        id: "PARQ007",
        title: "Parquet member is not assignable",
        messageFormat: "The member '{0}' on type '{1}' cannot be assigned by the generated deserializer. Give it an accessible set or init accessor, or mark it [ParquetIgnore]",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ008: Reference type has no accessible parameterless constructor.
    /// </summary>
    /// <remarks>
    /// Covers positional records (<c>record Person(int Id, string Name)</c>) and any class whose
    /// only constructors take arguments. The generated reader uses an object initializer, which
    /// needs one; without this the emitted code failed with CS7036.
    /// </remarks>
    public static readonly DiagnosticDescriptor NoParameterlessConstructor = new(
        id: "PARQ008",
        title: "Parquet type has no accessible parameterless constructor",
        messageFormat: "The type '{0}' has no accessible parameterless constructor, so the generated deserializer cannot construct it. Add one, or declare the columns as settable members instead of primary constructor parameters",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ009: A nested target type is unreachable from the generated extension class.
    /// </summary>
    /// <remarks>
    /// Nested <c>[ParquetSerializable]</c> declarations are supported (issue #176 reversed the
    /// original blanket rejection: the extension class and the hint name now carry the full
    /// containing-type path). What cannot work is a type the generated namespace-scope class
    /// cannot name — a <c>private</c> nested declaration — so that narrow case keeps the id.
    /// </remarks>
    public static readonly DiagnosticDescriptor NestedTypeNotSupported = new(
        id: "PARQ009",
        title: "Nested type is not accessible to generated code",
        messageFormat: "The type '{0}' is nested inside '{1}' with accessibility '{2}', which generated code cannot reach. Give it internal or public accessibility",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ010: Generic target types are not supported.
    /// </summary>
    /// <remarks>
    /// The emitted schema is a single <c>static readonly</c> field, so it cannot vary per type
    /// argument, and the emitter wrote the type name without its parameters — producing code that
    /// did not compile.
    /// </remarks>
    public static readonly DiagnosticDescriptor GenericTypeNotSupported = new(
        id: "PARQ010",
        title: "Generic type cannot be Parquet-serializable",
        messageFormat: "The type '{0}' is generic. [ParquetSerializable] supports non-generic types only, because the emitted schema is a single static field and cannot vary by type argument",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ011: The member's type is supported by Parquet.Net 6 but not by the 4.x/5.x API.
    /// </summary>
    /// <remarks>
    /// Reported only by the classic (v4/v5) backend. It is deliberately distinct from PARQ006: the
    /// member is perfectly representable in Parquet, just not by the API generation this package
    /// targets, so the fix is to switch packages rather than to change the model.
    /// </remarks>
    public static readonly DiagnosticDescriptor TypeUnsupportedOnClassicApi = new(
        id: "PARQ011",
        title: "Property type is not supported by the Parquet.Net 4.x/5.x API",
        messageFormat: "The member '{0}' on type '{2}' has type '{1}', which Parquet.Net 6 supports but the 4.x/5.x API does not. Use the Parquet.SourceGenerator package instead, or change its type",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ012: A compound member closes a type cycle, which cannot be flattened to columns.
    /// </summary>
    /// <remarks>
    /// Parquet schemas are trees; a type that reaches itself through struct members or list
    /// elements has no finite column layout. Without this rule the parser would recurse until
    /// the stack gave out and the build failed with CS8785 and no pointer to the declaration
    /// responsible.
    /// </remarks>
    public static readonly DiagnosticDescriptor NestedTypeCycleDetected = new(
        id: "PARQ012",
        title: "Cyclic compound type cannot be Parquet-serializable",
        messageFormat: "The member '{0}' on type '{1}' creates a reference cycle back to '{2}'. Parquet cannot flatten a self-referencing type — give the cyclic member [ParquetIgnore] or break the cycle",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ013: Compound nesting exceeds the depth the emitter will expand.
    /// </summary>
    /// <remarks>
    /// Record-shredding is unrolled per leaf at generation time, so emitted-code size grows with
    /// the leaf count and the row-path depth. Six levels is generous for real domain models and
    /// keeps pathological recursion from emitting megabyte-scale source files.
    /// </remarks>
    public static readonly DiagnosticDescriptor NestedTypeTooDeep = new(
        id: "PARQ013",
        title: "Compound nesting exceeds the supported depth",
        messageFormat: "The member '{0}' on type '{1}' nests compound types {2} levels deep, exceeding the maximum of {3}. Flatten the model or mark the deep member [ParquetIgnore]",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// PARQ014: A member marked <c>[ParquetSortKey]</c> cannot drive row-group pruning.
    /// </summary>
    /// <remarks>
    /// Pruning binary searches the footer <c>[Min, Max]</c> statistics, which only works for a
    /// flat, non-nullable root column whose Parquet statistics order agrees with
    /// <c>Comparer&lt;T&gt;.Default</c>. Silently emitting nothing for an ineligible marker would
    /// leave the author expecting a lookup method that never appeared, so the marker is reported
    /// with the reason instead.
    /// </remarks>
    public static readonly DiagnosticDescriptor SortKeyNotEligible = new(
        id: "PARQ014",
        title: "Member cannot be a Parquet sort key",
        messageFormat: "The member '{0}' on type '{1}' is marked [ParquetSortKey] but cannot drive row-group pruning: {2}",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
}
