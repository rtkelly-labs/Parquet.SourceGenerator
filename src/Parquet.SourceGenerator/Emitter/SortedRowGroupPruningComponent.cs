using System.Collections.Generic;
using System.Text;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter;

/// <summary>
/// One sortable key column: a root property marked <c>[ParquetSortKey]</c> that is flat,
/// non-nullable and totally ordered, so its row-group <c>[Min, Max]</c> statistics can drive
/// binary-search pruning.
/// </summary>
internal sealed class SortableColumn
{
    public PropertyModel Property { get; set; } = null!;

    /// <summary>Emission-plan column slot, which is also the schema field index.</summary>
    public int Slot { get; set; }

    /// <summary>Fully-qualified CLR type of the key ("int", "global::System.DateTime").</summary>
    public string KeyType { get; set; } = "";
}

/// <summary>
/// Emits sorted-row-group pruning (issue #151): binary search over the in-memory footer
/// <c>[Min, Max]</c> statistics so a point lookup or a range slice decompresses only the
/// row groups that can possibly contain the key, falling back to a full linear scan when
/// the statistics are missing or the intervals overlap.
/// <para>
/// Emission is opt-in per column via <c>[ParquetSortKey]</c>. A model with no marker gets
/// none of this — the emitted source is exactly what it would be without the feature.
/// </para>
/// </summary>
internal static class SortedRowGroupPruningComponent
{
    /// <summary>
    /// Selects the key columns to emit lookup overloads for: the root properties the model
    /// author opted in with <c>[ParquetSortKey]</c> and that
    /// <see cref="SortKeyEligibility"/> accepts. An ineligible marker never reaches here — the
    /// parser has already reported PARQ014 against it — so this stays a filter, not a second
    /// place where a rule could silently diverge.
    /// <para>
    /// With no marker anywhere on the model this returns empty and nothing at all is emitted:
    /// no lookups, no shared generic core, no pruning helpers.
    /// </para>
    /// </summary>
    public static List<SortableColumn> SortableColumns(TargetClassModel model)
    {
        var result = new List<SortableColumn>();
        EmissionPlan plan = EmissionPlan.For(model);

        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            if (!prop.IsSortKey)
                continue;

            if (!SortKeyEligibility.IsEligible(prop, out _))
                continue;

            int slot = plan.ColumnSlotByProperty[i];
            if (slot < 0 || slot >= plan.Columns.Length)
                continue;

            LeafColumn column = plan.Columns[slot];
            if (column.IsCompound || column.IsListLeaf)
                continue;

            result.Add(
                new SortableColumn
                {
                    Property = prop,
                    Slot = slot,
                    KeyType = prop.TypeName,
                }
            );
        }

        return result;
    }

    /// <summary>
    /// Emits the model-independent statistics comparison and binary-search pruning helpers.
    /// </summary>
    /// <remarks>
    /// Convergence target of #264: <c>TryPruneSortedRowGroups</c> is a second independent read
    /// of the same footer statistics <see cref="RowGroupPruningComponent.EmitAcceptRowGroup"/>
    /// projects, for models that opt into both mechanisms. Keep the conservative posture
    /// identical when they merge: missing statistics disable pruning here exactly as a chunk
    /// with no usable zone map disables it there. Prefetching must consult either decision
    /// before decoding a group (see the constraint recorded on <c>EmitAcceptRowGroup</c>).
    /// </remarks>
    public static void EmitPruningHelpers(StringBuilder builder)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Compares two row-group statistic values as <c>TKey</c>. Returns false — meaning"
        );
        builder.AppendLine(
            "    /// \"unknown, do not prune\" — when either value is not a <c>TKey</c>, so a file whose statistics"
        );
        builder.AppendLine(
            "    /// were written with a different physical type degrades to a full scan instead of a wrong answer."
        );
        builder.AppendLine(
            "    /// The type test unboxes rather than boxing the key, so no allocation reaches the heap."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    private static bool TryCompareStatistics<TKey>(object left, object right, out int comparison)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        if (left is TKey leftKey && right is TKey rightKey)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            comparison = global::System.Collections.Generic.Comparer<TKey>.Default.Compare(leftKey, rightKey);"
        );
        builder.AppendLine("            return true;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        comparison = 0;");
        builder.AppendLine("        return false;");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Compares one row-group statistic value against the sought key, without boxing the key."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    private static bool TryCompareStatisticToKey<TKey>(object statistic, TKey key, out int comparison)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        if (statistic is TKey statisticKey)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            comparison = global::System.Collections.Generic.Comparer<TKey>.Default.Compare(statisticKey, key);"
        );
        builder.AppendLine("            return true;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        comparison = 0;");
        builder.AppendLine("        return false;");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Certifies the key column as sorted from the footer metadata alone and, if so, binary searches"
        );
        builder.AppendLine(
            "    /// the row-group <c>[Min, Max]</c> intervals for the contiguous span that can contain"
        );
        builder.AppendLine(
            "    /// <c>[lowerBound, upperBound]</c>. Returns false when the column cannot be certified, in which"
        );
        builder.AppendLine(
            "    /// case the caller must scan every row group. No column data is read: statistics come from the"
        );
        builder.AppendLine("    /// footer already parsed in memory.");
        builder.AppendLine("    /// <para>");
        builder.AppendLine(
            "    /// Certifying sortedness inspects every row group's statistics, so this metadata phase is O(N)"
        );
        builder.AppendLine(
            "    /// in row groups; only the subsequent decompression is logarithmic. That is where the speedup"
        );
        builder.AppendLine(
            "    /// comes from — reading a footer entry is orders of magnitude cheaper than decompressing a page."
        );
        builder.AppendLine("    /// </para>");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    private static bool TryPruneSortedRowGroups<TKey>(");
        builder.AppendLine("        global::Parquet.ParquetReader reader,");
        builder.AppendLine("        global::Parquet.Schema.DataField keyField,");
        builder.AppendLine("        TKey lowerBound,");
        builder.AppendLine("        TKey upperBound,");
        builder.AppendLine("        out int firstRowGroup,");
        builder.AppendLine("        out int lastRowGroup,");
        builder.AppendLine("        out bool strictlyMonotonic)");
        builder.AppendLine("    {");
        builder.AppendLine("        int rowGroupCount = reader.RowGroupCount;");
        builder.AppendLine("        firstRowGroup = 0;");
        builder.AppendLine("        lastRowGroup = rowGroupCount - 1;");
        builder.AppendLine("        strictlyMonotonic = false;");
        builder.AppendLine("        if (rowGroupCount <= 0)");
        builder.AppendLine("        {");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var minima = new object[rowGroupCount];");
        builder.AppendLine("        var maxima = new object[rowGroupCount];");
        builder.AppendLine("        for (int r = 0; r < rowGroupCount; r++)");
        builder.AppendLine("        {");
        builder.AppendLine("            using var groupReader = reader.OpenRowGroupReader(r);");
        builder.AppendLine(
            "            global::Parquet.Data.DataColumnStatistics? statistics = groupReader.GetStatistics(keyField);"
        );
        builder.AppendLine("            object? min = statistics?.MinValue;");
        builder.AppendLine("            object? max = statistics?.MaxValue;");
        builder.AppendLine("            if (min is null || max is null)");
        builder.AppendLine("            {");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            minima[r] = min;");
        builder.AppendLine("            maxima[r] = max;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine(
            "        // Non-overlapping and non-decreasing is what binary search actually needs;"
        );
        builder.AppendLine(
            "        // strict monotonicity is recorded separately for the caller's diagnostics."
        );
        builder.AppendLine("        bool strict = true;");
        builder.AppendLine("        for (int r = 0; r < rowGroupCount; r++)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            if (!TryCompareStatistics<TKey>(minima[r], maxima[r], out int within) || within > 0)"
        );
        builder.AppendLine("            {");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (r + 1 < rowGroupCount)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                if (!TryCompareStatistics<TKey>(maxima[r], minima[r + 1], out int across) || across > 0)"
        );
        builder.AppendLine("                {");
        builder.AppendLine("                    return false;");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                if (across == 0)");
        builder.AppendLine("                {");
        builder.AppendLine("                    strict = false;");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        // First row group whose Max >= lowerBound.");
        builder.AppendLine("        int low = 0;");
        builder.AppendLine("        int high = rowGroupCount;");
        builder.AppendLine("        while (low < high)");
        builder.AppendLine("        {");
        builder.AppendLine("            int mid = low + ((high - low) >> 1);");
        builder.AppendLine(
            "            if (!TryCompareStatisticToKey<TKey>(maxima[mid], lowerBound, out int comparison))"
        );
        builder.AppendLine("            {");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (comparison < 0)");
        builder.AppendLine("            {");
        builder.AppendLine("                low = mid + 1;");
        builder.AppendLine("            }");
        builder.AppendLine("            else");
        builder.AppendLine("            {");
        builder.AppendLine("                high = mid;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        firstRowGroup = low;");
        builder.AppendLine();
        builder.AppendLine("        // Last row group whose Min <= upperBound.");
        builder.AppendLine("        low = 0;");
        builder.AppendLine("        high = rowGroupCount;");
        builder.AppendLine("        while (low < high)");
        builder.AppendLine("        {");
        builder.AppendLine("            int mid = low + ((high - low) >> 1);");
        builder.AppendLine(
            "            if (!TryCompareStatisticToKey<TKey>(minima[mid], upperBound, out int comparison))"
        );
        builder.AppendLine("            {");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (comparison <= 0)");
        builder.AppendLine("            {");
        builder.AppendLine("                low = mid + 1;");
        builder.AppendLine("            }");
        builder.AppendLine("            else");
        builder.AppendLine("            {");
        builder.AppendLine("                high = mid;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        lastRowGroup = low - 1;");
        builder.AppendLine("        strictlyMonotonic = strict;");
        builder.AppendLine("        return true;");
        builder.AppendLine("    }");
    }

    /// <summary>
    /// Emits the strongly typed point-lookup and range overloads, one pair per sortable column.
    /// Each is a three-line forward into the shared pruned-read core.
    /// </summary>
    public static void EmitLookupOverloads(
        StringBuilder builder,
        TargetClassModel model,
        List<SortableColumn> columns
    )
    {
        foreach (SortableColumn column in columns)
        {
            string name = column.Property.Name;
            string keyType = column.KeyType;

            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder.AppendLine(
                $"    /// Point lookup on <c>{name}</c>. When the file's row-group statistics certify <c>{name}</c> as"
            );
            builder.AppendLine(
                "    /// sorted, binary search locates the row group(s) that can hold the key and every other row"
            );
            builder.AppendLine(
                "    /// group is skipped without being decompressed; otherwise the read falls back to a full scan."
            );
            builder.AppendLine(
                "    /// The result is identical either way — pruning changes only how much work it costs."
            );
            builder.AppendLine("    /// </summary>");
            builder.AppendLine(
                $"    public static global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<{model.ClassName}>> ReadParquetBy{name}Async("
            );
            builder.AppendLine("        global::System.IO.Stream stream,");
            builder.AppendLine($"        {keyType} key,");
            builder.AppendLine(
                "        global::Parquet.SourceGenerator.ParquetPruneStatistics? pruneStatistics = null,"
            );
            builder.AppendLine(
                "        global::Parquet.SourceGenerator.ParquetSerializerOptions? options = null,"
            );
            builder.AppendLine(
                "        global::System.Threading.CancellationToken cancellationToken = default)"
            );
            builder.AppendLine(
                $"        => ReadPrunedRangeAsync<{keyType}>(stream, {column.Slot}, _field_{column.Slot}, key, key, static item => item.{name}, pruneStatistics, options, cancellationToken);"
            );

            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder.AppendLine(
                $"    /// Inclusive range slice on <c>{name}</c>. Both bounds are binary searched against the row-group"
            );
            builder.AppendLine(
                "    /// statistics so only the contiguous span of row groups overlapping the range is decompressed."
            );
            builder.AppendLine("    /// </summary>");
            builder.AppendLine(
                $"    public static global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<{model.ClassName}>> ReadParquet{name}RangeAsync("
            );
            builder.AppendLine("        global::System.IO.Stream stream,");
            builder.AppendLine($"        {keyType} inclusiveStart,");
            builder.AppendLine($"        {keyType} inclusiveEnd,");
            builder.AppendLine(
                "        global::Parquet.SourceGenerator.ParquetPruneStatistics? pruneStatistics = null,"
            );
            builder.AppendLine(
                "        global::Parquet.SourceGenerator.ParquetSerializerOptions? options = null,"
            );
            builder.AppendLine(
                "        global::System.Threading.CancellationToken cancellationToken = default)"
            );
            builder.AppendLine(
                $"        => ReadPrunedRangeAsync<{keyType}>(stream, {column.Slot}, _field_{column.Slot}, inclusiveStart, inclusiveEnd, static item => item.{name}, pruneStatistics, options, cancellationToken);"
            );
        }
    }
}
