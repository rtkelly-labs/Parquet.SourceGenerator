using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

    /// <summary>
    /// True for an open generic descriptor (<c>typeof(Id&lt;&gt;)</c>): the adapter serves every
    /// closed construction of the source definition and is closed per member by
    /// <see cref="TypeAdapterResolver.Close"/>. Its conversions are validated at that point,
    /// because only then are the type arguments known.
    /// </summary>
    public bool IsGenericDefinition { get; set; }

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
            if (descriptor.Error is not null)
                return new AdapterResolution(AdapterOrigin.Member, descriptor, null, null);

            if (Matches(descriptor, underlyingType))
            {
                return new AdapterResolution(
                    AdapterOrigin.Member,
                    Close(descriptor, underlyingType, member.ContainingAssembly),
                    null,
                    null
                );
            }

            // On a collection member the explicit adapter names the element type: the list
            // planner applies it per element, not to the collection itself.
            if (
                TryGetCollectionElement(underlyingType, out ITypeSymbol element)
                && Matches(descriptor, UnwrapNullable(element))
            )
            {
                return AdapterResolution.NoAdapter;
            }

            descriptor = WithError(
                descriptor,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "it converts '{0}', but the member's type is '{1}'",
                    descriptor.Source?.ToDisplayString(),
                    underlyingType.ToDisplayString()
                )
            );
            return new AdapterResolution(AdapterOrigin.Member, descriptor, null, null);
        }

        return ResolveDefault(member.ContainingAssembly, underlyingType, builtIn);
    }

    /// <summary>
    /// Resolves the adapter for a collection member's element type (docs/44 §A.4): an explicit
    /// <c>[ParquetAdapter]</c> on the member whose source is the element type, else the
    /// registered defaults. Built-in element types are adapted only explicitly.
    /// </summary>
    public static AdapterResolution ResolveElement(
        ISymbol member,
        ITypeSymbol elementType,
        bool builtIn
    )
    {
        if (FindMemberAdapter(member) is { } explicitAdapter)
        {
            AdapterDescriptor descriptor = Describe(explicitAdapter, member.ContainingAssembly);
            return descriptor.Error is null && Matches(descriptor, elementType)
                ? new AdapterResolution(
                    AdapterOrigin.Member,
                    Close(descriptor, elementType, member.ContainingAssembly),
                    null,
                    null
                )
                : AdapterResolution.NoAdapter;
        }

        return builtIn
            ? AdapterResolution.NoAdapter
            : ResolveDefault(member.ContainingAssembly, elementType, builtIn: false);
    }

    private static AdapterResolution ResolveDefault(
        IAssemblySymbol? consumer,
        ITypeSymbol underlyingType,
        bool builtIn
    )
    {
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
            return Select(AdapterOrigin.Project, project, underlyingType, consumer);

        List<AdapterDescriptor>? package = registry.PackageFor(underlyingType);
        return package is { Count: > 0 }
            ? Select(AdapterOrigin.Package, package, underlyingType, consumer)
            : AdapterResolution.NoAdapter;
    }

    private static AdapterResolution Select(
        AdapterOrigin origin,
        List<AdapterDescriptor> found,
        ITypeSymbol source,
        IAssemblySymbol consumer
    )
    {
        if (found.Count == 1)
            return new AdapterResolution(origin, Close(found[0], source, consumer), null, null);

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

        if (descriptor.IsGenericDefinition)
            return descriptor;

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

        if (source is ITypeParameterSymbol || surrogate is ITypeParameterSymbol)
            return "its source and surrogate types must be named types";

        if (SymbolEqualityComparer.Default.Equals(source, surrogate))
            return "it maps a type to itself";

        if (source is INamedTypeSymbol { IsUnboundGenericType: true } definition)
            return CheckGenericShape(descriptor, definition, surrogate);

        if (IsOpenGeneric(surrogate))
        {
            return "an open generic surrogate needs an open generic source of the same arity to take its type arguments from";
        }

        if (descriptor.Adapter.IsGenericType)
        {
            return "a generic adapter class needs an open generic source type, e.g. typeof(Id<>), to take its type arguments from";
        }

        return null;
    }

    /// <summary>
    /// Shape rules for an open generic descriptor: the type arguments come from the member's
    /// closed source type and reach the conversions either through a generic adapter class of
    /// the same arity (<c>IdAdapter&lt;T&gt;</c>) or through generic conversion methods of that
    /// arity (<c>ToStorage&lt;T&gt;(Id&lt;T&gt;)</c>). An open surrogate is constructed with the same
    /// arguments.
    /// </summary>
    private static string? CheckGenericShape(
        AdapterDescriptor descriptor,
        INamedTypeSymbol definition,
        ITypeSymbol surrogate
    )
    {
        int arity = definition.Arity;
        if (
            surrogate is INamedTypeSymbol { IsUnboundGenericType: true } openSurrogate
            && openSurrogate.Arity != arity
        )
        {
            return "an open generic surrogate must have the same number of type parameters as the source";
        }

        INamedTypeSymbol adapter = descriptor.Adapter;
        if (adapter.IsGenericType && adapter.Arity != arity)
        {
            return "a generic adapter class must have the same number of type parameters as its source type";
        }

        if (adapter.ContainingType?.IsGenericType == true)
            return "adapter classes must not be nested in generic types";

        descriptor.IsGenericDefinition = true;
        return null;
    }

    /// <summary>
    /// Whether an adapter serves <paramref name="type"/>: exactly its source, or — for an open
    /// generic descriptor — any closed construction of the source definition.
    /// </summary>
    public static bool Matches(AdapterDescriptor descriptor, ITypeSymbol type)
    {
        if (!descriptor.IsGenericDefinition)
            return SymbolEqualityComparer.Default.Equals(descriptor.Source, type);

        return type is INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } named
            && SymbolEqualityComparer.Default.Equals(
                named.OriginalDefinition,
                descriptor.Source!.OriginalDefinition
            );
    }

    /// <summary>
    /// Closes an open generic descriptor over the member's constructed source type: constructs
    /// the adapter class or the conversion methods with its type arguments, checks their
    /// constraints, derives the closed surrogate, and validates the closed conversions exactly as
    /// a non-generic adapter's are. Returns a new descriptor; the registry's copy is untouched.
    /// </summary>
    public static AdapterDescriptor Close(
        AdapterDescriptor descriptor,
        ITypeSymbol source,
        IAssemblySymbol? consumer
    )
    {
        if (!descriptor.IsGenericDefinition || descriptor.Error is not null)
            return descriptor;

        var named = (INamedTypeSymbol)source;
        ITypeSymbol[] arguments = [.. named.TypeArguments];
        // typeof(TaggedAdapter<>) binds to the unbound form, which Roslyn treats as already
        // constructed; construction has to start from the definition.
        INamedTypeSymbol open = descriptor.Adapter.OriginalDefinition;
        INamedTypeSymbol adapter = open.IsGenericType ? open.Construct(arguments) : open;

        ITypeSymbol surrogate = descriptor.Surrogate
            is INamedTypeSymbol { IsUnboundGenericType: true } openSurrogate
            ? openSurrogate.OriginalDefinition.Construct(arguments)
            : descriptor.Surrogate!;

        var closed = new AdapterDescriptor(adapter) { Source = source, Surrogate = surrogate };

        if (open.IsGenericType)
        {
            string? error =
                CheckConstraints(open.TypeParameters, arguments, open.ToDisplayString())
                ?? FindConversion(adapter, ToStorageName, source, surrogate, consumer)
                ?? FindConversion(adapter, FromStorageName, surrogate, source, consumer);
            return error is null ? closed : WithError(closed, error);
        }

        string? methodError =
            FindGenericConversion(closed, ToStorageName, source, surrogate, arguments, consumer)
            ?? FindGenericConversion(
                closed,
                FromStorageName,
                surrogate,
                source,
                arguments,
                consumer
            );
        return methodError is null ? closed : WithError(closed, methodError);
    }

    /// <summary>
    /// The single generic conversion <paramref name="name"/>&lt;T...&gt; of the descriptor's arity
    /// that, constructed with <paramref name="arguments"/>, maps <paramref name="from"/> to
    /// <paramref name="to"/>. Records the constructed call spelling on the descriptor.
    /// </summary>
    private static string? FindGenericConversion(
        AdapterDescriptor closed,
        string name,
        ITypeSymbol from,
        ITypeSymbol to,
        ITypeSymbol[] arguments,
        IAssemblySymbol? consumer
    )
    {
        IMethodSymbol? match = null;
        int matches = 0;
        foreach (ISymbol candidate in closed.Adapter.GetMembers(name))
        {
            if (
                candidate
                    is not IMethodSymbol
                    {
                        IsStatic: true,
                        MethodKind: MethodKind.Ordinary,
                        IsGenericMethod: true,
                        Parameters.Length: 1,
                    } method
                || method.Arity != arguments.Length
                || !IsAccessible(
                    method.DeclaredAccessibility,
                    closed.Adapter.ContainingAssembly,
                    consumer
                )
            )
            {
                continue;
            }

            IMethodSymbol constructed = method.Construct(arguments);
            if (
                constructed.Parameters[0].RefKind is RefKind.None or RefKind.In
                && SymbolEqualityComparer.Default.Equals(constructed.Parameters[0].Type, from)
                && SymbolEqualityComparer.Default.Equals(constructed.ReturnType, to)
            )
            {
                match = method;
                matches++;
            }
        }

        if (matches != 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                matches == 0
                    ? "it declares no accessible generic 'static {0} {1}<...>({2})' conversion for this type"
                    : "it declares more than one generic 'static {0} {1}<...>({2})' conversion for this type",
                to.ToDisplayString(),
                name,
                from.ToDisplayString()
            );
        }

        string? constraintError = CheckConstraints(
            match!.TypeParameters,
            arguments,
            match.ToDisplayString()
        );
        if (constraintError is not null)
            return constraintError;

        string call =
            name
            + "<"
            + string.Join(
                ", ",
                arguments.Select(a => a.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            )
            + ">";
        if (name == ToStorageName)
            closed.ToStorageMethod = call;
        else
            closed.FromStorageMethod = call;
        return null;
    }

    /// <summary>
    /// The constraint checks the compiler would apply to the constructed call, so an argument
    /// that violates them is a PARQ016 at the member rather than a compile error in generated
    /// code. Constraint types that themselves mention type parameters are left to the compiler.
    /// </summary>
    private static string? CheckConstraints(
        ImmutableArray<ITypeParameterSymbol> parameters,
        ITypeSymbol[] arguments,
        string owner
    )
    {
        for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
        {
            ITypeParameterSymbol p = parameters[i];
            ITypeSymbol a = arguments[i];
            if (!SatisfiesConstraints(p, a))
            {
                return "the type argument '"
                    + a.ToDisplayString()
                    + "' does not satisfy the constraints on '"
                    + p.Name
                    + "' of '"
                    + owner
                    + "'";
            }
        }

        return null;
    }

    private static bool SatisfiesConstraints(ITypeParameterSymbol p, ITypeSymbol a) =>
        (!p.HasReferenceTypeConstraint || a.IsReferenceType)
        && (!p.HasValueTypeConstraint || (a.IsValueType && !IsNullableValueType(a)))
        && (!p.HasUnmanagedTypeConstraint || a.IsUnmanagedType)
        && (!p.HasConstructorConstraint || HasPublicParameterlessConstructor(a))
        && p.ConstraintTypes.All(c => MentionsTypeParameter(c) || IsAssignableTo(a, c));

    private static bool HasPublicParameterlessConstructor(ITypeSymbol type) =>
        type.IsValueType
        || (
            type is INamedTypeSymbol { IsAbstract: false } named
            && named.InstanceConstructors.Any(c =>
                c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public
            )
        );

    private static bool MentionsTypeParameter(ITypeSymbol type) =>
        type is ITypeParameterSymbol
        || (type is INamedTypeSymbol named && named.TypeArguments.Any(MentionsTypeParameter));

    private static bool IsAssignableTo(ITypeSymbol type, ITypeSymbol target)
    {
        if (target.SpecialType == SpecialType.System_Object)
            return true;
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, target))
                return true;
        }

        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target));
    }

    /// <summary>
    /// The element type of a collection shape the list emitter reconstructs (single-dimension
    /// arrays other than <c>byte[]</c>, and the <c>List</c> / <c>IList</c> /
    /// <c>IEnumerable</c> family), or false.
    /// </summary>
    public static bool TryGetCollectionElement(ITypeSymbol type, out ITypeSymbol element)
    {
        if (
            type is IArrayTypeSymbol { Rank: 1 } array
            && array.ElementType.SpecialType != SpecialType.System_Byte
        )
        {
            element = array.ElementType;
            return true;
        }

        if (
            type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic
            && Array.IndexOf(
                CollectionDefinitions,
                generic.ConstructedFrom.ToDisplayString().Split('<')[0]
            ) >= 0
        )
        {
            element = generic.TypeArguments[0];
            return true;
        }

        element = type;
        return false;
    }

    /// <summary>Generic collection definitions the list emitter reconstructs.</summary>
    private static readonly string[] CollectionDefinitions =
    [
        "System.Collections.Generic.List",
        "System.Collections.Generic.IList",
        "System.Collections.Generic.ICollection",
        "System.Collections.Generic.IReadOnlyCollection",
        "System.Collections.Generic.IReadOnlyList",
        "System.Collections.Generic.IEnumerable",
    ];

    /// <summary><c>T?</c> for a nullable value type → <c>T</c>; anything else unchanged.</summary>
    public static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        IsNullableValueType(type) ? ((INamedTypeSymbol)type).TypeArguments[0] : type;

    private static string? CheckAccessible(INamedTypeSymbol adapter, IAssemblySymbol? consumer)
    {
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

        public List<AdapterDescriptor>? ProjectFor(ITypeSymbol source) => Find(_project, source);

        public List<AdapterDescriptor>? PackageFor(ITypeSymbol source) => Find(_package, source);

        /// <summary>
        /// An exact registration first; otherwise one for the open definition of a constructed
        /// generic (<c>Id&lt;Order&gt;</c> → <c>Id&lt;&gt;</c>). Exact wins, so a closed adapter for
        /// one construction specialises a generic default.
        /// </summary>
        private static List<AdapterDescriptor>? Find(
            Dictionary<ITypeSymbol, List<AdapterDescriptor>> table,
            ITypeSymbol source
        )
        {
            if (table.TryGetValue(source, out List<AdapterDescriptor>? exact))
                return exact;

            return
                source is INamedTypeSymbol { IsGenericType: true } named
                && table.TryGetValue(named.OriginalDefinition, out List<AdapterDescriptor>? open)
                ? open
                : null;
        }

        private static ITypeSymbol KeyOf(AdapterDescriptor descriptor) =>
            descriptor.IsGenericDefinition
                ? descriptor.Source!.OriginalDefinition
                : descriptor.Source!;

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

                ITypeSymbol key = KeyOf(descriptor);
                if (!target.TryGetValue(key, out List<AdapterDescriptor>? list))
                {
                    list = [];
                    target[key] = list;
                }

                list.Add(descriptor);
            }
        }
    }
}
