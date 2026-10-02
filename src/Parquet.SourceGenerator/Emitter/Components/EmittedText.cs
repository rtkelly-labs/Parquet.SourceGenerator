using Microsoft.CodeAnalysis.CSharp;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// The one place a user-controlled string is made safe to write into emitted source (#363, #364,
/// #372). A <c>[ParquetColumn]</c> name comes verbatim from the consumer's attribute and may hold
/// quotes, backslashes, newlines (including the Unicode line separators C# treats as line ends),
/// <c>*/</c>, braces or any other character. Which escaping is right depends on where the string
/// lands, so there is one method per destination. Every interpolation of such a name into emitted
/// text goes through one of them; the raw value is never interpolated.
/// </summary>
internal static class EmittedText
{
    /// <summary>
    /// A name taken from the model, made safe to use as a C# identifier: a reserved or contextual
    /// keyword gets the verbatim <c>@</c> prefix (#376). Roslyn's <c>ISymbol.Name</c> drops the
    /// <c>@</c> of a member declared <c>@event</c>, so the model holds the bare keyword. A dotted
    /// name (a nested type path) is escaped segment by segment. Use it wherever the name stands alone
    /// as an identifier; a name glued into a longer identifier (<c>{name}DefinitionLevels</c>) stays
    /// raw, because <c>@</c> is only legal at the start of one.
    /// </summary>
    public static string Ident(string name)
    {
        if (name.IndexOf('.') < 0)
        {
            return IsKeyword(name) ? "@" + name : name;
        }

        string[] parts = name.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = Ident(parts[i]);
        }

        return string.Join(".", parts);
    }

    private static bool IsKeyword(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
        || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None;

    /// <summary>
    /// The value as a quoted C# string literal, for use as an expression.
    /// </summary>
    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    /// <summary>
    /// The value as text that is safe on a single <c>//</c> or <c>///</c> comment line: the escaped
    /// literal body, so control characters and line separators are written as escape sequences and
    /// cannot end the comment.
    /// </summary>
    public static string Comment(string value) => SymbolDisplay.FormatLiteral(value, quote: false);

    /// <summary>
    /// The value as text that is safe inside an XML doc comment: <see cref="Comment"/> with the
    /// XML metacharacters escaped, so it is well-formed (no CS1570) as well as single-line.
    /// </summary>
    public static string XmlDoc(string value) =>
        Comment(value).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
