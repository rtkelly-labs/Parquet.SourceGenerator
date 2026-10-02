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
