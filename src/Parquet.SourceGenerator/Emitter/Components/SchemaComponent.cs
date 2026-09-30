using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Composable emitter component for schema field resolution, static field caching, and schema declaration.
/// </summary>
internal static class SchemaComponent
{
    /// <summary>
    /// Formats a boolean as a C# literal ("true" or "false").
    /// </summary>
    public static string BoolLiteral(bool value) => value ? "true" : "false";

    /// <summary>
    /// Generates compile-time ParquetSchema DataField instantiation code.
    /// </summary>
    public static string GetFieldCreationExpression(PropertyModel prop)
    {
        string name = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
            prop.ParquetColumnName,
            quote: true
        );

        return prop.Kind switch
        {
            PropertyKind.Decimal
                when prop.DecimalPrecision.HasValue && prop.DecimalScale.HasValue =>
                $"new global::Parquet.Schema.DecimalDataField({name}, {prop.DecimalPrecision.Value}, {prop.DecimalScale.Value}, isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.Decimal =>
                $"new global::Parquet.Schema.DecimalDataField({name}, 38, 18, isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.DateTime
                when prop.TimestampUnit == "1"
                    || prop.TimestampUnit?.Contains("Microseconds") == true =>
                $"new global::Parquet.Schema.DateTimeDataField({name}, global::Parquet.Schema.DateTimeFormat.DateAndTimeMicros, isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.DateTime =>
                $"new global::Parquet.Schema.DateTimeDataField({name}, global::Parquet.Schema.DateTimeFormat.Impala, isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.DateOnly =>
                $"new global::Parquet.Schema.DateTimeDataField({name}, global::Parquet.Schema.DateTimeFormat.Impala, isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.TimeSpan =>
                $"new global::Parquet.Schema.TimeDataField({name}, global::Parquet.Schema.TimeUnitPrecision.Millis, isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.TimeOnly =>
                $"new global::Parquet.Schema.TimeDataField({name}, global::Parquet.Schema.TimeUnitPrecision.Micros, isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.Guid =>
                $"new global::Parquet.Schema.DataField({name}, typeof(global::System.Guid), isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.Enum =>
                $"new global::Parquet.Schema.DataField({name}, typeof({prop.EnumUnderlyingTypeName ?? "int"}), isNullable: {BoolLiteral(prop.IsNullable)})",

            PropertyKind.ByteArray =>
                $"new global::Parquet.Schema.DataField({name}, typeof(byte[]), isNullable: {BoolLiteral(prop.IsNullable)})",

            _ =>
                $"new global::Parquet.Schema.DataField({name}, typeof({prop.TypeName.TrimEnd('?')}), isNullable: {BoolLiteral(prop.IsNullable)})",
        };
    }

    /// <summary>
    /// Emits static cached DataField references from the static Schema.
    /// </summary>
    public static void EmitStaticFields(StringBuilder builder, TargetClassModel model)
    {
        for (int i = 0; i < model.Properties.Length; i++)
        {
            builder.AppendLine(
                $"    private static readonly global::Parquet.Schema.DataField _field_{i} = (global::Parquet.Schema.DataField)Schema.Fields[{i}];"
            );
        }
    }

    /// <summary>
    /// Emits compile-time ParquetSchema definition.
    /// </summary>
    public static void EmitSchema(StringBuilder builder, TargetClassModel model)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Static compile-time <c>Parquet.Schema.ParquetSchema</c> for <c>{model.ClassName}</c>."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    public static readonly global::Parquet.Schema.ParquetSchema Schema = new global::Parquet.Schema.ParquetSchema("
        );

        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            string comma = i < model.Properties.Length - 1 ? "," : "";
            builder.AppendLine($"        {GetFieldCreationExpression(prop)}{comma}");
        }

        builder.AppendLine("    );");
    }

    /// <summary>
    /// The emitted text that renders a <c>FieldPath</c> as a string. Parquet.Net 6 implements
    /// <c>IFormattable</c> on it, so the modern backend passes an invariant provider (MA0011); a null
    /// format is the same as <c>ToString()</c>. Parquet.Net 4.x/5.x has no such overload.
    /// </summary>
    private static string PathText(string expression, bool invariantPath) =>
        invariantPath
            ? $"{expression}.Path.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture)"
            : $"{expression}.Path.ToString()";

    /// <summary>
    /// Emits ResolveSchemaField helper method.
    /// </summary>
    /// <param name="builder">The string builder.</param>
    /// <param name="usePath">When true, resolves via field.Path.ToString() (for v4/v5); when false, resolves via field.Name (for v6).</param>
    /// <param name="invariantPath">When true, renders field paths with an invariant <c>IFormatProvider</c> (Parquet.Net 6 only).</param>
    public static void EmitResolveSchemaField(
        StringBuilder builder,
        bool usePath = false,
        bool invariantPath = false
    )
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Resolves one generated schema field against the fields actually present in the file."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    private static global::Parquet.Schema.DataField ResolveSchemaField("
        );
        builder.AppendLine("        global::Parquet.Schema.DataField[] fileFields,");
        builder.AppendLine("        int index,");
        builder.AppendLine("        global::Parquet.Schema.DataField expected,");
        builder.AppendLine(
            "        ref global::System.Collections.Generic.Dictionary<string, global::Parquet.Schema.DataField>? byName,"
        );
        builder.AppendLine("        out bool missing)");
        builder.AppendLine("    {");
        builder.AppendLine("        missing = false;");

        if (usePath)
        {
            builder.AppendLine(
                $"        string expectedPath = {PathText("expected", invariantPath)};"
            );
            builder.AppendLine();
            builder.AppendLine(
                "        // Ordered schemas resolve on a single index check. Every file this generator writes lands"
            );
            builder.AppendLine(
                "        // here, as does any file whose column order matches; the linear scan below is only for"
            );
            builder.AppendLine("        // files written with a different column order.");
            builder.AppendLine("        if ((uint)index < (uint)fileFields.Length");
            builder.AppendLine(
                $"            && string.Equals({PathText("fileFields[index]", invariantPath)}, expectedPath, global::System.StringComparison.OrdinalIgnoreCase))"
            );
            builder.AppendLine("        {");
            builder.AppendLine("            var field = fileFields[index];");
            builder.AppendLine("            if (field.ClrType != expected.ClrType)");
            builder.AppendLine("            {");
            builder.AppendLine(
                "                throw new global::System.IO.InvalidDataException($\"Column '{field.Path}' type '{field.ClrType}' does not match expected '{expected.ClrType}'.\");"
            );
            builder.AppendLine("            }");
            builder.AppendLine("            return field;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (byName is null)");
            builder.AppendLine("        {");
            builder.AppendLine(
                "            byName = new global::System.Collections.Generic.Dictionary<string, global::Parquet.Schema.DataField>("
            );
            builder.AppendLine(
                "                fileFields.Length, global::System.StringComparer.OrdinalIgnoreCase);"
            );
            builder.AppendLine("            for (int i = 0; i < fileFields.Length; i++)");
            builder.AppendLine("            {");
            builder.AppendLine(
                "                // First occurrence wins if a file carries duplicate column names. Dictionary.TryAdd is"
            );
            builder.AppendLine(
                "                // not available to netstandard2.0 consumers, hence the explicit containment check."
            );
            builder.AppendLine(
                $"                string path = {PathText("fileFields[i]", invariantPath)};"
            );
            builder.AppendLine("                if (!byName.ContainsKey(path))");
            builder.AppendLine("                {");
            builder.AppendLine("                    byName.Add(path, fileFields[i]);");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (byName.TryGetValue(expectedPath, out var matched))");
            builder.AppendLine("        {");
            builder.AppendLine("            if (matched.ClrType != expected.ClrType)");
            builder.AppendLine("            {");
            builder.AppendLine(
                "                throw new global::System.IO.InvalidDataException($\"Column '{matched.Path}' type '{matched.ClrType}' does not match expected '{expected.ClrType}'.\");"
            );
            builder.AppendLine("            }");
            builder.AppendLine("            return matched;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (!expected.IsNullable)");
            builder.AppendLine("        {");
            builder.AppendLine(
                "            throw new global::System.IO.InvalidDataException($\"Required column '{expectedPath}' was not found in the Parquet file schema.\");"
            );
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine(
                "        // Optional column absent from the file: documented schema evolution. The caller"
            );
            builder.AppendLine(
                "        // materialises nulls for it instead of asking the file for a column it does not have."
            );
            builder.AppendLine("        missing = true;");
            builder.AppendLine("        return expected;");
            builder.AppendLine("    }");
        }
        else
        {
            builder.AppendLine(
                "        // Ordered schemas resolve on a single index check: no hashing, no delegate, no allocation."
            );
            builder.AppendLine(
                "        // Every file this generator writes lands here, as does any file whose column order matches."
            );
            builder.AppendLine("        if ((uint)index < (uint)fileFields.Length");
            builder.AppendLine(
                "            && string.Equals(fileFields[index].Name, expected.Name, global::System.StringComparison.OrdinalIgnoreCase))"
            );
            builder.AppendLine("        {");
            builder.AppendLine("            var field = fileFields[index];");
            builder.AppendLine("            if (field.ClrType != expected.ClrType)");
            builder.AppendLine("            {");
            builder.AppendLine(
                "                throw new global::System.IO.InvalidDataException($\"Column '{field.Name}' type '{field.ClrType}' does not match expected '{expected.ClrType}'.\");"
            );
            builder.AppendLine("            }");
            builder.AppendLine("            return field;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine(
                "        // Only a file whose column order differs reaches here. The name index is built at most once"
            );
            builder.AppendLine(
                "        // per read and reused for every subsequent miss, so even a fully reordered schema costs O(n)"
            );
            builder.AppendLine("        // in total rather than a linear scan per field.");
            builder.AppendLine("        if (byName is null)");
            builder.AppendLine("        {");
            builder.AppendLine(
                "            byName = new global::System.Collections.Generic.Dictionary<string, global::Parquet.Schema.DataField>("
            );
            builder.AppendLine(
                "                fileFields.Length, global::System.StringComparer.OrdinalIgnoreCase);"
            );
            builder.AppendLine("            for (int i = 0; i < fileFields.Length; i++)");
            builder.AppendLine("            {");
            builder.AppendLine(
                "                // First occurrence wins if a file carries duplicate column names. Dictionary.TryAdd is"
            );
            builder.AppendLine(
                "                // not available to netstandard2.0 consumers, hence the explicit containment check."
            );
            builder.AppendLine("                if (!byName.ContainsKey(fileFields[i].Name))");
            builder.AppendLine("                {");
            builder.AppendLine(
                "                    byName.Add(fileFields[i].Name, fileFields[i]);"
            );
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (byName.TryGetValue(expected.Name, out var match))");
            builder.AppendLine("        {");
            builder.AppendLine("            if (match.ClrType != expected.ClrType)");
            builder.AppendLine("            {");
            builder.AppendLine(
                "                throw new global::System.IO.InvalidDataException($\"Column '{match.Name}' type '{match.ClrType}' does not match expected '{expected.ClrType}'.\");"
            );
            builder.AppendLine("            }");
            builder.AppendLine("            return match;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (!expected.IsNullable)");
            builder.AppendLine("        {");
            builder.AppendLine(
                "            throw new global::System.IO.InvalidDataException($\"Required column '{expected.Name}' was not found in the Parquet file schema.\");"
            );
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine(
                "        // Optional column absent from the file: documented schema evolution. The caller"
            );
            builder.AppendLine(
                "        // materialises nulls for it instead of asking the file for a column it does not have."
            );
            builder.AppendLine("        missing = true;");
            builder.AppendLine("        return expected;");
            builder.AppendLine("    }");
        }
    }

    /// <summary>
    /// Emits validation for the physical type recorded in every row-group column chunk.
    /// </summary>
    /// <remarks>
    /// Schema fields expose a CLR projection, but a hostile footer can retain the expected schema
    /// while changing a column chunk's physical type. The generated readers must reject that
    /// mismatch before asking Parquet.Net to allocate or decode a column buffer.
    /// </remarks>
    public static void EmitValidatePhysicalType(StringBuilder builder, bool invariantPath = false)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Validates that a present column chunk retains the physical type declared by the generated schema."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    private static void ValidatePhysicalType(");
        builder.AppendLine("        global::Parquet.ParquetReader reader,");
        builder.AppendLine("        global::Parquet.Schema.DataField[] fileFields,");
        builder.AppendLine("        global::Parquet.Schema.DataField expected)");
        builder.AppendLine("    {");
        builder.AppendLine($"        string expectedPath = {PathText("expected", invariantPath)};");
        builder.AppendLine("        int fieldIndex = -1;");
        builder.AppendLine("        for (int i = 0; i < fileFields.Length; i++)");
        builder.AppendLine("        {");
        builder.AppendLine(
            $"            if (string.Equals({PathText("fileFields[i]", invariantPath)}, expectedPath, global::System.StringComparison.OrdinalIgnoreCase))"
        );
        builder.AppendLine("            {");
        builder.AppendLine("                fieldIndex = i;");
        builder.AppendLine("                break;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("        if (fieldIndex < 0)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (expected.IsNullable) return;");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException($\"Required column '{expectedPath}' was not found in the Parquet file schema.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var schemaElement = fileFields[fieldIndex].SchemaElement;");
        builder.AppendLine(
            "        if (schemaElement is null) throw new global::System.IO.InvalidDataException($\"Column '{expectedPath}' has no physical type in the Parquet file schema.\");"
        );
        builder.AppendLine("        var expectedPhysicalType = schemaElement.Type;");
        builder.AppendLine(
            "        for (int rowGroup = 0; rowGroup < reader.RowGroupCount; rowGroup++)"
        );
        builder.AppendLine("        {");
        builder.AppendLine(
            "            if ((uint)rowGroup >= (uint)reader.Metadata.RowGroups.Count) throw new global::System.IO.InvalidDataException($\"Row group {rowGroup} is missing from the Parquet footer metadata.\");"
        );
        builder.AppendLine(
            "            var columns = reader.Metadata.RowGroups[rowGroup].Columns;"
        );
        builder.AppendLine(
            "            if ((uint)fieldIndex >= (uint)columns.Count) throw new global::System.IO.InvalidDataException($\"Column '{expectedPath}' is missing from row group {rowGroup} metadata.\");"
        );
        builder.AppendLine("            var metadata = columns[fieldIndex].MetaData;");
        builder.AppendLine(
            "            if (metadata is null) throw new global::System.IO.InvalidDataException($\"Column '{expectedPath}' has no metadata in row group {rowGroup}.\");"
        );
        builder.AppendLine("            if (metadata.Type != expectedPhysicalType)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Column '{expectedPath}' physical type '{metadata.Type}' does not match expected '{expectedPhysicalType}' for CLR type '{expected.ClrType}' in row group {rowGroup}.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    /// <summary>
    /// Emits validation for column chunk byte ranges before any generated reader opens a page.
    /// </summary>
    public static void EmitValidateColumnChunkBounds(StringBuilder builder)
    {
        builder.AppendLine(
            "    private static async global::System.Threading.Tasks.Task<long> GetFooterStartAsync(global::System.IO.Stream stream, global::System.Threading.CancellationToken cancellationToken)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        if (!stream.CanSeek || stream.Length < 8)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException(\"Parquet stream is not seekable or is too small to contain a footer.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        long originalPosition = stream.Position;");
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine("            stream.Position = stream.Length - 8;");
        builder.AppendLine("            var footerLengthBytes = new byte[4];");
        builder.AppendLine("            int read = 0;");
        builder.AppendLine("            while (read < footerLengthBytes.Length)");
        builder.AppendLine("            {");
        builder.AppendLine("#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER");
        builder.AppendLine(
            "                int count = await stream.ReadAsync(global::System.MemoryExtensions.AsMemory(footerLengthBytes, read, footerLengthBytes.Length - read), cancellationToken).ConfigureAwait(false);"
        );
        builder.AppendLine("#else");
        builder.AppendLine(
            "                int count = await stream.ReadAsync(footerLengthBytes, read, footerLengthBytes.Length - read, cancellationToken).ConfigureAwait(false);"
        );
        builder.AppendLine("#endif");
        builder.AppendLine("                if (count == 0)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException(\"Parquet footer length could not be read.\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine("                read += count;");
        builder.AppendLine("            }");
        builder.AppendLine(
            "            long footerLength = footerLengthBytes[0] | ((long)footerLengthBytes[1] << 8) | ((long)footerLengthBytes[2] << 16) | ((long)footerLengthBytes[3] << 24);"
        );
        builder.AppendLine("            long footerStart = stream.Length - 8 - footerLength;");
        builder.AppendLine("            if (footerStart < 4 || footerStart > stream.Length - 8)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException(\"Parquet footer bounds are invalid.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            return footerStart;");
        builder.AppendLine("        }");
        builder.AppendLine("        finally");
        builder.AppendLine("        {");
        builder.AppendLine("            stream.Position = originalPosition;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static void ValidateColumnChunkBounds(global::Parquet.ParquetReader reader, long footerStart, int maxChunkCount)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        var fileMetadata = reader.Metadata;");
        builder.AppendLine("        if (fileMetadata is null)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException(\"Parquet footer metadata is missing.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        long chunkCount = 0;");
        builder.AppendLine("        foreach (var rowGroup in fileMetadata.RowGroups)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            if (maxChunkCount < 0 || rowGroup.Columns.Count > maxChunkCount - chunkCount)"
        );
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Parquet footer contains more than the maximum allowed {maxChunkCount} column chunks.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            chunkCount += rowGroup.Columns.Count;");
        builder.AppendLine("        }");
        builder.AppendLine("        var starts = new long[checked((int)chunkCount)];");
        builder.AppendLine("        var ends = new long[checked((int)chunkCount)];");
        builder.AppendLine("        int rangeCount = 0;");
        builder.AppendLine(
            "        for (int rowGroupIndex = 0; rowGroupIndex < fileMetadata.RowGroups.Count; rowGroupIndex++)"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            var rowGroup = fileMetadata.RowGroups[rowGroupIndex];");
        builder.AppendLine(
            "            for (int columnIndex = 0; columnIndex < rowGroup.Columns.Count; columnIndex++)"
        );
        builder.AppendLine("            {");
        builder.AppendLine(
            "                if (!TryGetColumnChunkRange(rowGroup, rowGroupIndex, columnIndex, footerStart, out long chunkStart, out long chunkEnd))"
        );
        builder.AppendLine("                {");
        builder.AppendLine("                    continue;");
        builder.AppendLine("                }");
        builder.AppendLine("                starts[rangeCount] = chunkStart;");
        builder.AppendLine("                ends[rangeCount] = chunkEnd;");
        builder.AppendLine("                rangeCount++;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("        global::System.Array.Sort(starts, ends, 0, rangeCount);");
        builder.AppendLine("        long previousEnd = rangeCount == 0 ? 0 : ends[0];");
        builder.AppendLine("        for (int i = 1; i < rangeCount; i++)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (starts[i] < previousEnd)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Column chunk ranges overlap at file offset {starts[i]}.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            if (ends[i] > previousEnd)");
        builder.AppendLine("            {");
        builder.AppendLine("                previousEnd = ends[i];");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static bool TryGetColumnChunkRange(global::Parquet.Meta.RowGroup rowGroup, int rowGroupIndex, int columnIndex, long footerStart, out long chunkStart, out long chunkEnd)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        chunkStart = 0;");
        builder.AppendLine("        chunkEnd = 0;");
        builder.AppendLine("        var metadata = rowGroup.Columns[columnIndex].MetaData;");
        builder.AppendLine("        if (metadata is null)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException($\"Column chunk metadata is missing for row group {rowGroupIndex}, column {columnIndex}.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        if (metadata.TotalCompressedSize < 0)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} has a negative compressed size.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        if (metadata.TotalCompressedSize == 0)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (rowGroup.NumRows > 0)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} has no compressed data.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine("        long dataPageOffset = metadata.DataPageOffset;");
        builder.AppendLine("        if (dataPageOffset < 4 || dataPageOffset >= footerStart)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} has data page offset {dataPageOffset} outside the file data bounds.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        chunkStart = dataPageOffset;");
        builder.AppendLine("        if (metadata.IndexPageOffset.HasValue)");
        builder.AppendLine("        {");
        builder.AppendLine("            long indexPageOffset = metadata.IndexPageOffset.Value;");
        builder.AppendLine(
            "            if (indexPageOffset < 4 || indexPageOffset >= footerStart)"
        );
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} has index page offset {indexPageOffset} outside the file data bounds.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            if (indexPageOffset > dataPageOffset)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} has an index page after its first data page.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            chunkStart = indexPageOffset;");
        builder.AppendLine("        }");
        builder.AppendLine("        if (metadata.DictionaryPageOffset.HasValue)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            long dictionaryPageOffset = metadata.DictionaryPageOffset.Value;"
        );
        builder.AppendLine(
            "            if (dictionaryPageOffset < 4 || dictionaryPageOffset >= footerStart)"
        );
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} has dictionary page offset {dictionaryPageOffset} outside the file data bounds.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            if (dictionaryPageOffset > dataPageOffset)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} has a dictionary page after its data page.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine(
            "            if (dictionaryPageOffset < chunkStart) chunkStart = dictionaryPageOffset;"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        if (metadata.TotalCompressedSize > footerStart - chunkStart)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException($\"Column chunk {columnIndex} in row group {rowGroupIndex} extends beyond the file data bounds.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        chunkEnd = chunkStart + metadata.TotalCompressedSize;");
        builder.AppendLine("        return true;");
        builder.AppendLine("    }");
    }
}
