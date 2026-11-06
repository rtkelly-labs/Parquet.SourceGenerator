using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>
/// Renders a signature-only baseline of the public API a generated source file emits.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The golden <c>.g.cs</c> files hold the full emitted body, so an
/// added public overload and a retuned buffer loop produce diffs of the same visual shape. The
/// generated surface is also the one API in this repository that lives in no shipped assembly, so
/// no .NET API-diff tool can see it. A <c>.api.txt</c> baseline next to every golden file makes
/// that surface reviewable on its own.</para>
///
/// <para><b>Format.</b> The grammar is deliberately the one used by
/// <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c> in this repository's
/// <c>PublicAPI.Shipped.txt</c> / <c>PublicAPI.Unshipped.txt</c>, so a reader learns one grammar
/// for both the shipped and the generated surface:</para>
/// <list type="bullet">
///   <item><description>Line 1 is <c>#nullable enable</c>. Nullable reference annotations below it
///     are meaningful.</description></item>
///   <item><description>One member per line. Every line carries its fully-qualified containing
///     type, so a line is self-contained and does not change when an unrelated member is added or
///     removed. One generated file routinely holds several types (the extensions class, a nested
///     <c>ColumnBatch</c>, a row-group metadata struct, a columnar batch struct).</description></item>
///   <item><description>A type contributes a bare line: <c>Ns.Type</c> — generic types keep their
///     type-parameter list, <c>Ns.Type&lt;T&gt;</c>.</description></item>
///   <item><description>Methods: <c>Ns.Type.Method(Type param, Type other = 4) -&gt; ReturnType</c>.
///     <c>-&gt;</c> introduces the return type; <c>void</c> is written out. Constructors are
///     <c>Ns.Type.Type(...) -&gt; void</c>.</description></item>
///   <item><description>Properties contribute one line per visible accessor:
///     <c>Ns.Type.Prop.get -&gt; T</c>, <c>Ns.Type.Prop.set -&gt; void</c>,
///     <c>Ns.Type.Prop.init -&gt; void</c>. Indexers use <c>Ns.Type.this[Type i].get -&gt; T</c>.
///     </description></item>
///   <item><description>Fields: <c>Ns.Type.Field -&gt; T</c>; constants keep their value,
///     <c>const Ns.Type.Field = 4 -&gt; int</c>; enum members are
///     <c>Ns.Enum.Member = 1 -&gt; Ns.Enum</c>.</description></item>
///   <item><description>API-relevant modifiers are prefixed in a fixed canonical order
///     (<c>const static readonly required abstract virtual override sealed</c>), independent of the
///     order they appear in the emitted source. Accessibility, <c>partial</c>, <c>async</c>,
///     <c>unsafe</c>, <c>extern</c>, <c>new</c> and <c>volatile</c> are implementation detail and
///     are dropped.</description></item>
/// </list>
///
/// <para><b>Deliberate deviations from PublicAPI.txt.</b> Two, both forced by this baseline being
/// derived from syntax rather than from symbols — the emitted source is not compiled against the
/// consumer's references at the point the golden files are produced:</para>
/// <list type="bullet">
///   <item><description>The <c>!</c> non-null reference marker is not synthesized. Whether a type
///     name denotes a reference type is a semantic fact. Nullable annotations that the emitter
///     actually wrote (<c>string?</c>, <c>Guid?</c>) are preserved verbatim, so a reference type
///     becoming nullable still shows as a diff.</description></item>
///   <item><description><c>global::</c> qualifiers are stripped. They are a
///     collision-proofing device of the emitter, not part of the API, and stripping them keeps
///     lines readable and identical in shape to the shipped baselines.</description></item>
/// </list>
///
/// <para><b>Determinism.</b> Lines are sorted with <see cref="StringComparer.Ordinal"/> — never a
/// culture-sensitive comparison, which would reorder across machines — and are derived from
/// declaration syntax, never from reflection or symbol enumeration order. Duplicate lines are
/// collapsed so that a type split across two <c>partial</c> declarations in one file yields one
/// entry. The result is byte-identical across runs, platforms and locales.</para>
///
/// <para><b>Visibility.</b> Only externally-reachable declarations are listed: a member is included
/// when it is <c>public</c>, <c>protected</c> or <c>protected internal</c> <i>and</i> every type
/// containing it is likewise. A <c>public</c> member of a <c>private</c> nested helper — the
/// emitted <c>StringDeduplicator</c>, for instance — is not public API and is not listed.</para>
/// </remarks>
public static class GeneratedApiBaseline
{
    /// <summary>First line of every baseline file, mirroring <c>PublicAPI.Shipped.txt</c>.</summary>
    public const string NullableHeader = "#nullable enable";

    /// <summary>
    /// Modifiers that are part of the API contract, in the fixed order they are rendered. Rendering
    /// from this list rather than from the source token order is what makes the output independent
    /// of how the emitter happened to spell a declaration.
    /// </summary>
    private static readonly string[] ApiModifiers =
    {
        "const",
        "static",
        "readonly",
        "required",
        "abstract",
        "virtual",
        "override",
        "sealed",
    };

    /// <summary>
    /// Parses <paramref name="emittedSource"/> with Roslyn and renders its public API baseline.
    /// </summary>
    /// <param name="emittedSource">Generated C# exactly as the emitter produced it.</param>
    /// <returns>The baseline text, newline-terminated, ordinal-sorted.</returns>
    public static string Create(string emittedSource)
    {
        CompilationUnitSyntax root = CSharpSyntaxTree
            .ParseText(emittedSource)
            .GetCompilationUnitRoot();

        var lines = new List<string>();
        foreach (MemberDeclarationSyntax member in root.Members)
        {
            VisitContainerMember(member, containerName: null, lines);
        }

        // Ordinal, not culture-sensitive: ordering must not depend on the machine's locale.
        var ordered = lines.Distinct(StringComparer.Ordinal).ToList();
        ordered.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder();
        builder.Append(NullableHeader).Append('\n');
        foreach (string line in ordered)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Counts the members catalogued in a rendered baseline, ignoring the header.</summary>
    public static int CountMembers(string baseline) =>
        baseline
            .Split('\n')
            .Count(line =>
                line.Length > 0 && !string.Equals(line, NullableHeader, StringComparison.Ordinal)
            );

    private static void VisitContainerMember(
        MemberDeclarationSyntax member,
        string? containerName,
        List<string> lines
    )
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax ns:
            {
                string nested = Qualify(containerName, Normalize(ns.Name.ToString()));
                foreach (MemberDeclarationSyntax child in ns.Members)
                {
                    VisitContainerMember(child, nested, lines);
                }

                break;
            }

            case EnumDeclarationSyntax enumDeclaration:
                VisitEnum(enumDeclaration, containerName, lines);
                break;

            case TypeDeclarationSyntax typeDeclaration:
                VisitType(typeDeclaration, containerName, lines);
                break;

            case DelegateDeclarationSyntax delegateDeclaration:
                if (IsExternallyVisible(delegateDeclaration.Modifiers))
                {
                    lines.Add(
                        Qualify(
                            containerName,
                            delegateDeclaration.Identifier.Text
                                + Normalize(delegateDeclaration.TypeParameterList?.ToString())
                        )
                    );
                }

                break;

            default:
                // Global statements and using directives carry no public surface.
                break;
        }
    }

    private static void VisitEnum(
        EnumDeclarationSyntax declaration,
        string? containerName,
        List<string> lines
    )
    {
        if (!IsExternallyVisible(declaration.Modifiers))
        {
            return;
        }

        string typeName = Qualify(containerName, declaration.Identifier.Text);
        lines.Add(typeName);

        long implicitValue = 0;
        foreach (EnumMemberDeclarationSyntax enumMember in declaration.Members)
        {
            string value;
            if (enumMember.EqualsValue is { } equalsValue)
            {
                value = Normalize(equalsValue.Value.ToString());
                if (
                    long.TryParse(
                        value,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out long parsed
                    )
                )
                {
                    implicitValue = parsed + 1;
                }
            }
            else
            {
                value = implicitValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                implicitValue++;
            }

            lines.Add($"{typeName}.{enumMember.Identifier.Text} = {value} -> {typeName}");
        }
    }

    private static void VisitType(
        TypeDeclarationSyntax declaration,
        string? containerName,
        List<string> lines
    )
    {
        if (!IsExternallyVisible(declaration.Modifiers))
        {
            // Nothing inside a non-visible type can be reached from outside, however it is declared.
            return;
        }

        string typeName = Qualify(
            containerName,
            declaration.Identifier.Text + Normalize(declaration.TypeParameterList?.ToString())
        );
        lines.Add(typeName);

        // Positional records declare their primary constructor and its properties in the header.
        if (declaration is RecordDeclarationSyntax { ParameterList: { } primary })
        {
            lines.Add(
                $"{Prefix(declaration.Modifiers)}{typeName}.{declaration.Identifier.Text}{RenderParameters(primary)} -> void"
            );
        }

        foreach (MemberDeclarationSyntax member in declaration.Members)
        {
            VisitTypeMember(member, typeName, declaration.Identifier.Text, lines);
        }
    }

    private static void VisitTypeMember(
        MemberDeclarationSyntax member,
        string typeName,
        string typeIdentifier,
        List<string> lines
    )
    {
        switch (member)
        {
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                VisitContainerMember(member, typeName, lines);
                return;
        }

        if (!IsExternallyVisible(member.Modifiers))
        {
            return;
        }

        string prefix = Prefix(member.Modifiers);

        switch (member)
        {
            case ConstructorDeclarationSyntax ctor:
                lines.Add(
                    $"{prefix}{typeName}.{typeIdentifier}{RenderParameters(ctor.ParameterList)} -> void"
                );
                break;

            case MethodDeclarationSyntax method:
                lines.Add(
                    $"{prefix}{typeName}.{method.Identifier.Text}"
                        + $"{Normalize(method.TypeParameterList?.ToString())}"
                        + $"{RenderParameters(method.ParameterList)} -> {Normalize(method.ReturnType.ToString())}"
                );
                break;

            case OperatorDeclarationSyntax op:
                lines.Add(
                    $"{prefix}{typeName}.operator {op.OperatorToken.Text}"
                        + $"{RenderParameters(op.ParameterList)} -> {Normalize(op.ReturnType.ToString())}"
                );
                break;

            case ConversionOperatorDeclarationSyntax conversion:
                lines.Add(
                    $"{prefix}{typeName}.{conversion.ImplicitOrExplicitKeyword.Text} operator "
                        + $"{Normalize(conversion.Type.ToString())}"
                        + $"{RenderParameters(conversion.ParameterList)} -> {Normalize(conversion.Type.ToString())}"
                );
                break;

            case PropertyDeclarationSyntax property:
                AddAccessors(
                    lines,
                    prefix,
                    $"{typeName}.{property.Identifier.Text}",
                    Normalize(property.Type.ToString()),
                    property.AccessorList,
                    property.ExpressionBody is not null
                );
                break;

            case IndexerDeclarationSyntax indexer:
                AddAccessors(
                    lines,
                    prefix,
                    $"{typeName}.this{RenderParameters(indexer.ParameterList, '[', ']')}",
                    Normalize(indexer.Type.ToString()),
                    indexer.AccessorList,
                    indexer.ExpressionBody is not null
                );
                break;

            case FieldDeclarationSyntax field:
            {
                string fieldType = Normalize(field.Declaration.Type.ToString());
                bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    string value =
                        isConst && variable.Initializer is { } initializer
                            ? $" = {Normalize(initializer.Value.ToString())}"
                            : string.Empty;
                    lines.Add(
                        $"{prefix}{typeName}.{variable.Identifier.Text}{value} -> {fieldType}"
                    );
                }

                break;
            }

            case EventFieldDeclarationSyntax eventField:
            {
                string eventType = Normalize(eventField.Declaration.Type.ToString());
                foreach (VariableDeclaratorSyntax variable in eventField.Declaration.Variables)
                {
                    lines.Add($"{prefix}{typeName}.{variable.Identifier.Text} -> {eventType}");
                }

                break;
            }

            case EventDeclarationSyntax eventDeclaration:
                lines.Add(
                    $"{prefix}{typeName}.{eventDeclaration.Identifier.Text} -> "
                        + Normalize(eventDeclaration.Type.ToString())
                );
                break;

            default:
                // Destructors and initializers are not API.
                break;
        }
    }

    private static void AddAccessors(
        List<string> lines,
        string prefix,
        string memberName,
        string memberType,
        AccessorListSyntax? accessors,
        bool hasExpressionBody
    )
    {
        if (accessors is null)
        {
            if (hasExpressionBody)
            {
                lines.Add($"{prefix}{memberName}.get -> {memberType}");
            }

            return;
        }

        foreach (AccessorDeclarationSyntax accessor in accessors.Accessors)
        {
            // An accessor may narrow the member's accessibility (`public int X { get; private set; }`).
            if (accessor.Modifiers.Count > 0 && !IsExternallyVisible(accessor.Modifiers))
            {
                continue;
            }

            string keyword = accessor.Keyword.Text;
            lines.Add(
                string.Equals(keyword, "get", StringComparison.Ordinal)
                    ? $"{prefix}{memberName}.get -> {memberType}"
                    : $"{prefix}{memberName}.{keyword} -> void"
            );
        }
    }

    private static string RenderParameters(
        BaseParameterListSyntax parameterList,
        char open = '(',
        char close = ')'
    )
    {
        var rendered = new List<string>(parameterList.Parameters.Count);
        foreach (ParameterSyntax parameter in parameterList.Parameters)
        {
            var builder = new StringBuilder();
            foreach (SyntaxToken modifier in parameter.Modifiers)
            {
                builder.Append(modifier.Text).Append(' ');
            }

            if (parameter.Type is { } parameterType)
            {
                builder.Append(Normalize(parameterType.ToString())).Append(' ');
            }

            builder.Append(parameter.Identifier.Text);

            if (parameter.Default is { } defaultValue)
            {
                builder.Append(" = ").Append(Normalize(defaultValue.Value.ToString()));
            }

            rendered.Add(builder.ToString());
        }

        return open + string.Join(", ", rendered) + close;
    }

    /// <summary>
    /// Renders the API-relevant modifiers of a declaration in a fixed canonical order, with a
    /// trailing space, or the empty string when there are none.
    /// </summary>
    private static string Prefix(SyntaxTokenList modifiers)
    {
        var present = new List<string>();
        foreach (string candidate in ApiModifiers)
        {
            foreach (SyntaxToken modifier in modifiers)
            {
                if (string.Equals(modifier.Text, candidate, StringComparison.Ordinal))
                {
                    present.Add(candidate);
                    break;
                }
            }
        }

        return present.Count == 0 ? string.Empty : string.Join(" ", present) + " ";
    }

    private static bool IsExternallyVisible(SyntaxTokenList modifiers)
    {
        bool isPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        bool isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        bool isPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);

        // `private protected` is not reachable outside the assembly; `protected internal` is.
        return isPublic || (isProtected && !isPrivate);
    }

    private static string Qualify(string? containerName, string name) =>
        string.IsNullOrEmpty(containerName) ? name : containerName + "." + name;

    /// <summary>
    /// Ordinal <c>string.Replace</c>. The <c>StringComparison</c> overload does not exist on
    /// netstandard2.0, which this assembly targets so that it can load as a Roslyn analyzer.
    /// </summary>
    private static string RemoveOrdinal(string text, string value)
    {
        int index = text.IndexOf(value, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        int start = 0;
        while (index >= 0)
        {
            builder.Append(text, start, index - start);
            start = index + value.Length;
            index = text.IndexOf(value, start, StringComparison.Ordinal);
        }

        builder.Append(text, start, text.Length - start);
        return builder.ToString();
    }

    /// <summary>
    /// Collapses the layout the emitter chose — parameter lists wrap across many lines — into one
    /// canonical single-line spelling, and drops <c>global::</c> qualifiers.
    /// </summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string withoutGlobal = RemoveOrdinal(text!, "global::");

        var builder = new StringBuilder(withoutGlobal.Length);
        bool pendingSpace = false;
        foreach (char character in withoutGlobal)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                // Whitespace never carries meaning next to these delimiters in a signature.
                if (
                    character is not (',' or ')' or '>' or ']' or '?')
                    && builder[builder.Length - 1] is not ('(' or '<' or '[')
                )
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
