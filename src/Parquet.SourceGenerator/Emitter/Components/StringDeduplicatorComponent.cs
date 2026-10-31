using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Composable emitter component for emitting the raw UTF-16 span read helpers that feed
/// the shared runtime <see cref="global::Parquet.SourceGenerator.StringDeduplicator"/>,
/// and the deduplicator state setup within reading methods.
/// </summary>
internal static class StringDeduplicatorComponent
{
    public static bool HasStringProperties(TargetClassModel model) =>
        global::System.Linq.Enumerable.Any(
            model.Properties,
            p => p.Kind == PropertyKind.Primitive && p.TypeName.Contains("string")
        );

    public static void EmitStringDeduplicator(
        StringBuilder builder,
        bool emitRawSpanColumnReader = true,
        bool utf8 = false
    )
    {
        if (!emitRawSpanColumnReader)
        {
            return;
        }

        if (utf8)
        {
            EmitUtf8StringColumnReader(builder);
        }
        else
        {
            EmitRawStringColumnReader(builder);
        }
    }

    /// <summary>
    /// The same helpers for Lake.Parquet, which reads a string column as its stored UTF-8 bytes
    /// (a string field's physical type there): no page of chars decoded first, the length limit is the
    /// byte length, and each value is decoded once, into its string or, for the deduplicator, into a
    /// stack or rented buffer so cache hits still allocate nothing. Same signature as the UTF-16
    /// helpers, so the call sites do not change.
    /// </summary>
    private static void EmitUtf8StringColumnReader(StringBuilder builder)
    {
        string[] lines =
        [
            "    /// <summary>",
            "    /// Reads a string column through Lake.Parquet's raw <c>ReadOnlyMemory&lt;byte&gt;</c> (UTF-8) surface and",
            "    /// materializes it via <see cref=\"global::Parquet.SourceGenerator.StringDeduplicator\"/>, so only distinct values allocate.",
            "    /// </summary>",
            "    private static async global::System.Threading.Tasks.ValueTask ReadBoundedStringColumnAsync(",
            "        global::Parquet.ParquetRowGroupReader groupReader,",
            "        global::Parquet.Schema.DataField field,",
            "        string?[] destination,",
            "        int rowCount,",
            "        bool deduplicateStrings,",
            "        global::Parquet.SourceGenerator.StringDeduplicator deduplicator,",
            "        int maxStringLengthBytes,",
            "        global::System.Threading.CancellationToken cancellationToken)",
            "    {",
            "        var raw = global::System.Buffers.ArrayPool<global::System.ReadOnlyMemory<byte>>.Shared.Rent(rowCount);",
            "        int maxDefinitionLevel = field.MaxDefinitionLevel;",
            "        int[]? definitionLevels = maxDefinitionLevel > 0",
            "            ? global::System.Buffers.ArrayPool<int>.Shared.Rent(rowCount)",
            "            : null;",
            "        try",
            "        {",
            "            await groupReader.ReadRawAsync(",
            "                field,",
            "                new global::System.Memory<global::System.ReadOnlyMemory<byte>>(raw, 0, rowCount),",
            "                definitionLevels is null",
            "                    ? (global::System.Memory<int>?)null",
            "                    : new global::System.Memory<int>(definitionLevels, 0, rowCount),",
            "                null,",
            "                cancellationToken).ConfigureAwait(false);",
            "",
            "            MaterializeBoundedStrings(raw, definitionLevels, destination, rowCount, maxDefinitionLevel, deduplicateStrings, deduplicator, maxStringLengthBytes, field.Name);",
            "        }",
            "        finally",
            "        {",
            "            global::System.Buffers.ArrayPool<global::System.ReadOnlyMemory<byte>>.Shared.Return(raw, clearArray: true);",
            "            if (definitionLevels is not null)",
            "            {",
            "                global::System.Buffers.ArrayPool<int>.Shared.Return(definitionLevels, clearArray: false);",
            "            }",
            "        }",
            "    }",
            "",
            "    /// <summary>",
            "    /// Spreads the packed raw UTF-8 lane back over the row lane, validating and materializing each value.",
            "    /// </summary>",
            "    private static void MaterializeBoundedStrings(",
            "        global::System.ReadOnlyMemory<byte>[] raw,",
            "        int[]? definitionLevels,",
            "        string?[] destination,",
            "        int rowCount,",
            "        int maxDefinitionLevel,",
            "        bool deduplicateStrings,",
            "        global::Parquet.SourceGenerator.StringDeduplicator deduplicator,",
            "        int maxStringLengthBytes,",
            "        string fieldName)",
            "    {",
            "        int packed = 0;",
            "        for (int i = 0; i < rowCount; i++)",
            "        {",
            "            if (definitionLevels is not null && definitionLevels[i] != maxDefinitionLevel)",
            "            {",
            "                destination[i] = null;",
            "                continue;",
            "            }",
            "",
            "            var value = raw[packed++].Span;",
            "            if (value.Length > maxStringLengthBytes)",
            "            {",
            "                throw new global::System.IO.InvalidDataException($\"String column '{fieldName}' value at index {i} UTF-8 length {value.Length} exceeds maximum allowed {maxStringLengthBytes}.\");",
            "            }",
            "            destination[i] = deduplicateStrings ? DeduplicateUtf8(value, deduplicator) : global::System.Text.Encoding.UTF8.GetString(value);",
            "        }",
            "    }",
            "",
            "    /// <summary>A UTF-8 value through the deduplicator, decoded into a stack or rented buffer.</summary>",
            "    private static string DeduplicateUtf8(global::System.ReadOnlySpan<byte> value, global::Parquet.SourceGenerator.StringDeduplicator deduplicator)",
            "    {",
            "        if (value.Length <= 256)",
            "        {",
            "            global::System.Span<char> chars = stackalloc char[256];",
            "            int count = global::System.Text.Encoding.UTF8.GetChars(value, chars);",
            "            return deduplicator.GetOrAdd(chars.Slice(0, count));",
            "        }",
            "",
            "        char[] rented = global::System.Buffers.ArrayPool<char>.Shared.Rent(value.Length);",
            "        try",
            "        {",
            "            int count = global::System.Text.Encoding.UTF8.GetChars(value, rented);",
            "            return deduplicator.GetOrAdd(new global::System.ReadOnlySpan<char>(rented, 0, count));",
            "        }",
            "        finally",
            "        {",
            "            global::System.Buffers.ArrayPool<char>.Shared.Return(rented);",
            "        }",
            "    }",
        ];
        foreach (string line in lines)
        {
            builder.AppendLine(line);
        }
    }

    /// <summary>
    /// Emits the helper that reads a string column as raw UTF-16 spans and materializes it
    /// through the deduplicator, so cache hits never instantiate a string.
    /// </summary>
    private static void EmitRawStringColumnReader(StringBuilder builder)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Reads a string column through Parquet.Net's raw <c>ReadOnlyMemory&lt;char&gt;</c> surface and"
        );
        builder.AppendLine(
            "    /// materializes it via <see cref=\"global::Parquet.SourceGenerator.StringDeduplicator\"/>, so only distinct values allocate."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    private static async global::System.Threading.Tasks.ValueTask ReadBoundedStringColumnAsync("
        );
        builder.AppendLine("        global::Parquet.ParquetRowGroupReader groupReader,");
        builder.AppendLine("        global::Parquet.Schema.DataField field,");
        builder.AppendLine("        string?[] destination,");
        builder.AppendLine("        int rowCount,");
        builder.AppendLine("        bool deduplicateStrings,");
        builder.AppendLine(
            "        global::Parquet.SourceGenerator.StringDeduplicator deduplicator,"
        );
        builder.AppendLine("        int maxStringLengthBytes,");
        builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        var raw = global::System.Buffers.ArrayPool<global::System.ReadOnlyMemory<char>>.Shared.Rent(rowCount);"
        );
        builder.AppendLine("        int maxDefinitionLevel = field.MaxDefinitionLevel;");
        builder.AppendLine("        int[]? definitionLevels = maxDefinitionLevel > 0");
        builder.AppendLine(
            "            ? global::System.Buffers.ArrayPool<int>.Shared.Rent(rowCount)"
        );
        builder.AppendLine("            : null;");
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine("            await groupReader.ReadRawAsync(");
        builder.AppendLine("                field,");
        builder.AppendLine(
            "                new global::System.Memory<global::System.ReadOnlyMemory<char>>(raw, 0, rowCount),"
        );
        builder.AppendLine("                definitionLevels is null");
        builder.AppendLine("                    ? (global::System.Memory<int>?)null");
        builder.AppendLine(
            "                    : new global::System.Memory<int>(definitionLevels, 0, rowCount),"
        );
        builder.AppendLine("                null,");
        builder.AppendLine("                cancellationToken).ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine(
            "            MaterializeBoundedStrings(raw, definitionLevels, destination, rowCount, maxDefinitionLevel, deduplicateStrings, deduplicator, maxStringLengthBytes, field.Name);"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        finally");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            global::System.Buffers.ArrayPool<global::System.ReadOnlyMemory<char>>.Shared.Return(raw, clearArray: true);"
        );
        builder.AppendLine("            if (definitionLevels is not null)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                global::System.Buffers.ArrayPool<int>.Shared.Return(definitionLevels, clearArray: false);"
        );
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Spreads the packed raw span lane back over the row lane, validating and materializing each value."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    private static void MaterializeBoundedStrings(");
        builder.AppendLine("        global::System.ReadOnlyMemory<char>[] raw,");
        builder.AppendLine("        int[]? definitionLevels,");
        builder.AppendLine("        string?[] destination,");
        builder.AppendLine("        int rowCount,");
        builder.AppendLine("        int maxDefinitionLevel,");
        builder.AppendLine("        bool deduplicateStrings,");
        builder.AppendLine(
            "        global::Parquet.SourceGenerator.StringDeduplicator deduplicator,"
        );
        builder.AppendLine("        int maxStringLengthBytes,");
        builder.AppendLine("        string fieldName)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (definitionLevels is null)");
        builder.AppendLine("        {");
        builder.AppendLine("            for (int i = 0; i < rowCount; i++)");
        builder.AppendLine("            {");
        builder.AppendLine("                var value = raw[i].Span;");
        builder.AppendLine(
            "                int byteCount = global::System.Text.Encoding.UTF8.GetByteCount(value);"
        );
        builder.AppendLine("                if (byteCount > maxStringLengthBytes)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException($\"String column '{fieldName}' value at index {i} UTF-8 length {byteCount} exceeds maximum allowed {maxStringLengthBytes}.\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine(
            "                destination[i] = deduplicateStrings ? deduplicator.GetOrAdd(value) : value.ToString();"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            return;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        int packed = 0;");
        builder.AppendLine("        for (int i = 0; i < rowCount; i++)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (definitionLevels[i] == maxDefinitionLevel)");
        builder.AppendLine("            {");
        builder.AppendLine("                var value = raw[packed].Span;");
        builder.AppendLine(
            "                int byteCount = global::System.Text.Encoding.UTF8.GetByteCount(value);"
        );
        builder.AppendLine("                if (byteCount > maxStringLengthBytes)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException($\"String column '{fieldName}' value at index {i} UTF-8 length {byteCount} exceeds maximum allowed {maxStringLengthBytes}.\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine(
            "                destination[i] = deduplicateStrings ? deduplicator.GetOrAdd(value) : value.ToString();"
        );
        builder.AppendLine("                packed++;");
        builder.AppendLine("            }");
        builder.AppendLine("            else");
        builder.AppendLine("            {");
        builder.AppendLine("                destination[i] = null;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    public static void EmitDeduplicatorDeclaration(
        StringBuilder builder,
        TargetClassModel model,
        string indent = "        "
    )
    {
        if (HasStringProperties(model))
        {
            builder.AppendLine(
                $"{indent}using var stringDeduplicator = new global::Parquet.SourceGenerator.StringDeduplicator(512);"
            );
            builder.AppendLine($"{indent}bool deduplicateStrings = options.DeduplicateStrings;");
            builder.AppendLine();
        }
    }
}
