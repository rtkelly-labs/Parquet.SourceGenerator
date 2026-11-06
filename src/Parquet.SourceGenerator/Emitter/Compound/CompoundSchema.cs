using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Compound;

/// <summary>
/// Schema and static-field emission for compound models: StructField recursion in the schema,
/// and one cached DataField per leaf column reached through the StructField cast chain. v6-only
/// (the classic backend's schema tree for compounds lands with M5).
/// </summary>
internal static class CompoundSchema
{
    /// <summary>
    /// Field creation expression, recursing into struct children. Leaf kinds delegate to
    /// SchemaComponent's table so the two never drift; compound kinds beyond struct are
    /// unreachable here (the parser dial keeps them out of models until their milestones).
    /// </summary>
    public static string GetFieldCreationExpression(PropertyModel prop, string indent)
    {
        if (prop.Kind == PropertyKind.List)
        {
            // M3a: row-level lists standard 3-level. ListField synthesizes the optional
            // group + repeated list wrapper with no nullability knobs (docs/15 §2.4
            // semantics for groups apply to the list group too).
            string listName = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                prop.ParquetColumnName,
                quote: true
            );
            PropertyModel element = prop.Element!;
            string itemField =
                element.Kind == PropertyKind.Struct
                    ? GetFieldCreationExpression(element, "")
                    : SchemaComponent.GetFieldCreationExpression(element);
            return $"{indent}new global::Parquet.Schema.ListField({listName}, {itemField})";
        }

        if (prop.Kind != PropertyKind.Struct)
            return SchemaComponent.GetFieldCreationExpression(prop);

        // Parquet.Net 6's StructField is always an optional group (docs/15 §2.4) — the
        // definition ladder in CompoundMapping counts that rung unconditionally.
        string name = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
            prop.ParquetColumnName,
            quote: true
        );
        string inner = indent + "    ";
        var sb = new StringBuilder();
        sb.Append($"{indent}new global::Parquet.Schema.StructField(\n");
        sb.Append($"{indent}    {name},\n");
        for (int c = 0; c < prop.Children.Length; c++)
        {
            string comma = c < prop.Children.Length - 1 ? "," : "";
            sb.Append($"{GetFieldCreationExpression(prop.Children[c], inner)}{comma}\n");
        }
        sb.Append($"{indent})");
        return sb.ToString();
    }

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
            string comma = i < model.Properties.Length - 1 ? "," : "";
            builder.AppendLine(
                $"        {GetFieldCreationExpression(model.Properties[i], "        ")}{comma}"
            );
        }

        builder.AppendLine("    );");
    }

    public static void EmitStaticFields(StringBuilder builder, TargetClassModel model)
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            builder.AppendLine(
                $"    private static readonly global::Parquet.Schema.DataField _field_{col.Slot} = {col.SchemaAccessor};"
            );
        }
    }
}
