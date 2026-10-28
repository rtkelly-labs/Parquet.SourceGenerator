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
        bool emitRawSpanColumnReader = true
    )
    {
        if (emitRawSpanColumnReader)
        {
            EmitRawStringColumnReader(builder);
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
