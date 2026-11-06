namespace Parquet.SourceGenerator.Parser;

/// <summary>
/// Which generation of the Parquet.Net API a backend emits against.
/// </summary>
/// <remarks>
/// The two backends do not accept the same set of member types. Parquet.Net's
/// <c>SchemaEncoder.SupportedTypes</c> grew between the 4.x/5.x line and 6.x, so a model that the v6
/// emitter handles can contain a column the classic emitter would write code for and then fail on at
/// runtime. Threading the level through the parser is what lets that be a compile-time error instead
/// — the audit named compile-time rejection a prerequisite for shipping a second backend.
/// </remarks>
public enum ParquetApiLevel
{
    /// <summary>
    /// Parquet.Net 6.x — the <c>Memory&lt;T&gt;</c> buffer API.
    /// </summary>
    V6 = 0,

    /// <summary>
    /// Parquet.Net 4.x and 5.x — the <c>DataColumn</c> API, and the last line to support .NET Framework.
    /// </summary>
    V4 = 1,
}
