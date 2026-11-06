using System;
using System.Collections.Generic;
using System.Text;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter;

/// <summary>
/// Row-group predicate pushdown (issue #149). Emits a per-model zone-map struct carrying each
/// eligible column's <c>min_value</c>/<c>max_value</c>/<c>null_count</c>, plus the guard the read
/// loops call before opening a row group's column pages. A pruned row group costs one footer
/// statistics lookup and nothing else — no page read, no decompression, no buffer rental.
/// </summary>
internal static class RowGroupPruningComponent
{
    /// <summary>Members the generated metadata struct always carries; a colliding column is dropped.</summary>
    private static readonly HashSet<string> ReservedMembers = new(StringComparer.Ordinal)
    {
        "RowGroupIndex",
        "RowCount",
        "HasStatistics",
    };

    /// <summary>
    /// CLR types whose Parquet statistics project back losslessly. Decimal, DateTime, TimeSpan,
    /// Guid and byte[] columns record their *physical* representation (unscaled int64, epoch
    /// int64, UTF-8 string) rather than the logical value, so they are deliberately excluded —
    /// projecting them would invite a wrong skip.
    /// </summary>
    private static readonly HashSet<string> EligibleTypes = new(StringComparer.Ordinal)
    {
        "int",
        "long",
        "short",
        "sbyte",
        "byte",
        "ushort",
        "uint",
        "ulong",
        "float",
        "double",
        "string",
    };

    /// <summary>The columns a pruning predicate can see for this model, in slot order.</summary>
    public static List<LeafColumn> EligibleColumns(TargetClassModel model)
    {
        var result = new List<LeafColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            if (col.IsCompound || col.IsListLeaf)
                continue;
            if (col.Leaf.Kind != PropertyKind.Primitive)
                continue;
            if (!EligibleTypes.Contains(UnderlyingType(col.Leaf)))
                continue;
            if (ReservedMembers.Contains(col.Leaf.Name) || !seen.Add(col.Leaf.Name))
                continue;
            result.Add(col);
        }

        return result;
    }

    /// <summary>Whether this model gets predicate-accepting read overloads at all.</summary>
    public static bool IsEnabled(TargetClassModel model) => EligibleColumns(model).Count > 0;

    private static string UnderlyingType(PropertyModel prop) => prop.TypeName.TrimEnd('?');

    /// <summary>Flattened name of the emitted zone-map struct.</summary>
    public static string MetadataTypeName(TargetClassModel model) =>
        $"{model.ClassName.Replace(".", string.Empty)}RowGroupMetadata";

    /// <summary>Globally qualified zone-map struct name, for use in public signatures.</summary>
    public static string QualifiedMetadataTypeName(TargetClassModel model) =>
        string.IsNullOrEmpty(model.Namespace)
            ? $"global::{MetadataTypeName(model)}"
            : $"global::{model.Namespace}.{MetadataTypeName(model)}";

    /// <summary>The predicate parameter line appended to a read method's parameter list.</summary>
    public static string PredicateParameter(TargetClassModel model) =>
        $"        global::System.Func<{QualifiedMetadataTypeName(model)}, bool>? predicate = null";

    /// <summary>
    /// Closing text for a read method's parameter list: appends the optional pruning predicate
    /// when the model has at least one column whose statistics can be projected.
    /// </summary>
    public static string SignatureSuffix(TargetClassModel model) =>
        IsEnabled(model)
            ? $",\n        global::System.Func<{QualifiedMetadataTypeName(model)}, bool>? predicate = null)"
            : ")";

    /// <summary>Argument text forwarding the predicate to a delegating overload.</summary>
    public static string ForwardArgument(TargetClassModel model) =>
        IsEnabled(model) ? ", predicate" : string.Empty;

    /// <summary>The call arguments passing every statistics column's resolved field to the guard.</summary>
    private static string FieldArguments(TargetClassModel model)
    {
        var sb = new StringBuilder();
        foreach (LeafColumn col in EligibleColumns(model))
        {
            sb.Append(", field_").Append(col.Slot);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Emits the per-row-group guard call. Placed immediately after the row group reader is
    /// opened and before any column read, so a rejected group never touches a data page.
    /// </summary>
    public static void EmitLoopGuard(
        StringBuilder builder,
        TargetClassModel model,
        string indent,
        string groupReaderVar = "groupReader",
        string indexVar = "r"
    )
    {
        if (!IsEnabled(model))
            return;
        builder.AppendLine(
            $"{indent}if (!AcceptRowGroup(predicate, {groupReaderVar}, {indexVar}{FieldArguments(model)})) continue;"
        );
    }

    /// <summary>
    /// Emits the pre-pass that decides which row groups survive and how many rows they hold, so a
    /// materializing reader can still size its result exactly. Declares <c>selectedGroups</c> and
    /// assigns <paramref name="totalRowsVar"/>.
    /// </summary>
    public static void EmitSelectionPass(
        StringBuilder builder,
        TargetClassModel model,
        string totalRowsVar
    )
    {
        if (!IsEnabled(model))
        {
            builder.AppendLine(
                $"        int {totalRowsVar} = (int)global::System.Linq.Enumerable.Sum(reader.RowGroups, rg => rg.RowCount);"
            );
            return;
        }

        builder.AppendLine("        int rowGroupCount = reader.RowGroupCount;");
        builder.AppendLine(
            "        if (rowGroupCount < 0 || rowGroupCount > options.MaxRowGroupCount)"
        );
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException($\"Row group count {rowGroupCount} is invalid or exceeds maximum allowed {options.MaxRowGroupCount}.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine($"        int {totalRowsVar};");
        builder.AppendLine("        bool[]? selectedGroups = null;");
        builder.AppendLine("        if (predicate == null)");
        builder.AppendLine("        {");
        builder.AppendLine("            long sumRows = 0;");
        builder.AppendLine("            for (int r = 0; r < rowGroupCount; r++)");
        builder.AppendLine("            {");
        builder.AppendLine("                long rc = reader.RowGroups[r].RowCount;");
        builder.AppendLine("                if (rc < 0 || rc > options.MaxAllocationValues)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException($\"Row group {r} row count {rc} is invalid or exceeds maximum allowed {options.MaxAllocationValues}.\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine("                sumRows = checked(sumRows + rc);");
        builder.AppendLine("            }");
        builder.AppendLine("            if (sumRows > options.MaxAllocationValues)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Total row count {sumRows} exceeds maximum allowed {options.MaxAllocationValues}.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine($"            {totalRowsVar} = checked((int)sumRows);");
        builder.AppendLine("        }");
        builder.AppendLine("        else");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            // Zone-map pre-pass: footer statistics only. Surviving groups are the only ones"
        );
        builder.AppendLine(
            "            // whose pages are ever read, and the result is sized to exactly their rows."
        );
        builder.AppendLine("            selectedGroups = new bool[rowGroupCount];");
        builder.AppendLine("            long sumRows = 0;");
        builder.AppendLine("            for (int r = 0; r < rowGroupCount; r++)");
        builder.AppendLine("            {");
        builder.AppendLine("                using var probeReader = reader.OpenRowGroupReader(r);");
        builder.AppendLine(
            $"                if (!AcceptRowGroup(predicate, probeReader, r{FieldArguments(model)})) continue;"
        );
        builder.AppendLine("                long rc = probeReader.RowCount;");
        builder.AppendLine("                if (rc < 0 || rc > options.MaxAllocationValues)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException($\"Row group {r} row count {rc} is invalid or exceeds maximum allowed {options.MaxAllocationValues}.\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine("                selectedGroups[r] = true;");
        builder.AppendLine("                sumRows = checked(sumRows + rc);");
        builder.AppendLine("            }");
        builder.AppendLine("            if (sumRows > options.MaxAllocationValues)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Total matching row count {sumRows} exceeds maximum allowed {options.MaxAllocationValues}.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine($"            {totalRowsVar} = checked((int)sumRows);");
        builder.AppendLine("        }");
    }

    /// <summary>Emits the cheap per-iteration test against the pre-pass result.</summary>
    public static void EmitSelectionCheck(
        StringBuilder builder,
        TargetClassModel model,
        string indent
    )
    {
        if (!IsEnabled(model))
            return;
        builder.AppendLine($"{indent}if (selectedGroups != null && !selectedGroups[r]) continue;");
    }

    /// <summary>
    /// Emits the guard method: projects one row group's column-chunk statistics onto the model's
    /// types and asks the caller's predicate whether the group can hold a matching row.
    /// </summary>
    /// <remarks>
    /// Constraint recorded per #264 (from closed #209's lesson): any future row-group PREFETCH
    /// (overlapped read-ahead of group pages) MUST consult this emitted <c>AcceptRowGroup</c>
    /// decision before decoding a group, or it silently pulls every skipped group back into the
    /// page cache and undoes the pruning win it was meant to compose with. Same rule applies to
    /// <see cref="SortedRowGroupPruningComponent"/>'s pruned-range core. A revival of #209 that
    /// ignores this turns pushdown into pure overhead.
    /// </remarks>
    public static void EmitAcceptRowGroup(StringBuilder builder, TargetClassModel model)
    {
        if (!IsEnabled(model))
            return;

        List<LeafColumn> columns = EligibleColumns(model);
        string metaType = QualifiedMetadataTypeName(model);

        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Decides whether a row group can hold a row matching <paramref name=\"predicate\"/>, using"
        );
        builder.AppendLine(
            "    /// only the column-chunk statistics already present in the file footer."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            "    /// Returns <c>true</c> — read the group — whenever the answer is not certain: a null predicate,"
        );
        builder.AppendLine(
            "    /// or a chunk that recorded no usable min/max. Pruning only ever removes row groups the"
        );
        builder.AppendLine("    /// statistics prove cannot match.");
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine("    private static bool AcceptRowGroup(");
        builder.AppendLine($"        global::System.Func<{metaType}, bool>? predicate,");
        builder.AppendLine("        global::Parquet.ParquetRowGroupReader groupReader,");
        builder.Append("        int rowGroupIndex");
        foreach (LeafColumn col in columns)
        {
            builder.AppendLine(",");
            builder.Append($"        global::Parquet.Schema.DataField field_{col.Slot}");
        }

        builder.AppendLine(")");
        builder.AppendLine("    {");
        builder.AppendLine("        if (predicate == null) return true;");
        builder.AppendLine();

        foreach (LeafColumn col in columns)
        {
            builder.AppendLine(
                $"        var stats_{col.Slot} = groupReader.GetStatistics(field_{col.Slot});"
            );
        }

        builder.AppendLine();
        builder.Append("        bool hasStatistics =");
        for (int i = 0; i < columns.Count; i++)
        {
            int slot = columns[i].Slot;
            builder.AppendLine();
            builder.Append(
                $"            {(i == 0 ? " " : "&&")} stats_{slot} != null && stats_{slot}.MinValue != null && stats_{slot}.MaxValue != null"
            );
        }

        builder.AppendLine(";");
        builder.AppendLine();
        builder.AppendLine(
            "        // A chunk with no usable zone map disables pruning for the whole group rather than"
        );
        builder.AppendLine(
            "        // letting a raw Min/Max comparison against a default value skip live rows."
        );
        builder.AppendLine("        if (!hasStatistics) return true;");
        builder.AppendLine();
        builder.AppendLine($"        var metadata = new {metaType}(");
        builder.AppendLine("            rowGroupIndex,");
        builder.AppendLine("            groupReader.RowCount,");
        builder.Append("            true");
        foreach (LeafColumn col in columns)
        {
            string type = UnderlyingType(col.Leaf);
            builder.AppendLine(",");
            builder.Append(
                $"            global::Parquet.SourceGenerator.ParquetColumnStatistics.FromRaw<{type}>("
                    + $"stats_{col.Slot}!.MinValue, stats_{col.Slot}!.MaxValue, stats_{col.Slot}!.NullCount, stats_{col.Slot}!.DistinctCount)"
            );
        }

        builder.AppendLine(");");
        builder.AppendLine();
        builder.AppendLine("        return predicate(metadata);");
        builder.AppendLine("    }");
    }

    /// <summary>
    /// Emits the public zone-map struct handed to a caller's pruning predicate. One property per
    /// eligible column, named after the model property it mirrors.
    /// </summary>
    public static void EmitMetadataStruct(StringBuilder builder, TargetClassModel model)
    {
        if (!IsEnabled(model))
            return;

        List<LeafColumn> columns = EligibleColumns(model);
        string name = MetadataTypeName(model);

        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// Footer statistics for one row group of a <c>{model.ClassName}</c> Parquet file, as seen by a"
        );
        builder.AppendLine(
            "/// row-group pruning predicate. Reading a property costs nothing beyond the footer that was"
        );
        builder.AppendLine("/// already parsed when the file was opened.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public readonly struct {name}");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>Creates a row-group zone map.</summary>");
        builder.AppendLine($"    public {name}(");
        builder.AppendLine("        int rowGroupIndex,");
        builder.AppendLine("        long rowCount,");
        builder.Append("        bool hasStatistics");
        foreach (LeafColumn col in columns)
        {
            builder.AppendLine(",");
            builder.Append(
                $"        global::Parquet.SourceGenerator.ParquetColumnStatistics<{UnderlyingType(col.Leaf)}> column_{col.Slot}"
            );
        }

        builder.AppendLine(")");
        builder.AppendLine("    {");
        builder.AppendLine("        RowGroupIndex = rowGroupIndex;");
        builder.AppendLine("        RowCount = rowCount;");
        builder.AppendLine("        HasStatistics = hasStatistics;");
        foreach (LeafColumn col in columns)
        {
            builder.AppendLine($"        {col.Leaf.Name} = column_{col.Slot};");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    /// <summary>Zero-based index of this row group in the file.</summary>"
        );
        builder.AppendLine("    public int RowGroupIndex { get; }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Rows in this row group.</summary>");
        builder.AppendLine("    public long RowCount { get; }");
        builder.AppendLine();
        builder.AppendLine(
            "    /// <summary>Whether every projected column recorded a usable min/max. Always true inside a"
        );
        builder.AppendLine(
            "    /// pruning predicate: a group without complete statistics is read rather than tested.</summary>"
        );
        builder.AppendLine("    public bool HasStatistics { get; }");

        foreach (LeafColumn col in columns)
        {
            builder.AppendLine();
            builder.AppendLine(
                $"    /// <summary>Zone map for the <c>{col.Leaf.ParquetColumnName}</c> column.</summary>"
            );
            builder.AppendLine(
                $"    public global::Parquet.SourceGenerator.ParquetColumnStatistics<{UnderlyingType(col.Leaf)}> {col.Leaf.Name} {{ get; }}"
            );
        }

        builder.AppendLine("}");
    }
}
