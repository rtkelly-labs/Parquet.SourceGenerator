using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>
/// Renders a member symbol into the one-per-line catalogue grammar used by
/// <c>src/api/seams.txt</c>.
/// </summary>
/// <remarks>
/// <para>The grammar is deliberately the same one the emitted-API baselines use (see
/// <c>docs/17-GENERATED-API-BASELINES.md</c>), so a contributor learns one line format and applies
/// it to all three governed surfaces. The renderer differs only in its input: baselines are
/// rendered from <i>syntax</i>, because the emitted source has no semantic model at the point the
/// golden files are produced, whereas seams are rendered from <i>symbols</i>, because the gate runs
/// inside a real compilation. Rendering from symbols is strictly better where it is available —
/// <c>string</c> and <c>System.String</c> cannot produce two different lines, and a type moved
/// between namespaces changes the line as it should.</para>
/// </remarks>
public static class SeamSignature
{
    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    /// <remarks>
    /// <c>required</c> is absent from the canonical modifier order the baselines use because the
    /// Roslyn version this analyzer targets (4.0.1, matching the generators) predates the feature
    /// and its <c>IsRequired</c> symbol API. Nothing in <c>src/</c> uses it; when something does,
    /// this is the one place to add it.
    /// </remarks>
    /// <summary>Renders every catalogue line a member contributes, or nothing when it contributes none.</summary>
    public static IEnumerable<string> Render(ISymbol symbol)
    {
        string container = symbol.ContainingType.ToDisplayString(TypeFormat);
        string prefix = Prefix(symbol);

        switch (symbol)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                yield return prefix
                    + container
                    + "."
                    + constructor.ContainingType.Name
                    + RenderParameters(constructor)
                    + " -> void";
                break;

            case IMethodSymbol { MethodKind: MethodKind.Conversion } conversion:
                yield return prefix
                    + container
                    + "."
                    + (conversion.Name == "op_Implicit" ? "implicit" : "explicit")
                    + " operator "
                    + conversion.ReturnType.ToDisplayString(TypeFormat)
                    + RenderParameters(conversion)
                    + " -> "
                    + conversion.ReturnType.ToDisplayString(TypeFormat);
                break;

            case IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } op:
                yield return prefix
                    + container
                    + ".operator "
                    + OperatorToken(op.Name)
                    + RenderParameters(op)
                    + " -> "
                    + op.ReturnType.ToDisplayString(TypeFormat);
                break;

            case IMethodSymbol method:
                yield return prefix
                    + container
                    + "."
                    + method.Name
                    + RenderTypeParameters(method)
                    + RenderParameters(method)
                    + " -> "
                    + method.ReturnType.ToDisplayString(TypeFormat);
                break;

            case IPropertySymbol property:
            {
                string name = property.IsIndexer
                    ? container + ".this" + RenderParameters(property.Parameters, '[', ']')
                    : container + "." + property.Name;
                string type = property.Type.ToDisplayString(TypeFormat);

                if (IsWidened(property.GetMethod))
                {
                    yield return prefix + name + ".get -> " + type;
                }

                if (IsWidened(property.SetMethod))
                {
                    yield return prefix
                        + name
                        + (property.SetMethod!.IsInitOnly ? ".init" : ".set")
                        + " -> void";
                }

                break;
            }

            case IFieldSymbol field:
            {
                string value =
                    field.HasConstantValue && field.IsConst
                        ? " = " + FormatConstant(field.ConstantValue, field.Type)
                        : string.Empty;
                yield return prefix
                    + container
                    + "."
                    + field.Name
                    + value
                    + " -> "
                    + field.Type.ToDisplayString(TypeFormat);
                break;
            }

            case IEventSymbol @event:
                yield return prefix
                    + container
                    + "."
                    + @event.Name
                    + " -> "
                    + @event.Type.ToDisplayString(TypeFormat);
                break;
        }
    }

    /// <summary>True when a member is widened past <c>private</c> in the way a seam is.</summary>
    public static bool IsWidened(ISymbol? symbol) =>
        symbol is not null
        && symbol.DeclaredAccessibility
            is Accessibility.Internal
                or Accessibility.ProtectedOrInternal;

    private static string Prefix(ISymbol symbol)
    {
        var parts = new List<string>();
        if (symbol is IFieldSymbol { IsConst: true })
        {
            parts.Add("const");
        }

        if (symbol.IsStatic && symbol is not IFieldSymbol { IsConst: true })
        {
            parts.Add("static");
        }

        if (symbol is IFieldSymbol { IsReadOnly: true })
        {
            parts.Add("readonly");
        }

        if (symbol.IsAbstract)
        {
            parts.Add("abstract");
        }

        if (symbol.IsVirtual)
        {
            parts.Add("virtual");
        }

        if (symbol.IsOverride)
        {
            parts.Add("override");
        }

        if (symbol.IsSealed && symbol is not IFieldSymbol)
        {
            parts.Add("sealed");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" ", parts) + " ";
    }

    private static string RenderTypeParameters(IMethodSymbol method) =>
        method.TypeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", method.TypeParameters.Select(p => p.Name)) + ">";

    private static string RenderParameters(IMethodSymbol method) =>
        RenderParameters(method.Parameters, '(', ')', method.IsExtensionMethod);

    private static string RenderParameters(
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> parameters,
        char open,
        char close,
        bool isExtension = false
    )
    {
        var rendered = new List<string>(parameters.Length);
        foreach (IParameterSymbol parameter in parameters)
        {
            var builder = new StringBuilder();
            if (isExtension && parameter.Ordinal == 0)
            {
                builder.Append("this ");
            }

            switch (parameter.RefKind)
            {
                case RefKind.Ref:
                    builder.Append("ref ");
                    break;
                case RefKind.Out:
                    builder.Append("out ");
                    break;
                case RefKind.In:
                    builder.Append("in ");
                    break;
            }

            if (parameter.IsParams)
            {
                builder.Append("params ");
            }

            builder.Append(parameter.Type.ToDisplayString(TypeFormat)).Append(' ');
            builder.Append(parameter.Name);

            if (parameter.HasExplicitDefaultValue)
            {
                builder
                    .Append(" = ")
                    .Append(FormatConstant(parameter.ExplicitDefaultValue, parameter.Type));
            }

            rendered.Add(builder.ToString());
        }

        return open + string.Join(", ", rendered) + close;
    }

    private static string FormatConstant(object? value, ITypeSymbol type)
    {
        if (value is null)
        {
            return type.IsReferenceType || type.NullableAnnotation == NullableAnnotation.Annotated
                ? "null"
                : "default";
        }

        return SymbolDisplay.FormatPrimitive(
            value,
            quoteStrings: true,
            useHexadecimalNumbers: false
        );
    }

    private static string OperatorToken(string metadataName) =>
        metadataName switch
        {
            "op_Addition" => "+",
            "op_Subtraction" => "-",
            "op_Multiply" => "*",
            "op_Division" => "/",
            "op_Equality" => "==",
            "op_Inequality" => "!=",
            "op_LessThan" => "<",
            "op_GreaterThan" => ">",
            "op_LessThanOrEqual" => "<=",
            "op_GreaterThanOrEqual" => ">=",
            _ => metadataName,
        };
}
