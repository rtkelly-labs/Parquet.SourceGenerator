using System.Collections.Generic;
using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter;

/// <summary>
/// Emits the struct-of-arrays (SoA) columnar batch reading API (issue #147).
/// <para>
/// For every flat model the generator emits a nested <c>ColumnBatch</c> readonly struct exposing
/// one <see cref="System.ReadOnlySpan{T}"/> per column, plus a
/// <c>ReadParquetBatchesAsync</c> async iterator yielding one batch per row group. The batch holds
/// the pooled column buffers directly, so an analytical consumer scans decoded columns without a
/// single domain-object allocation.
/// </para>
/// <para>
/// Buffer lifetime: the iterator rents before decoding a row group and returns in a
/// <c>finally</c> that runs when the consumer advances the enumerator or disposes it. A batch is
/// therefore valid only until the next <c>MoveNextAsync</c>; the emitted XML docs say so.
/// </para>
/// <para>
/// Compound models (nested structs, lists, maps) get no batch API: their leaves do not line up
/// one-per-row with the row count, so a flat column span would be a lie rather than a view.
/// </para>
/// </summary>
internal static class ColumnBatchComponent
{
    /// <summary>Name of the nested batch struct emitted into the extensions class.</summary>
    public const string BatchTypeName = "ColumnBatch";

    /// <summary>
    /// Whether a model can be served by the SoA batch API: at least one column, and every column
    /// a flat leaf that maps one value per row.
    /// </summary>
    public static bool Supports(TargetClassModel model)
    {
        if (model.Properties.Length == 0)
        {
            return false;
        }

        EmissionPlan plan = EmissionPlan.For(model);
        return !plan.HasCompound && plan.Columns.Length > 0;
    }

    /// <summary>Buffer / span element type for a column — identical to the rental type so the
    /// span aliases the pooled array with no conversion and no nullability mismatch.</summary>
    private static string ElementType(LeafColumn col) =>
        BufferPoolComponent.GetBufferElementType(col.Leaf);

    /// <summary>
    /// Span accessor name for a column. <c>Amount</c> becomes <c>AmountSpan</c>; a collision with
    /// another member of the batch struct is resolved by appending underscores.
    /// </summary>
    private static Dictionary<int, string> SpanNames(EmissionPlan plan)
    {
        var used = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "RowCount",
            "RowGroupIndex",
        };
        var names = new Dictionary<int, string>();
        foreach (LeafColumn col in plan.Columns)
        {
            string candidate = col.Leaf.Name + "Span";
            while (!used.Add(candidate))
            {
                candidate += "_";
            }
            names[col.Slot] = candidate;
        }
        return names;
    }

    /// <summary>
    /// Emits the nested <c>ColumnBatch</c> struct: one pooled array field per column, the row
    /// count, and a <see cref="System.ReadOnlySpan{T}"/> accessor per column.
    /// </summary>
    public static void EmitBatchStruct(StringBuilder builder, TargetClassModel model)
    {
        EmissionPlan plan = EmissionPlan.For(model);
        Dictionary<int, string> spanNames = SpanNames(plan);

        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Struct-of-arrays view over one row group of <c>{model.ClassName}</c> data."
        );
        builder.AppendLine(
            "    /// Each column is exposed as a <see cref=\"global::System.ReadOnlySpan{T}\"/> over a pooled buffer."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            "    /// The buffers belong to <see cref=\"global::System.Buffers.ArrayPool{T}\"/> and are returned when the"
        );
        builder.AppendLine(
            "    /// producing enumerator advances or is disposed. Copy anything you need to outlive the current"
        );
        builder.AppendLine("    /// iteration; never store the batch itself.");
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine($"    public readonly struct {BatchTypeName}");
        builder.AppendLine("    {");

        foreach (LeafColumn col in plan.Columns)
        {
            builder.AppendLine(
                $"        private readonly {ElementType(col)}[] _buffer_{col.Slot};"
            );
        }

        builder.AppendLine();
        builder.AppendLine("        /// <summary>Number of rows in this row group.</summary>");
        builder.AppendLine("        public int RowCount { get; }");
        builder.AppendLine();
        builder.AppendLine(
            "        /// <summary>Zero-based index of the row group this batch came from.</summary>"
        );
        builder.AppendLine("        public int RowGroupIndex { get; }");
        builder.AppendLine();

        builder.Append($"        internal {BatchTypeName}(int rowCount, int rowGroupIndex");
        foreach (LeafColumn col in plan.Columns)
        {
            builder.Append($", {ElementType(col)}[] buffer_{col.Slot}");
        }
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            RowCount = rowCount;");
        builder.AppendLine("            RowGroupIndex = rowGroupIndex;");
        foreach (LeafColumn col in plan.Columns)
        {
            builder.AppendLine($"            _buffer_{col.Slot} = buffer_{col.Slot};");
        }
        builder.AppendLine("        }");

        foreach (LeafColumn col in plan.Columns)
        {
            string elem = ElementType(col);
            builder.AppendLine();
            builder.AppendLine("        /// <summary>");
            builder.AppendLine(
                $"        /// Column <c>{col.Leaf.ParquetColumnName}</c> (<c>{col.Leaf.Name}</c>) for the rows in this batch."
            );
            builder.AppendLine("        /// </summary>");
            builder.AppendLine(
                $"        public global::System.ReadOnlySpan<{elem}> {spanNames[col.Slot]} =>"
            );
            builder.AppendLine(
                $"            new global::System.ReadOnlySpan<{elem}>(_buffer_{col.Slot}, 0, RowCount);"
            );
        }

        builder.AppendLine("    }");
    }

    /// <summary>
    /// Emits <c>ReadParquetBatchesAsync</c> — one <c>ColumnBatch</c> per row group, no domain
    /// object ever constructed. <paramref name="emitColumnRead"/> is the shared per-column decode
    /// the POCO readers use, so there is no second copy of the buffer logic here.
    /// </summary>
    public static void EmitReadBatchesAsync(
        StringBuilder builder,
        TargetClassModel model,
        System.Action<StringBuilder, LeafColumn, string, string> emitColumnRead
    )
    {
        EmissionPlan plan = EmissionPlan.For(model);

        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Asynchronously streams <c>{model.ClassName}</c> data as columnar batches — one per row group —"
        );
        builder.AppendLine(
            $"    /// without materializing a single <c>{model.ClassName}</c> instance."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            $"    /// Each yielded <see cref=\"{BatchTypeName}\"/> aliases pooled buffers that are returned as soon as the"
        );
        builder.AppendLine(
            "    /// enumerator advances or is disposed, so the spans must not escape the loop body."
        );
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine(
            $"    public static async global::System.Collections.Generic.IAsyncEnumerable<{BatchTypeName}> ReadParquetBatchesAsync("
        );
        builder.AppendLine("        global::System.IO.Stream stream,");
        builder.AppendLine(
            "        global::Parquet.SourceGenerator.ParquetSerializerOptions? options = null,"
        );
        builder.AppendLine(
            "        [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine(
            "        if (stream == null) throw new global::System.ArgumentNullException(nameof(stream));"
        );
        builder.AppendLine();
        builder.AppendLine(
            "        options ??= global::Parquet.SourceGenerator.ParquetSerializerOptions.Default;"
        );
        builder.AppendLine();
        builder.AppendLine(
            "        await using var reader = await global::Parquet.ParquetReader.CreateAsync("
        );
        builder.AppendLine("            stream,");
        builder.AppendLine("            BuildFormatOptions(options),");
        builder.AppendLine("            cancellationToken: cancellationToken);");
        builder.AppendLine("        ValidateReader(reader, options);");
        builder.AppendLine("        var fileFields = reader.Schema.DataFields;");
        builder.AppendLine();
        builder.AppendLine(
            "        global::System.Collections.Generic.Dictionary<string, global::Parquet.Schema.DataField>? fieldsByName = null;"
        );
        foreach (LeafColumn col in plan.Columns)
        {
            // Shared with the POCO read path so the absent-optional-column flag (#168) stays in
            // step: columns that can go missing capture it, the rest discard it.
            builder.AppendLine(CodeEmitter.EmitResolveFieldLine(col, "        "));
        }
        // String columns in a batch decode through the same shared read emitter as the POCO
        // path, which references the deduplicator locals — declare them here too (#143).
        StringDeduplicatorComponent.EmitDeduplicatorDeclaration(builder, model);
        builder.AppendLine();
        builder.AppendLine("        for (int r = 0; r < reader.RowGroupCount; r++)");
        builder.AppendLine("        {");
        builder.AppendLine("            cancellationToken.ThrowIfCancellationRequested();");
        builder.AppendLine("            using var groupReader = reader.OpenRowGroupReader(r);");
        builder.AppendLine("            int rowCount = checked((int)groupReader.RowCount);");
        builder.AppendLine(
            "            if (rowCount < 0 || rowCount > options.MaxAllocationValues)"
        );
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Row group {r} row count {rowCount} is invalid or exceeds maximum allowed {options.MaxAllocationValues}.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine();

        BufferPoolComponent.EmitRentals(builder, model, "rowCount", indent: "            ");

        builder.AppendLine();
        builder.AppendLine("            try");
        builder.AppendLine("            {");

        foreach (LeafColumn col in plan.Columns)
        {
            emitColumnRead(builder, col, $"field_{col.Slot}", $"buffer_{col.Slot}");
        }

        builder.AppendLine();
        builder.Append($"                yield return new {BatchTypeName}(rowCount, r");
        foreach (LeafColumn col in plan.Columns)
        {
            builder.Append($", buffer_{col.Slot}");
        }
        builder.AppendLine(");");
        builder.AppendLine("            }");
        builder.AppendLine("            finally");
        builder.AppendLine("            {");

        BufferPoolComponent.EmitReturns(builder, model, indent: "                ");

        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Asynchronously streams <c>{model.ClassName}</c> columnar batches from an in-memory byte buffer."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            $"    public static async global::System.Collections.Generic.IAsyncEnumerable<{BatchTypeName}> ReadParquetBatchesAsync("
        );
        builder.AppendLine("        global::System.ReadOnlyMemory<byte> parquetBytes,");
        builder.AppendLine(
            "        global::Parquet.SourceGenerator.ParquetSerializerOptions? options = null,"
        );
        builder.AppendLine(
            "        [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        using var stream = CreateBufferStream(parquetBytes);");
        builder.AppendLine(
            "        await foreach (var batch in ReadParquetBatchesAsync(stream, options, cancellationToken))"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            yield return batch;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }
}
