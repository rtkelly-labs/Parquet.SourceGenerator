using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Parquet.SourceGenerator.Parser;

/// <summary>
/// An adapter class as the parser sees it once its descriptor has been read and checked.
/// </summary>
/// <remarks>
/// Holds Roslyn symbols, so it lives only for the duration of a parse (and in the per-assembly
/// registry memo below). Nothing here reaches the value-equatable model: the parser copies the
/// fully-qualified names it needs into <see cref="Models.AdapterShadowModel"/> (docs/44 §17).
/// </remarks>
internal sealed class AdapterDescriptor
{
    public AdapterDescriptor(INamedTypeSymbol adapter)
    {
        Adapter = adapter;
    }

    public INamedTypeSymbol Adapter { get; }

    public ITypeSymbol? Source { get; set; }

    public ITypeSymbol? Surrogate { get; set; }

    public string ToStorageMethod { get; set; } = TypeAdapterResolver.ToStorageName;

    public string FromStorageMethod { get; set; } = TypeAdapterResolver.FromStorageName;

    /// <summary>Why the adapter cannot be used, or null when it is well-formed.</summary>
    public string? Error { get; set; }

    public string DisplayName => Adapter.ToDisplayString();

    public string QualifiedName =>
        Adapter.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}

/// <summary>How a member's adapter was chosen, in precedence order (docs/44 §11).</summary>
internal enum AdapterOrigin
{
    /// <summary>No adapter applies; the ordinary classification path runs.</summary>
    None,

    /// <summary><c>[ParquetAdapter]</c> on the member itself.</summary>
    Member,

    /// <summary>A registration on the consuming assembly.</summary>
    Project,

    /// <summary>A registration carried by a referenced assembly (an adapter package).</summary>
    Package,
}

/// <summary>
/// The outcome of resolving one member: the adapter to use, or the diagnostic that explains why
/// resolution failed or was declined.
/// </summary>
internal readonly struct AdapterResolution
{
    public AdapterResolution(
        AdapterOrigin origin,
        AdapterDescriptor? adapter,
        string? ambiguity,
        AdapterDescriptor? ignoredBuiltInOverride
    )
    {
        Origin = origin;
        Adapter = adapter;
        Ambiguity = ambiguity;
        IgnoredBuiltInOverride = ignoredBuiltInOverride;
    }

    public AdapterOrigin Origin { get; }

    public AdapterDescriptor? Adapter { get; }

    /// <summary>The competing registrations, comma-separated, when resolution was ambiguous.</summary>
    public string? Ambiguity { get; }

    /// <summary>A project registration that was ignored because the type is built-in.</summary>
    public AdapterDescriptor? IgnoredBuiltInOverride { get; }

    public static AdapterResolution NoAdapter { get; } = new(AdapterOrigin.None, null, null, null);
}

/// <summary>
/// Discovers, validates and selects compile-time type adapters (docs/44-TYPE-ADAPTERS.md).
/// </summary>
/// <remarks>
/// Discovery reads <c>[assembly: ParquetTypeAdapter(typeof(...))]</c> on the consuming assembly
/// and on its references — never a scan of the types inside them. The registry is memoised per
/// consuming assembly symbol, which is new for every compilation, so a reference change always
/// rebuilds it and nothing survives into the incremental cache.
/// </remarks>
internal static class TypeAdapterResolver
{
    public const string ToStorageName = "ToStorage";
    public const string FromStorageName = "FromStorage";
    public const int SupportedContractVersion = 1;

    private const string TypeAdapterAttributeFullName =
        "Parquet.SourceGenerator.ParquetTypeAdapterAttribute";
    private const string MemberAdapterAttributeFullName =
        "Parquet.SourceGenerator.ParquetAdapterAttribute";

    private static readonly ConditionalWeakTable<IAssemblySymbol, Registry> Registries = new();

    /// <summary>
    /// Selects the adapter for a member whose (nullable-unwrapped) type is
    /// <paramref name="underlyingType"/>.
    /// </summary>
    /// <param name="member">The property or field being classified.</param>
    /// <param name="underlyingType">The member type with <c>Nullable&lt;T&gt;</c> removed.</param>
    /// <param name="builtIn">Whether the generator already maps the type itself.</param>
    public static AdapterResolution Resolve(
        ISymbol member,
        ITypeSymbol underlyingType,
        bool builtIn
    )
    {
        if (FindMemberAdapter(member) is { } explicitAdapter)
        {
            AdapterDescriptor descriptor = Describe(explicitAdapter, member.ContainingAssembly);
            if (
                descriptor.Error is null
                && !SymbolEqualityComparer.Default.Equals(descriptor.Source, underlyingType)
            )
            {
                descriptor = WithError(
                    descriptor,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "it converts '{0}', but the member's type is '{1}'",
                        descriptor.Source?.ToDisplayString(),
                        underlyingType.ToDisplayString()
                    )
                );
            }

            return new AdapterResolution(AdapterOrigin.Member, descriptor, null, null);
        }

        IAssemblySymbol? consumer = member.ContainingAssembly;
        if (consumer is null)
            return AdapterResolution.NoAdapter;

        Registry registry = Registries.GetValue(consumer, Registry.Build);
        List<AdapterDescriptor>? project = registry.ProjectFor(underlyingType);

        if (builtIn)
        {
            // A registration can make an unsupported type work; it cannot silently change how a
            // type the generator already maps is stored (docs/44 §11). Package defaults for a
            // built-in type lose quietly — they were never the consumer's intent.
            return project is { Count: > 0 }
                ? new AdapterResolution(AdapterOrigin.None, null, null, project[0])
                : AdapterResolution.NoAdapter;
        }

        if (project is { Count: > 0 })
            return Select(AdapterOrigin.Project, project);

        List<AdapterDescriptor>? package = registry.PackageFor(underlyingType);
        return package is { Count: > 0 }
            ? Select(AdapterOrigin.Package, package)
            : AdapterResolution.NoAdapter;
    }

    private static AdapterResolution Select(AdapterOrigin origin, List<AdapterDescriptor> found)
    {
        if (found.Count == 1)
            return new AdapterResolution(origin, found[0], null, null);

        // Never chosen by reference order or assembly name (docs/44 §12).
        string names = string.Join(
            ", ",
            found.Select(d => d.DisplayName).OrderBy(n => n, StringComparer.Ordinal)
        );
        return new AdapterResolution(origin, null, names, null);
    }

    private static INamedTypeSymbol? FindMemberAdapter(ISymbol member)
    {
        foreach (AttributeData attribute in member.GetAttributes())
        {
            if (
                attribute.AttributeClass?.ToDisplayString() == MemberAdapterAttributeFullName
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is INamedTypeSymbol adapter
            )
            {
                return adapter;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads and validates an adapter class's descriptor and conversion methods. An invalid
    /// adapter is returned with <see cref="AdapterDescriptor.Error"/> set rather than dropped, so
    /// the member that selected it can say exactly what is wrong.
    /// </summary>
    public static AdapterDescriptor Describe(INamedTypeSymbol adapter, IAssemblySymbol? consumer)
    {
        var descriptor = new AdapterDescriptor(adapter);

        string? error =
            ReadDescriptorAttribute(adapter, descriptor)
            ?? CheckAccessible(adapter, consumer)
            ?? CheckTypes(descriptor);
        if (error is not null)
            return WithError(descriptor, error);

        error =
            FindConversion(
                adapter,
                ToStorageName,
                descriptor.Source!,
                descriptor.Surrogate!,
                consumer
            )
            ?? FindConversion(
                adapter,
                FromStorageName,
                descriptor.Surrogate!,
                descriptor.Source!,
                consumer
            );
        return error is null ? descriptor : WithError(descriptor, error);
    }

    private static AdapterDescriptor WithError(AdapterDescriptor descriptor, string error)
    {
        descriptor.Error = error;
        return descriptor;
    }

    private static string? ReadDescriptorAttribute(
        INamedTypeSymbol adapter,
        AdapterDescriptor descriptor
    )
    {
        AttributeData? found = null;
        foreach (AttributeData attribute in adapter.GetAttributes())
        {
            if (
                attribute.AttributeClass?.ToDisplayString() != TypeAdapterAttributeFullName
                || attribute.ConstructorArguments.Length != 2
            )
            {
                continue;
            }

            if (found is not null)
                return "it carries more than one [ParquetTypeAdapter(sourceType, surrogateType)] descriptor";
            found = attribute;
        }

        if (found is null)
        {
            return "it does not carry a [ParquetTypeAdapter(sourceType, surrogateType)] descriptor";
        }

        foreach (KeyValuePair<string, TypedConstant> named in found.NamedArguments)
        {
            if (
                named.Key == "ContractVersion"
                && named.Value.Value is int version
                && version != SupportedContractVersion
            )
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "it declares adapter contract version {0}, but this generator supports version {1}",
                    version,
                    SupportedContractVersion
                );
            }
        }

        descriptor.Source = found.ConstructorArguments[0].Value as ITypeSymbol;
        descriptor.Surrogate = found.ConstructorArguments[1].Value as ITypeSymbol;
        return null;
    }

    private static string? CheckTypes(AdapterDescriptor descriptor)
    {
        ITypeSymbol? source = descriptor.Source;
        ITypeSymbol? surrogate = descriptor.Surrogate;
        if (
            source is null
            || surrogate is null
            || source.TypeKind == TypeKind.Error
            || surrogate.TypeKind == TypeKind.Error
        )
        {
            return "its source or surrogate type does not resolve";
        }

        if (IsNullableValueType(source) || IsNullableValueType(surrogate))
        {
            return "its source and surrogate types must not be Nullable<T>; the generator unwraps nullable members before calling an adapter";
        }

        if (IsOpenGeneric(source) || IsOpenGeneric(surrogate))
            return "its source and surrogate types must be closed types";

        if (SymbolEqualityComparer.Default.Equals(source, surrogate))
            return "it maps a type to itself";

        return null;
    }

    private static string? CheckAccessible(INamedTypeSymbol adapter, IAssemblySymbol? consumer)
    {
        if (adapter.IsGenericType)
            return "adapter classes must not be generic";

        for (INamedTypeSymbol? t = adapter; t is not null; t = t.ContainingType)
        {
            if (!IsAccessible(t.DeclaredAccessibility, t.ContainingAssembly, consumer))
                return "it is not accessible from the generated code";
        }

        return null;
    }

    /// <summary>
    /// Finds the single static conversion <paramref name="name"/>(<paramref name="from"/>) →
    /// <paramref name="to"/>. Ordinary static and static extension methods both qualify.
    /// </summary>
    private static string? FindConversion(
        INamedTypeSymbol adapter,
        string name,
        ITypeSymbol from,
        ITypeSymbol to,
        IAssemblySymbol? consumer
    )
    {
        int matches = 0;
        foreach (ISymbol candidate in adapter.GetMembers(name))
        {
            if (
                candidate
                    is IMethodSymbol
                    {
                        IsStatic: true,
                        MethodKind: MethodKind.Ordinary,
                        IsGenericMethod: false,
                        Parameters.Length: 1,
                    } method
                && method.Parameters[0].RefKind is RefKind.None or RefKind.In
                && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, from)
                && SymbolEqualityComparer.Default.Equals(method.ReturnType, to)
                && IsAccessible(method.DeclaredAccessibility, adapter.ContainingAssembly, consumer)
            )
            {
                matches++;
            }
        }

        if (matches == 1)
            return null;

        return string.Format(
            CultureInfo.InvariantCulture,
            matches == 0
                ? "it declares no accessible 'static {0} {1}({2})' conversion"
                : "it declares more than one 'static {0} {1}({2})' conversion",
            to.ToDisplayString(),
            name,
            from.ToDisplayString()
        );
    }

    private static bool IsAccessible(
        Accessibility accessibility,
        IAssemblySymbol? declaring,
        IAssemblySymbol? consumer
    ) =>
        accessibility == Accessibility.Public
        || (
            accessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal
            && SymbolEqualityComparer.Default.Equals(declaring, consumer)
        );

    private static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } generic
        && generic.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

    private static bool IsOpenGeneric(ITypeSymbol type) =>
        type is ITypeParameterSymbol
        || (type is INamedTypeSymbol named && named.IsUnboundGenericType);

    /// <summary>
    /// The default registrations visible to one consuming assembly, keyed by source type.
    /// Registrations whose descriptor cannot even name a source type are unreachable by any
    /// member, so they are dropped here; everything else — valid or not — is kept so that a
    /// broken default is reported where it is used.
    /// </summary>
    private sealed class Registry
    {
        private readonly Dictionary<ITypeSymbol, List<AdapterDescriptor>> _project = new(
            SymbolEqualityComparer.Default
        );
        private readonly Dictionary<ITypeSymbol, List<AdapterDescriptor>> _package = new(
            SymbolEqualityComparer.Default
        );

        public List<AdapterDescriptor>? ProjectFor(ITypeSymbol source) =>
            _project.TryGetValue(source, out List<AdapterDescriptor>? found) ? found : null;

        public List<AdapterDescriptor>? PackageFor(ITypeSymbol source) =>
            _package.TryGetValue(source, out List<AdapterDescriptor>? found) ? found : null;

        public static Registry Build(IAssemblySymbol consumer)
        {
            var registry = new Registry();
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            Add(registry._project, consumer, consumer, seen);
            foreach (IModuleSymbol module in consumer.Modules)
            {
                foreach (IAssemblySymbol reference in module.ReferencedAssemblySymbols)
                    Add(registry._package, reference, consumer, seen);
            }

            return registry;
        }

        private static void Add(
            Dictionary<ITypeSymbol, List<AdapterDescriptor>> target,
            IAssemblySymbol assembly,
            IAssemblySymbol consumer,
            HashSet<INamedTypeSymbol> seen
        )
        {
            foreach (AttributeData attribute in assembly.GetAttributes())
            {
                if (
                    attribute.AttributeClass?.ToDisplayString() != TypeAdapterAttributeFullName
                    || attribute.ConstructorArguments.Length != 1
                    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol adapter
                    || !seen.Add(adapter)
                )
                {
                    continue;
                }

                AdapterDescriptor descriptor = Describe(adapter, consumer);
                if (descriptor.Source is null)
                    continue;

                if (!target.TryGetValue(descriptor.Source, out List<AdapterDescriptor>? list))
                {
                    list = [];
                    target[descriptor.Source] = list;
                }

                list.Add(descriptor);
            }
        }
    }
}
