using System;
using System.Linq;
using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter;

/// <summary>
/// Shared per-column helper methods for the v6 emitter (#552). The read and write bodies used to
/// repeat the same per-column fragment (dictionary-entry guard, all-null bypass, list-leaf buffer
/// sizing, packed string length guard, pooled-buffer return) inline for every column, so an
/// emitted method's cyclomatic complexity grew with the column count. Each fragment is now one
/// <c>private static</c> helper emitted once per class and called with one statement per column,
/// so a method is bounded by column shape rather than column count. The helpers capture nothing,
/// and the Task-returning ones are deliberately not <c>async</c>: they hand the underlying read
/// back to the caller's <c>await</c> with no state machine of their own.
/// </summary>
internal static class ColumnHelpersComponent
{
    /// <summary>The per-column value kinds the nullable read helpers dispatch on.</summary>
    public enum NullableReadShape
    {
        /// <summary>A nullable value type: <c>T?[]</c> read through <c>ReadAsync&lt;T&gt;</c>.</summary>
        Struct,
        String,
        ByteArray,
    }

    public static NullableReadShape GetNullableReadShape(PropertyModel prop)
    {
        if (prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string"))
            return NullableReadShape.String;
        return prop.Kind == PropertyKind.ByteArray
            ? NullableReadShape.ByteArray
            : NullableReadShape.Struct;
    }

    /// <summary>
    /// The <c>T</c> of <c>groupReader.ReadAsync&lt;T&gt;</c> for a flat value column: the wire type
    /// the buffer is read as, which differs from the CLR type for dates, enums and durations.
    /// </summary>
    public static string GetReadElementType(PropertyModel prop) =>
        prop.Kind switch
        {
            PropertyKind.Guid => "global::System.Guid",
            PropertyKind.DateOnly => "global::System.DateTime",
            PropertyKind.Enum => prop.EnumUnderlyingTypeName ?? "int",
            PropertyKind.TimeSpan => "int",
            PropertyKind.TimeOnly => "long",
            _ => prop.TypeName.TrimEnd('?'),
        };

    private static bool IsPackedString(LeafColumn col) =>
        col.Leaf.Kind == PropertyKind.Primitive
        && col.Leaf.TypeName.IndexOf("string", StringComparison.Ordinal) >= 0;

    private static bool IsFlatNullable(LeafColumn col) =>
        !col.IsCompound && !col.IsListLeaf && col.Leaf.IsNullable;

    private static bool AnyNullable(LeafColumn[] columns, NullableReadShape shape) =>
        columns.Any(c => IsFlatNullable(c) && GetNullableReadShape(c.Leaf) == shape);

    /// <summary>Emits every helper the model's columns call. Nothing is emitted for unused ones.</summary>
    public static void EmitHelpers(StringBuilder builder, TargetClassModel model)
    {
        // The compound lane, node and extraction helpers ride along with the shared ones: one
        // call site in the emitter hub keeps its fan-out under the ratchet.
        CompoundMapping.EmitReadHelpers(builder, model);
        CompoundMapping.EmitWriteHelpers(builder, model);

        if (!PropertyMappingComponent.IsSingleFieldBlittableStruct(model))
        {
            builder.AppendLine();
            EmitThrowIfSourceShorter(builder);
        }

        builder.AppendLine();
        AllocationBudgetComponent.EmitHelper(builder);

        LeafColumn[] columns = EmissionPlan.For(model).Columns;

        builder.AppendLine();
        EmitReadRowCount(builder);

        if (!RowGroupPruningComponent.IsEnabled(model))
        {
            builder.AppendLine();
            EmitSumRowGroupRows(builder);
        }

        if (columns.Any(c => c.Leaf.Kind == PropertyKind.TimeOnly))
        {
            builder.AppendLine();
            PropertyMappingComponent.EmitReadTimeOnlyHelper(builder);
        }
        if (columns.Length == 0)
            return;

        builder.AppendLine();
        EmitValidateDictionaryEntryLimit(builder);
        builder.AppendLine();
        EmitValidateChunkValueCount(builder);

        if (AnyNullable(columns, NullableReadShape.Struct))
        {
            builder.AppendLine();
            EmitReadNullableColumn(builder);
        }
        if (AnyNullable(columns, NullableReadShape.String))
        {
            builder.AppendLine();
            EmitReadNullableStringColumn(builder);
        }
        if (AnyNullable(columns, NullableReadShape.ByteArray))
        {
            builder.AppendLine();
            EmitReadNullableByteArrayColumn(builder);
        }

        if (columns.Any(c => c.IsListLeaf || c.IsCompound))
        {
            builder.AppendLine();
            EmitRequireNestedLeafPresent(builder);
        }

        if (columns.Any(c => c.IsListLeaf))
        {
            builder.AppendLine();
            EmitPrepareListLeafBuffers(builder);
        }

        if (columns.Any(c => (c.IsListLeaf || c.IsCompound) && IsPackedString(c)))
        {
            builder.AppendLine();
            EmitValidatePackedStringLengths(builder);
        }

        builder.AppendLine();
        EmitReturnPooledArray(builder);
    }

    /// <summary>
    /// The list fast path of the row-group writer walks a span without bounds checks, sized by the
    /// count read at method entry. This is the one comparison that makes a shrunken list an
    /// exception instead of a read past its backing array (#375). It is a helper so the unchecked
    /// loop function carries no branch of its own, and it exists only where that loop does.
    /// </summary>
    private static void EmitThrowIfSourceShorter(StringBuilder builder) =>
        AppendLines(
            builder,
            "#if NET6_0_OR_GREATER",
            "    private static void ThrowIfSourceShorter(int length, int count)",
            "    {",
            "        if (length < count)",
            "        {",
            "            throw new global::System.InvalidOperationException(\"Collection was modified during serialization.\");",
            "        }",
            "    }",
            "#endif"
        );

    /// <summary>
    /// Fails a read whose file lacks an optional column under a list or struct: schema resolution
    /// reports it as missing and hands back the generator's own template field, which the row group
    /// has no chunk for (#367).
    /// </summary>
    private static void EmitRequireNestedLeafPresent(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static void RequireNestedLeafPresent(bool missing, string columnName)",
            "    {",
            "        if (missing)",
            "        {",
            "            throw new global::System.IO.InvalidDataException($\"Column '{columnName}' is missing from the Parquet file; a column under a list or struct must be present.\");",
            "        }",
            "    }"
        );

    /// <summary>
    /// Narrows a row group's 64-bit row count with the range check done in <c>long</c> first, so a
    /// footer value above <c>int.MaxValue</c> is an <c>InvalidDataException</c> and not the
    /// <c>OverflowException</c> a checked cast raises before the limit is consulted (#371).
    /// </summary>
    private static void EmitReadRowCount(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static int ReadRowCount(long rowCount, int rowGroup, global::Parquet.SourceGenerator.ParquetSerializerOptions options)",
            "    {",
            "        if (rowCount < 0 || rowCount > options.MaxAllocationValues)",
            "        {",
            "            throw new global::System.IO.InvalidDataException($\"Row group {rowGroup} row count {rowCount} is invalid or exceeds maximum allowed {options.MaxAllocationValues}.\");",
            "        }",
            "",
            "        return (int)rowCount;",
            "    }"
        );

    /// <summary>
    /// Sums the row groups' 64-bit row counts for a read with no statistics to prune on, checking each
    /// and the total against <c>MaxAllocationValues</c> before narrowing to the <c>int</c> that sizes
    /// the result (#371). The pruned branch of the selection pass does the same inline.
    /// </summary>
    private static void EmitSumRowGroupRows(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static int SumRowGroupRows(global::Parquet.ParquetReader reader, global::Parquet.SourceGenerator.ParquetSerializerOptions options)",
            "    {",
            "        long total = 0;",
            "        for (int r = 0; r < reader.RowGroups.Count; r++)",
            "        {",
            "            long rowCount = reader.RowGroups[r].RowCount;",
            "            if (rowCount < 0 || rowCount > options.MaxAllocationValues)",
            "            {",
            "                throw new global::System.IO.InvalidDataException($\"Row group {r} row count {rowCount} is invalid or exceeds maximum allowed {options.MaxAllocationValues}.\");",
            "            }",
            "",
            "            total += rowCount;",
            "            if (total > options.MaxAllocationValues)",
            "            {",
            "                throw new global::System.IO.InvalidDataException($\"Total row count {total} exceeds maximum allowed {options.MaxAllocationValues}.\");",
            "            }",
            "        }",
            "",
            "        return (int)total;",
            "    }"
        );

    private static void AppendLines(StringBuilder builder, params string[] lines)
    {
        foreach (string line in lines)
            builder.AppendLine(line);
    }

    /// <summary>
    /// Rejects a column chunk whose value count differs from the row group's declared row count. The
    /// read buffers are rented for <c>rowCount</c> and Parquet.Net fills only the chunk's own
    /// <c>num_values</c>, so a footer that declares more rows would leave the tail of a pooled buffer
    /// unwritten and, for value types, uncleared (#382). Repeated columns are excluded by the caller.
    /// </summary>
    private static void EmitValidateChunkValueCount(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static void ValidateChunkValueCount(",
            "        global::Parquet.ParquetRowGroupReader groupReader,",
            "        global::Parquet.Schema.DataField field,",
            "        int rowCount,",
            "        string columnName,",
            "        bool missing = false)",
            "    {",
            "        if (missing)",
            "        {",
            "            return;",
            "        }",
            "        long numValues = groupReader.GetMetadata(field).MetaData.NumValues;",
            "        if (numValues != rowCount)",
            "        {",
            "            throw new global::System.IO.InvalidDataException($\"Column '{columnName}' holds {numValues} values but its row group declares {rowCount} rows.\");",
            "        }",
            "    }"
        );

    private static void EmitValidateDictionaryEntryLimit(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static void ValidateDictionaryEntryLimit(",
            "        global::Parquet.ParquetRowGroupReader groupReader,",
            "        global::System.IO.Stream stream,",
            "        global::Parquet.Schema.DataField field,",
            "        string columnName,",
            "        global::Parquet.SourceGenerator.ParquetSerializerOptions options,",
            "        bool missing = false)",
            "    {",
            "        if (missing)",
            "        {",
            "            return;",
            "        }",
            "        var metadata = groupReader.GetMetadata(field).MetaData;",
            "        bool dictionaryEncoded = false;",
            "        foreach (var encoding in metadata.Encodings)",
            "        {",
            "            if (encoding == global::Parquet.Meta.Encoding.PLAIN_DICTIONARY || encoding == global::Parquet.Meta.Encoding.RLE_DICTIONARY)",
            "            {",
            "                dictionaryEncoded = true;",
            "                break;",
            "            }",
            "        }",
            "        int? dictionaryEntries = dictionaryEncoded ? ReadDictionaryEntryCount(groupReader, stream, field) : null;",
            "        if (dictionaryEntries.HasValue && dictionaryEntries.Value > options.MaxDictionaryEntries)",
            "        {",
            "            throw new global::System.IO.InvalidDataException($\"Dictionary column '{columnName}' entry count {dictionaryEntries.Value} exceeds maximum allowed {options.MaxDictionaryEntries}.\");",
            "        }",
            "    }"
        );

    private static void EmitReadNullableColumn(StringBuilder builder) =>
        AppendLines(
            builder,
            "    // The column is absent from the file (optional-column schema evolution) or the chunk is",
            "    // entirely null: either way the answer is nulls, with no page read, decompression or decoding.",
            "    private static global::System.Threading.Tasks.ValueTask ReadNullableColumnAsync<T>(",
            "        global::Parquet.ParquetRowGroupReader groupReader,",
            "        global::Parquet.Schema.DataField field,",
            "        bool missing,",
            "        T?[] buffer,",
            "        int rowCount,",
            "        global::System.Threading.CancellationToken cancellationToken)",
            "        where T : struct",
            "    {",
            "        var chunkStats = missing ? null : groupReader.GetStatistics(field);",
            "        if (missing || chunkStats?.NullCount == rowCount)",
            "        {",
            "            global::System.Array.Clear(buffer, 0, rowCount);",
            "            return default;",
            "        }",
            "        return groupReader.ReadAsync<T>(",
            "            field,",
            "            new global::System.Memory<T?>(buffer, 0, rowCount),",
            "            cancellationToken: cancellationToken);",
            "    }"
        );

    private static void EmitReadNullableStringColumn(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static global::System.Threading.Tasks.ValueTask ReadNullableStringColumnAsync(",
            "        global::Parquet.ParquetRowGroupReader groupReader,",
            "        global::Parquet.Schema.DataField field,",
            "        bool missing,",
            "        string?[] buffer,",
            "        int rowCount,",
            "        bool deduplicateStrings,",
            "        StringDeduplicator deduplicator,",
            "        int maxStringLengthBytes,",
            "        global::System.Threading.CancellationToken cancellationToken)",
            "    {",
            "        var chunkStats = missing ? null : groupReader.GetStatistics(field);",
            "        if (missing || chunkStats?.NullCount == rowCount)",
            "        {",
            "            global::System.Array.Clear(buffer, 0, rowCount);",
            "            return default;",
            "        }",
            "        return ReadBoundedStringColumnAsync(",
            "            groupReader,",
            "            field,",
            "            buffer,",
            "            rowCount,",
            "            deduplicateStrings,",
            "            deduplicator,",
            "            maxStringLengthBytes,",
            "            cancellationToken);",
            "    }"
        );

    private static void EmitReadNullableByteArrayColumn(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static global::System.Threading.Tasks.ValueTask ReadNullableByteArrayColumnAsync(",
            "        global::Parquet.ParquetRowGroupReader groupReader,",
            "        global::Parquet.Schema.DataField field,",
            "        bool missing,",
            "        byte[]?[] buffer,",
            "        int rowCount,",
            "        global::System.Threading.CancellationToken cancellationToken)",
            "    {",
            "        var chunkStats = missing ? null : groupReader.GetStatistics(field);",
            "        if (missing || chunkStats?.NullCount == rowCount)",
            "        {",
            "            global::System.Array.Clear(buffer, 0, rowCount);",
            "            return default;",
            "        }",
            "        return groupReader.ReadAsync(",
            "            field,",
            "            new global::System.Memory<byte[]?>(buffer, 0, rowCount),",
            "            cancellationToken: cancellationToken);",
            "    }"
        );

    private static void EmitPrepareListLeafBuffers(StringBuilder builder) =>
        AppendLines(
            builder,
            "    // Entries run ahead of rowCount for multi-element lists: size from the column",
            "    // metadata and re-rent on growth (docs/guides/nested-types.md §2.3: the values buffer must cover",
            "    // NumValues, the packed lane follows inside it).",
            "    private static int PrepareListLeafBuffers<T>(",
            "        global::Parquet.ParquetRowGroupReader groupReader,",
            "        global::Parquet.Schema.DataField field,",
            "        string columnName,",
            "        global::Parquet.SourceGenerator.ParquetSerializerOptions options,",
            "        ref long allocatedBytes,",
            "        ref T[] buffer,",
            "        ref int[] defLevels,",
            "        ref int[] repLevels)",
            "    {",
            "        var entries = checked((int)groupReader.GetMetadata(field).MetaData.NumValues);",
            "        if (entries < 0 || entries > options.MaxAllocationValues)",
            "        {",
            "            throw new global::System.IO.InvalidDataException($\"Column '{columnName}' NumValues ({entries}) is invalid or exceeds maximum allowed {options.MaxAllocationValues}.\");",
            "        }",
            "        if (entries > defLevels.Length)",
            "        {",
            "            allocatedBytes = CheckAllocationBudget(allocatedBytes, entries - defLevels.Length, 24, options);",
            "            var newDefLevels = global::System.Buffers.ArrayPool<int>.Shared.Rent(entries);",
            "            global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels, clearArray: false);",
            "            defLevels = newDefLevels;",
            "            var newRepLevels = global::System.Buffers.ArrayPool<int>.Shared.Rent(entries);",
            "            global::System.Buffers.ArrayPool<int>.Shared.Return(repLevels, clearArray: false);",
            "            repLevels = newRepLevels;",
            "            var newValues = global::System.Buffers.ArrayPool<T>.Shared.Rent(entries);",
            "            global::System.Buffers.ArrayPool<T>.Shared.Return(buffer, clearArray: true);",
            "            buffer = newValues;",
            "        }",
            "        return entries;",
            "    }"
        );

    private static void EmitValidatePackedStringLengths(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static void ValidatePackedStringLengths(",
            "        global::System.ReadOnlyMemory<char>[] buffer,",
            "        int[] defLevels,",
            "        int entries,",
            "        int maxDefinitionLevel,",
            "        string levelColumnName,",
            "        string stringColumnName,",
            "        global::Parquet.SourceGenerator.ParquetSerializerOptions options)",
            "    {",
            "        int packedString = 0;",
            "        for (int levelIndex = 0; levelIndex < entries; levelIndex++)",
            "        {",
            "            if (defLevels[levelIndex] != maxDefinitionLevel) continue;",
            "            if (packedString >= entries)",
            "            {",
            "                throw new global::System.IO.InvalidDataException($\"Definition levels in column '{levelColumnName}' exceeded values count.\");",
            "            }",
            "            int stringByteCount = global::System.Text.Encoding.UTF8.GetByteCount(buffer[packedString++].Span);",
            "            if (stringByteCount > options.MaxStringLengthBytes)",
            "            {",
            "                throw new global::System.IO.InvalidDataException($\"String column '{stringColumnName}' value at index {levelIndex} UTF-8 length {stringByteCount} exceeds maximum allowed {options.MaxStringLengthBytes}.\");",
            "            }",
            "        }",
            "    }"
        );

    private static void EmitReturnPooledArray(StringBuilder builder) =>
        AppendLines(
            builder,
            "    private static void ReturnPooledArray<T>(T[]? array, bool clearArray)",
            "    {",
            "        if (array != null)",
            "        {",
            "            global::System.Buffers.ArrayPool<T>.Shared.Return(array, clearArray);",
            "        }",
            "    }"
        );
}
