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
        isEnabledByDefault: true);

    /// <summary>
    /// PARQ002: Duplicate Parquet column name detected.
    /// </summary>
    public static readonly DiagnosticDescriptor DuplicateColumnName = new(
        id: "PARQ002",
        title: "Duplicate Parquet column name detected",
        messageFormat: "The Parquet column name '{0}' is specified multiple times on type '{1}'",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// PARQ003: No public serializable properties found.
    /// </summary>
    public static readonly DiagnosticDescriptor NoPropertiesFound = new(
        id: "PARQ003",
        title: "No public serializable properties found",
        messageFormat: "The type '{0}' is decorated with [ParquetSerializable] but has no public serializable properties or fields",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// PARQ004: Non-public property decorated with [ParquetColumn] will be ignored.
    /// </summary>
    public static readonly DiagnosticDescriptor NonPublicPropertyIgnored = new(
        id: "PARQ004",
        title: "Non-public property ignored",
        messageFormat: "The property '{0}' on type '{1}' is decorated with [ParquetColumn] but is not public and will be ignored",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// PARQ005: Invalid ParquetDecimal precision or scale configuration.
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidDecimalPrecisionScale = new(
        id: "PARQ005",
        title: "Invalid ParquetDecimal precision or scale",
        messageFormat: "Invalid ParquetDecimal on property '{0}': precision ({1}) must be >= scale ({2}) and <= 38",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

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
        isEnabledByDefault: true);

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
        isEnabledByDefault: true);

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
        isEnabledByDefault: true);

    /// <summary>
    /// PARQ009: Nested target types are not supported.
    /// </summary>
    /// <remarks>
    /// The extension class is emitted at namespace scope and refers to the target by its bare name,
    /// which does not resolve for a nested type; the hint name is namespace + type name too, so two
    /// same-named nested types in one namespace also collided. Rejected rather than half-supported
    /// until the emitter carries the full containing-type path.
    /// </remarks>
    public static readonly DiagnosticDescriptor NestedTypeNotSupported = new(
        id: "PARQ009",
        title: "Nested type cannot be Parquet-serializable",
        messageFormat: "The type '{0}' is nested inside '{1}'. [ParquetSerializable] supports top-level types only — move it to namespace scope",
        category: "ParquetSourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

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
        isEnabledByDefault: true);

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
        isEnabledByDefault: true);
}
