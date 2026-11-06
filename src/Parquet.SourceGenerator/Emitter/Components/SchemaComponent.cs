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
    /// Emits ResolveSchemaField helper method.
    /// </summary>
    /// <param name="builder">The string builder.</param>
    /// <param name="usePath">When true, resolves via field.Path.ToString() (for v4/v5); when false, resolves via field.Name (for v6).</param>
    public static void EmitResolveSchemaField(StringBuilder builder, bool usePath = false)
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
            builder.AppendLine("        string expectedPath = expected.Path.ToString();");
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
                "            && string.Equals(fileFields[index].Path.ToString(), expectedPath, global::System.StringComparison.OrdinalIgnoreCase))"
            );
            builder.AppendLine("        {");
            builder.AppendLine("            return fileFields[index];");
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
            builder.AppendLine("                string path = fileFields[i].Path.ToString();");
            builder.AppendLine("                if (!byName.ContainsKey(path))");
            builder.AppendLine("                {");
            builder.AppendLine("                    byName.Add(path, fileFields[i]);");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (byName.TryGetValue(expectedPath, out var matched))");
            builder.AppendLine("        {");
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
            builder.AppendLine("            return fileFields[index];");
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
}
