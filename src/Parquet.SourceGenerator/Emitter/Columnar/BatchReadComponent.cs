using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Columnar;

/// <summary>
/// Emits the columnar read path (issues #147, #508): <c>ReadBatchesCoreAsync</c>, an async
/// iterator that yields one <c>&lt;Model&gt;Batch</c> per row group without constructing a single
/// domain object. The batch type itself is emitted by <see cref="ColumnarBatchComponent"/>, so the
/// type the reader yields is the type the columnar writer takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> Each decoded row group is handed over in the layout the writer consumes. Fixed
/// width columns wrap the pooled buffer they were decoded into. A nullable value column is decoded
/// raw into a packed value buffer plus definition levels (<c>ReadRawAsync</c>), which is the
/// shape Parquet.Net produces natively, so nothing is expanded or copied. String and byte-array
/// columns are decoded as before and then wrapped, one inline-nullable memory per row.
/// </para>
/// <para>
/// <b>Lifetime (#369).</b> The iterator rents before decoding a row group and returns in a
/// <c>finally</c> that runs when the consumer advances the enumerator or disposes it. A batch is
/// therefore borrowed: valid only until the next <c>MoveNextAsync</c>. It carries a lease the
/// iterator expires in that same <c>finally</c>, before the buffers go back to the pool, so a
/// batch kept past that point throws <see cref="System.ObjectDisposedException"/> from every lane
/// property instead of reading memory another renter now owns.
/// </para>
/// <para>
/// Compound models (nested structs, lists, maps) get no batch API: their leaves do not line up
/// one-per-row with the row count, so a flat column lane would be a lie rather than a view.
/// </para>
/// </remarks>
internal static class BatchReadComponent
{
    private const string Pool = "global::System.Buffers.ArrayPool";

    /// <summary>The element type the column is decoded into before it becomes a lane.</summary>
    private static string DecodeType(LeafColumn col) =>
        BufferPoolComponent.GetBufferElementType(col.Leaf);

    private static string LaneType(LeafColumn col) =>
        ColumnarBatchComponent.LaneElementType(col.Leaf);

    private static bool IsNullable(LeafColumn col) => col.Leaf.IsNullable;

    /// <summary>
    /// Rents the buffers a row group is decoded into. Strings and byte arrays rent a second array
    /// for the lane that wraps the decoded values.
    /// </summary>
    private static void EmitRentals(StringBuilder builder, EmissionPlan plan, string indent)
    {
        foreach (LeafColumn col in plan.Columns)
        {
            int slot = col.Slot;
            switch (ColumnarBatchComponent.KindOf(col.Leaf))
            {
                case ColumnarBatchComponent.LaneKind.Packed:
                    builder.AppendLine(
                        $"{indent}var buffer_{slot} = {Pool}<{LaneType(col)}>.Shared.Rent(rowCount);"
                    );
                    builder.AppendLine(
                        $"{indent}var defLevels_{slot} = {Pool}<int>.Shared.Rent(rowCount);"
                    );
                    break;
                case ColumnarBatchComponent.LaneKind.Direct:
                    builder.AppendLine(
                        $"{indent}var buffer_{slot} = {Pool}<{DecodeType(col)}>.Shared.Rent(rowCount);"
                    );
                    break;
                default:
                    builder.AppendLine(
                        $"{indent}var buffer_{slot} = {Pool}<{DecodeType(col)}>.Shared.Rent(rowCount);"
                    );
                    builder.AppendLine(
                        $"{indent}var lane_{slot} = {Pool}<{LaneType(col)}>.Shared.Rent(rowCount);"
                    );
                    break;
            }
        }
    }

    /// <summary>Returns every rented buffer. Reference-holding buffers are cleared on the way back.</summary>
    private static void EmitReturns(StringBuilder builder, EmissionPlan plan, string indent)
    {
        foreach (LeafColumn col in plan.Columns)
        {
            int slot = col.Slot;
            switch (ColumnarBatchComponent.KindOf(col.Leaf))
            {
                case ColumnarBatchComponent.LaneKind.Packed:
                    builder.AppendLine(
                        $"{indent}{Pool}<{LaneType(col)}>.Shared.Return(buffer_{slot}, clearArray: false);"
                    );
                    builder.AppendLine(
                        $"{indent}{Pool}<int>.Shared.Return(defLevels_{slot}, clearArray: false);"
                    );
                    break;
                case ColumnarBatchComponent.LaneKind.Direct:
                    builder.AppendLine(
                        $"{indent}{Pool}<{DecodeType(col)}>.Shared.Return(buffer_{slot}, clearArray: false);"
                    );
                    break;
                default:
                    builder.AppendLine(
                        $"{indent}{Pool}<{DecodeType(col)}>.Shared.Return(buffer_{slot}, clearArray: true);"
                    );
                    builder.AppendLine(
                        $"{indent}{Pool}<{LaneType(col)}>.Shared.Return(lane_{slot}, clearArray: true);"
                    );
                    break;
            }
        }
    }

    /// <summary>
    /// The statements that decode one column of the current row group. A packed column is read raw;
    /// every other column uses the decode the POCO readers share, followed for text and binary by a
    /// wrap into the lane.
    /// </summary>
    private static void EmitColumnRead(
        StringBuilder builder,
        LeafColumn col,
        System.Action<StringBuilder, LeafColumn, string, string> emitColumnRead
    )
    {
        const string indent = "                ";
        int slot = col.Slot;
        switch (ColumnarBatchComponent.KindOf(col.Leaf))
        {
            case ColumnarBatchComponent.LaneKind.Packed:
                builder.AppendLine(
                    $"{indent}ValidateDictionaryEntryLimit(groupReader, stream, field_{slot}, \"{col.Leaf.Name}\", options, missing_{slot});"
                );
                builder.AppendLine(
                    $"{indent}ValidateChunkValueCount(groupReader, field_{slot}, rowCount, \"{col.Leaf.Name}\", missing_{slot});"
                );
                builder.AppendLine(
                    $"{indent}int packed_{slot} = await ReadPackedColumnAsync<{LaneType(col)}>(groupReader, field_{slot}, missing_{slot}, buffer_{slot}, defLevels_{slot}, rowCount, cancellationToken).ConfigureAwait(false);"
                );
                break;
            case ColumnarBatchComponent.LaneKind.Direct:
                emitColumnRead(builder, col, $"field_{slot}", $"buffer_{slot}");
                break;
            default:
                emitColumnRead(builder, col, $"field_{slot}", $"buffer_{slot}");
                builder.AppendLine(
                    $"{indent}{LaneWrapHelper(col)}(buffer_{slot}, lane_{slot}, rowCount);"
                );
                break;
        }
    }

    private static string LaneWrapHelper(LeafColumn col)
    {
        bool text = ColumnarBatchComponent.KindOf(col.Leaf) == ColumnarBatchComponent.LaneKind.Text;
        return (text, IsNullable(col)) switch
        {
            (true, true) => "WrapNullableText",
            (true, false) => "WrapText",
            (false, true) => "WrapNullableBinary",
            _ => "WrapBinary",
        };
    }

    /// <summary>The constructor argument for a column: its lane, plus its levels when packed.</summary>
    private static void AppendLaneArguments(StringBuilder builder, LeafColumn col)
    {
        int slot = col.Slot;
        string lane = $"global::System.ReadOnlyMemory<{LaneType(col)}>";
        switch (ColumnarBatchComponent.KindOf(col.Leaf))
        {
            case ColumnarBatchComponent.LaneKind.Packed:
                builder.Append(
                    $",\n                    new {lane}(buffer_{slot}, 0, packed_{slot}), new global::System.ReadOnlyMemory<int>(defLevels_{slot}, 0, rowCount)"
                );
                break;
            case ColumnarBatchComponent.LaneKind.Direct:
                builder.Append($",\n                    new {lane}(buffer_{slot}, 0, rowCount)");
                break;
            default:
                builder.Append($",\n                    new {lane}(lane_{slot}, 0, rowCount)");
                break;
        }
    }

    /// <summary>
    /// Emits the private helpers the batch reader calls once per column, so the iterator's
    /// complexity is bounded by the model's column shapes rather than its column count (#552).
    /// Only the helpers the model uses are emitted.
    /// </summary>
    private static void EmitHelpers(StringBuilder builder, EmissionPlan plan)
    {
        bool packed = false;
        bool text = false;
        bool nullableText = false;
        bool binary = false;
        bool nullableBinary = false;
        foreach (LeafColumn col in plan.Columns)
        {
            switch (ColumnarBatchComponent.KindOf(col.Leaf))
            {
                case ColumnarBatchComponent.LaneKind.Packed:
                    packed = true;
                    break;
                case ColumnarBatchComponent.LaneKind.Text:
                    text |= !IsNullable(col);
                    nullableText |= IsNullable(col);
                    break;
                case ColumnarBatchComponent.LaneKind.Binary:
                    binary |= !IsNullable(col);
                    nullableBinary |= IsNullable(col);
                    break;
                default:
                    // Direct columns decode in place and need no helper.
                    break;
            }
        }

        if (packed)
        {
            EmitReadPackedColumn(builder);
        }

        if (text)
        {
            EmitWrap(builder, "WrapText", "string?", "global::System.ReadOnlyMemory<char>", false);
        }

        if (nullableText)
        {
            EmitWrap(
                builder,
                "WrapNullableText",
                "string?",
                "global::System.ReadOnlyMemory<char>?",
                true
            );
        }

        if (binary)
        {
            EmitWrap(
                builder,
                "WrapBinary",
                "byte[]?",
                "global::System.ReadOnlyMemory<byte>",
                false
            );
        }

        if (nullableBinary)
        {
            EmitWrap(
                builder,
                "WrapNullableBinary",
                "byte[]?",
                "global::System.ReadOnlyMemory<byte>?",
                true
            );
        }
    }

    /// <summary>
    /// Reads a nullable value column in the packed + definition-level layout: the decoder's own
    /// shape, so there is no expansion pass and no copy. An absent or entirely null column answers
    /// with all-zero levels and no page read, as the POCO path does.
    /// </summary>
    private static void EmitReadPackedColumn(StringBuilder builder)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Reads a nullable value column as packed non-null values plus one definition level per row, and"
        );
        builder.AppendLine("    /// returns the number of packed values.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    private static async global::System.Threading.Tasks.ValueTask<int> ReadPackedColumnAsync<T>("
        );
        builder.AppendLine("        global::Parquet.ParquetRowGroupReader groupReader,");
        builder.AppendLine("        global::Parquet.Schema.DataField field,");
        builder.AppendLine("        bool missing,");
        builder.AppendLine("        T[] values,");
        builder.AppendLine("        int[] levels,");
        builder.AppendLine("        int rowCount,");
        builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
        builder.AppendLine("        where T : struct");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        var chunkStats = missing || rowCount == 0 ? null : groupReader.GetStatistics(field);"
        );
        builder.AppendLine(
            "        if (missing || rowCount == 0 || chunkStats?.NullCount == rowCount)"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            global::System.Array.Clear(levels, 0, rowCount);");
        builder.AppendLine("            return 0;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        if (field.MaxDefinitionLevel == 0)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            // The file stores the column as required: every row is present."
        );
        builder.AppendLine("            await groupReader.ReadAsync<T>(");
        builder.AppendLine("                field,");
        builder.AppendLine("                new global::System.Memory<T>(values, 0, rowCount),");
        builder.AppendLine(
            "                cancellationToken: cancellationToken).ConfigureAwait(false);"
        );
        builder.AppendLine("            global::System.Array.Fill(levels, 1, 0, rowCount);");
        builder.AppendLine("            return rowCount;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        await groupReader.ReadRawAsync<T>(");
        builder.AppendLine("            field,");
        builder.AppendLine("            new global::System.Memory<T>(values, 0, rowCount),");
        builder.AppendLine("            new global::System.Memory<int>(levels, 0, rowCount),");
        builder.AppendLine("            null,");
        builder.AppendLine("            cancellationToken).ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine("        int packed = 0;");
        builder.AppendLine("        for (int i = 0; i < rowCount; i++)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (levels[i] != 0)");
        builder.AppendLine("            {");
        builder.AppendLine("                packed++;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return packed;");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>
    /// Wraps decoded strings or byte arrays as inline-nullable memories. The memories alias the
    /// decoded objects, which the garbage collector owns, so the wrap needs no lifetime of its own.
    /// </summary>
    private static void EmitWrap(
        StringBuilder builder,
        string name,
        string sourceElement,
        string destinationElement,
        bool preserveNull
    )
    {
        builder.AppendLine(
            $"    private static void {name}({sourceElement}[] source, {destinationElement}[] destination, int rowCount)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        for (int i = 0; i < rowCount; i++)");
        builder.AppendLine("        {");
        if (preserveNull)
        {
            string converter = sourceElement.StartsWith("string", System.StringComparison.Ordinal)
                ? "AsColumnarText"
                : "AsColumnarBinary";
            builder.AppendLine($"            destination[i] = {converter}(source[i]);");
        }
        else
        {
            builder.AppendLine(
                "            destination[i] = global::System.MemoryExtensions.AsMemory(source[i]);"
            );
        }

        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>
    /// Emits <c>ReadBatchesCoreAsync</c> — one batch per row group, no domain object ever
    /// constructed. <paramref name="emitColumnRead"/> is the shared per-column decode the POCO
    /// readers use, so there is no second copy of the buffer logic for the columns it serves.
    /// </summary>
    public static void EmitReadBatchesAsync(
        StringBuilder builder,
        TargetClassModel model,
        System.Action<StringBuilder, LeafColumn, string, string> emitColumnRead
    )
    {
        EmissionPlan plan = EmissionPlan.For(model);
        string batchType = ColumnarBatchComponent.BatchTypeName(model);
        string leaseType = $"{batchType}.{ColumnarBatchComponent.LeaseTypeName(model)}";

        EmitHelpers(builder, plan);

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
            $"    /// Each yielded <see cref=\"{batchType}\"/> is borrowed: its lanes alias pooled buffers that are returned as"
        );
        builder.AppendLine(
            "    /// soon as the enumerator advances or is disposed, after which the batch throws on use. Use a batch, and"
        );
        builder.AppendLine(
            "    /// any lane taken from it, in the loop body only. Lanes of a borrowed batch are not guaranteed to be"
        );
        builder.AppendLine("    /// array-backed; a batch is not thread-safe.");
        builder.AppendLine("    /// </remarks>");
        // Not an iterator: arguments are validated when this is called, not on the first
        // MoveNextAsync (MA0050). The iterator below owns [EnumeratorCancellation].
        builder.AppendLine(
            $"    internal static global::System.Collections.Generic.IAsyncEnumerable<{batchType}> ReadBatchesCoreAsync("
        );
        builder.AppendLine("        global::System.IO.Stream stream,");
        builder.AppendLine(
            "        global::Parquet.SourceGenerator.ParquetSerializerOptions? options = null,"
        );
        builder.AppendLine(
            "        global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(stream);");
        builder.AppendLine();
        builder.AppendLine(
            "        return ReadBatchesIteratorAsync(stream, options, cancellationToken);"
        );
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            $"    private static async global::System.Collections.Generic.IAsyncEnumerable<{batchType}> ReadBatchesIteratorAsync("
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
            "        options ??= global::Parquet.SourceGenerator.ParquetSerializerOptions.Default;"
        );
        builder.AppendLine();
        // The same guarded prologue every other read path uses (#358): the decompression limits
        // apply to batch reads too.
        builder.AppendLine(
            "        using var guardedStream = CreateGuardedReadStream(stream, options);"
        );
        builder.AppendLine("        var reader = await global::Parquet.ParquetReader.CreateAsync(");
        builder.AppendLine("            guardedStream,");
        builder.AppendLine("            BuildFormatOptions(options),");
        builder.AppendLine(
            "            cancellationToken: cancellationToken).ConfigureAwait(false);"
        );
        // try/finally instead of an `await using` scope: see the CA1506 note on EmitReadAsync.
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine("        guardedStream.Activate();");
        builder.AppendLine(
            "        await ValidateReaderAsync(reader, stream, options, cancellationToken).ConfigureAwait(false);"
        );
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
        // One lease for the whole enumeration. Each batch records its generation; the finally below
        // bumps it before the buffers go back, which is what makes a kept batch throw.
        builder.AppendLine($"        var lease = new {leaseType}();");
        builder.AppendLine();
        builder.AppendLine("        for (int r = 0; r < reader.RowGroupCount; r++)");
        builder.AppendLine("        {");
        builder.AppendLine("            cancellationToken.ThrowIfCancellationRequested();");
        builder.AppendLine("            using var groupReader = reader.OpenRowGroupReader(r);");
        builder.AppendLine(
            "            int rowCount = ReadRowCount(groupReader.RowCount, r, options);"
        );
        builder.AppendLine(
            "            if (rowCount < 0 || rowCount > options.MaxAllocationValues)"
        );
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.IO.InvalidDataException($\"Row group {r} row count {rowCount} is invalid or exceeds maximum allowed {options.MaxAllocationValues}.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine();

        builder.AppendLine(
            $"            CheckAllocationBudget(rowCount, {ReadBudget.BytesPerRow(plan)}, options);"
        );
        EmitRentals(builder, plan, "            ");

        builder.AppendLine();
        builder.AppendLine("            try");
        builder.AppendLine("            {");

        foreach (LeafColumn col in plan.Columns)
        {
            EmitColumnRead(builder, col, emitColumnRead);
        }

        builder.AppendLine();
        builder.Append($"                yield return new {batchType}(lease, rowCount");
        foreach (LeafColumn col in plan.Columns)
        {
            AppendLaneArguments(builder, col);
        }
        builder.AppendLine(");");
        builder.AppendLine("            }");
        builder.AppendLine("            finally");
        builder.AppendLine("            {");
        builder.AppendLine("                lease.Expire();");

        EmitReturns(builder, plan, "                ");

        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("        }");
        builder.AppendLine("        finally");
        builder.AppendLine("        {");
        builder.AppendLine("            await reader.DisposeAsync().ConfigureAwait(false);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Asynchronously streams <c>{model.ClassName}</c> columnar batches from an in-memory byte buffer."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            $"    internal static async global::System.Collections.Generic.IAsyncEnumerable<{batchType}> ReadBatchesCoreAsync("
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
            "        await foreach (var batch in global::System.Threading.Tasks.TaskAsyncEnumerableExtensions.ConfigureAwait(ReadBatchesCoreAsync(stream, options, cancellationToken), false))"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            yield return batch;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }
}
