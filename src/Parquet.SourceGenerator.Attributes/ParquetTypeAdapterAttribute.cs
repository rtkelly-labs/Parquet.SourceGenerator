using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Declares or registers a compile-time type adapter: a static class that converts a domain CLR
/// type the generator has no built-in mapping for into a surrogate type it already understands.
/// </summary>
/// <remarks>
/// The attribute has two forms, mirroring docs/44-TYPE-ADAPTERS.md:
/// <list type="bullet">
/// <item>
/// <description>
/// On the adapter class, <c>[ParquetTypeAdapter(sourceType: typeof(TDomain), surrogateType:
/// typeof(TSurrogate))]</c> is the adapter's descriptor. The class must expose exactly one
/// <c>static TSurrogate ToStorage(TDomain)</c> and one <c>static TDomain FromStorage(TSurrogate)</c>;
/// ordinary static extension methods satisfy that.
/// </description>
/// </item>
/// <item>
/// <description>
/// On an assembly, <c>[assembly: ParquetTypeAdapter(typeof(MyAdapter))]</c> registers that adapter
/// as the default for its source type. An integration package carries these itself, so
/// referencing the package is what makes its adapters discoverable. The generator reads only
/// these registrations — it never scans types looking for conversion methods.
/// </description>
/// </item>
/// </list>
/// <para>
/// Resolution happens entirely at compile time: generated code calls the conversion methods
/// directly, with no reflection and no runtime converter lookup. Adapters never see nulls — a
/// nullable member is unwrapped by the generator, which owns definition levels.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class,
    AllowMultiple = true,
    Inherited = false
)]
public sealed class ParquetTypeAdapterAttribute : Attribute
{
    /// <summary>
    /// Registers <paramref name="adapterType"/> as the default adapter for its source type
    /// (assembly-level form).
    /// </summary>
    /// <param name="adapterType">A static class carrying the descriptor form of this attribute.</param>
    public ParquetTypeAdapterAttribute(Type adapterType)
    {
        AdapterType = adapterType;
    }

    /// <summary>
    /// Describes the conversion an adapter class implements (class-level form).
    /// </summary>
    /// <param name="sourceType">The exact, closed domain type the adapter handles.</param>
    /// <param name="surrogateType">
    /// The representation written to Parquet: a type the generator maps natively, or a struct or
    /// class whose public members it maps recursively as a group.
    /// </param>
    public ParquetTypeAdapterAttribute(Type sourceType, Type surrogateType)
    {
        SourceType = sourceType;
        SurrogateType = surrogateType;
    }

    /// <summary>Gets the registered adapter class (assembly-level form only).</summary>
    public Type? AdapterType { get; }

    /// <summary>Gets the domain type the adapter converts from (class-level form only).</summary>
    public Type? SourceType { get; }

    /// <summary>Gets the storage representation the adapter converts to (class-level form only).</summary>
    public Type? SurrogateType { get; }

    /// <summary>
    /// Gets or sets the adapter contract version the class was written against. Only version
    /// <c>1</c> exists; a newer value is rejected at compile time rather than misinterpreted.
    /// </summary>
    public int ContractVersion { get; set; } = 1;
}

/// <summary>
/// Selects an adapter for one member explicitly. This is the highest-precedence resolution: it
/// overrides both registered defaults and the generator's own built-in mapping, and is the only
/// way to change how a built-in type such as <see cref="DateTime"/> is stored.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true
)]
public sealed class ParquetAdapterAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="ParquetAdapterAttribute"/>.
    /// </summary>
    /// <param name="adapterType">A static class carrying <see cref="ParquetTypeAdapterAttribute"/>.</param>
    public ParquetAdapterAttribute(Type adapterType)
    {
        AdapterType = adapterType;
    }

    /// <summary>Gets the adapter class selected for this member.</summary>
    public Type AdapterType { get; }
}
