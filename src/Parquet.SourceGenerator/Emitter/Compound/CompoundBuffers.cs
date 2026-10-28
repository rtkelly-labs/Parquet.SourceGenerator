using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Compound;

/// <summary>
/// Buffer rental/return lifecycle for models containing compound members. v6-only by design:
/// the classic backend's compound support (issue #176 M5) needs the 4.x-aligned-array
/// conventions from docs/guides/nested-types.md §3, not these, so nothing here is shared source — the Legacy
/// project's source links never see it. For the compound-free properties inside a mixed
/// model, these mirror BufferPoolComponent's exact rental shapes so flat column output is
/// unchanged wherever it lands in the slot order.
/// </summary>
internal static class CompoundBuffers
{
    /// <summary>
    /// Buffer element type for a compound-path leaf: packed (no null holes), definition
    /// levels carry the ladder, so strings/binary take the struct-typed Memory views the
    /// level-based WriteAllPartsAsync/ReadRawAsync require (docs/guides/nested-types.md §2.1).
    /// </summary>
    public static string GetPackedType(PropertyModel leaf)
    {
        if (leaf.Kind == PropertyKind.Primitive && leaf.TypeName.Contains("string"))
            return "global::System.ReadOnlyMemory<char>";
        if (leaf.Kind == PropertyKind.ByteArray)
            return "global::System.ReadOnlyMemory<byte>";
        return BufferPoolComponent.GetNonNullableBufferType(leaf);
    }

    public static void EmitWriteRentals(
        IndentedWriter writer,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_"
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsListLeaf)
            {
                writer.Line(
                    $"var {varPrefix}{i} = global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Rent({sizeExpr});"
                );
                writer.Line(
                    $"var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                writer.Line(
                    $"var repLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                writer.Line($"int nonNullCount_{i} = 0;");
                writer.Line($"int posCount_{i} = 0;");
            }
            else if (col.IsCompound)
            {
                writer.Line(
                    $"var {varPrefix}{i} = global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Rent({sizeExpr});"
                );
                writer.Line(
                    $"var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                writer.Line($"int nonNullCount_{i} = 0;");
            }
            else if (BufferPoolComponent.UsesWriteAllParts(col.Leaf))
            {
                string nonNullType = BufferPoolComponent.GetNonNullableBufferType(col.Leaf);
                writer.Line(
                    $"var {varPrefix}{i} = global::System.Buffers.ArrayPool<{nonNullType}>.Shared.Rent({sizeExpr});"
                );
                writer.Line(
                    $"var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                writer.Line($"int nonNullCount_{i} = 0;");
            }
            else
            {
                string bufType = BufferPoolComponent.GetWriteBufferElementType(col.Leaf);
                writer.Line(
                    $"var {varPrefix}{i} = global::System.Buffers.ArrayPool<{bufType}>.Shared.Rent({sizeExpr});"
                );
            }
        }
    }

    public static void EmitWriteRentals(
        StringBuilder builder,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_",
        string indent = "        "
    ) =>
        EmitWriteRentals(
            new IndentedWriter(builder) { IndentLevel = indent.Length / 4 },
            model,
            sizeExpr,
            varPrefix
        );

    public static void EmitSingleWriteReturn(
        IndentedWriter writer,
        LeafColumn col,
        string varPrefix = "buffer_"
    )
    {
        int propIndex = col.Slot;
        if (col.IsCompound || col.IsListLeaf)
        {
            bool growable = col.IsListLeaf;
            writer.Line(
                $"global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Return({varPrefix}{propIndex}, clearArray: true);"
            );
            writer.Line($"{varPrefix}{propIndex} = null!;");
            writer.Line(
                $"global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{propIndex}, clearArray: false);"
            );
            writer.Line($"defLevels_{propIndex} = null!;");
            if (growable)
            {
                writer.Line(
                    $"global::System.Buffers.ArrayPool<int>.Shared.Return(repLevels_{propIndex}, clearArray: false);"
                );
                writer.Line($"repLevels_{propIndex} = null!;");
            }
        }
        else
        {
            BufferPoolComponent.EmitSingleWriteReturn(writer, col.Leaf, propIndex, varPrefix);
        }
    }

    public static void EmitSingleWriteReturn(
        StringBuilder builder,
        LeafColumn col,
        string varPrefix = "buffer_",
        string indent = "                "
    ) =>
        EmitSingleWriteReturn(
            new IndentedWriter(builder) { IndentLevel = indent.Length / 4 },
            col,
            varPrefix
        );

    /// <summary>
    /// Emits the write-side <c>finally</c> returns. Each is one <c>ReturnPooledArray</c> call (the
    /// null guard lives in the helper), so the method's complexity does not grow with the column
    /// count (#552). Flat and compound columns share this one shape.
    /// </summary>
    public static void EmitWriteReturns(
        IndentedWriter writer,
        TargetClassModel model,
        string varPrefix = "buffer_"
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsCompound || col.IsListLeaf)
            {
                writer.Line($"ReturnPooledArray({varPrefix}{i}, clearArray: true);");
                writer.Line($"ReturnPooledArray(defLevels_{i}, clearArray: false);");
                if (col.IsListLeaf)
                    writer.Line($"ReturnPooledArray(repLevels_{i}, clearArray: false);");
            }
            else if (BufferPoolComponent.UsesWriteAllParts(col.Leaf))
            {
                writer.Line($"ReturnPooledArray({varPrefix}{i}, clearArray: false);");
                writer.Line($"ReturnPooledArray(defLevels_{i}, clearArray: false);");
            }
            else
            {
                bool isRef = BufferPoolComponent.IsReferenceTypeBuffer(col.Leaf, isWrite: true);
                string clearArg = isRef ? "clearArray: true" : "clearArray: false";
                writer.Line($"ReturnPooledArray({varPrefix}{i}, {clearArg});");
            }
        }
    }

    public static void EmitWriteReturns(
        StringBuilder builder,
        TargetClassModel model,
        string varPrefix = "buffer_",
        string indent = "            "
    ) =>
        EmitWriteReturns(
            new IndentedWriter(builder) { IndentLevel = indent.Length / 4 },
            model,
            varPrefix
        );

    public static void EmitRentals(
        IndentedWriter writer,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_",
        bool isWrite = false
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsCompound || col.IsListLeaf)
            {
                writer.Line(
                    $"var {varPrefix}{i} = global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Rent({sizeExpr});"
                );
                writer.Line(
                    $"var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                if (col.IsListLeaf)
                    writer.Line(
                        $"var repLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                    );
                continue;
            }
            string bufType = isWrite
                ? BufferPoolComponent.GetWriteBufferElementType(col.Leaf)
                : BufferPoolComponent.GetBufferElementType(col.Leaf);
            writer.Line(
                $"var {varPrefix}{i} = global::System.Buffers.ArrayPool<{bufType}>.Shared.Rent({sizeExpr});"
            );
        }
    }

    public static void EmitRentals(
        StringBuilder builder,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_",
        string indent = "        ",
        bool isWrite = false
    ) =>
        EmitRentals(
            new IndentedWriter(builder) { IndentLevel = indent.Length / 4 },
            model,
            sizeExpr,
            varPrefix,
            isWrite
        );

    public static void EmitReturns(
        IndentedWriter writer,
        TargetClassModel model,
        string varPrefix = "buffer_",
        bool isWrite = false
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsCompound || col.IsListLeaf)
            {
                writer.Line(
                    $"global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Return({varPrefix}{i}, clearArray: true);"
                );
                writer.Line(
                    $"global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{i}, clearArray: false);"
                );
                if (col.IsListLeaf)
                    writer.Line(
                        $"global::System.Buffers.ArrayPool<int>.Shared.Return(repLevels_{i}, clearArray: false);"
                    );
                continue;
            }
            string bufType = isWrite
                ? BufferPoolComponent.GetWriteBufferElementType(col.Leaf)
                : BufferPoolComponent.GetBufferElementType(col.Leaf);
            bool isRef = BufferPoolComponent.IsReferenceTypeBuffer(col.Leaf, isWrite);
            string clearArg = isRef ? "clearArray: true" : "clearArray: false";
            writer.Line(
                $"global::System.Buffers.ArrayPool<{bufType}>.Shared.Return({varPrefix}{i}, {clearArg});"
            );
        }
    }

    public static void EmitReturns(
        StringBuilder builder,
        TargetClassModel model,
        string varPrefix = "buffer_",
        string indent = "            ",
        bool isWrite = false
    ) =>
        EmitReturns(
            new IndentedWriter(builder) { IndentLevel = indent.Length / 4 },
            model,
            varPrefix,
            isWrite
        );
}
