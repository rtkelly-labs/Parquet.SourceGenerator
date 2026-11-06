using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Compound;

/// <summary>
/// Buffer rental/return lifecycle for models containing compound members. v6-only by design:
/// the classic backend's compound support (issue #176 M5) needs the 4.x-aligned-array
/// conventions from docs/15 §3, not these, so nothing here is shared source — the Legacy
/// project's source links never see it. For the compound-free properties inside a mixed
/// model, these mirror BufferPoolComponent's exact rental shapes so flat column output is
/// unchanged wherever it lands in the slot order.
/// </summary>
internal static class CompoundBuffers
{
    /// <summary>
    /// Buffer element type for a compound-path leaf: packed (no null holes), definition
    /// levels carry the ladder, so strings/binary take the struct-typed Memory views the
    /// level-based WriteAllPartsAsync/ReadRawAsync require (docs/15 §2.1).
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
        StringBuilder builder,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_",
        string indent = "        "
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsListLeaf)
            {
                builder.AppendLine(
                    $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine(
                    $"{indent}var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine(
                    $"{indent}var repLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine($"{indent}int nonNullCount_{i} = 0;");
                builder.AppendLine($"{indent}int posCount_{i} = 0;");
            }
            else if (col.IsCompound)
            {
                builder.AppendLine(
                    $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine(
                    $"{indent}var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine($"{indent}int nonNullCount_{i} = 0;");
            }
            else if (BufferPoolComponent.UsesWriteAllParts(col.Leaf))
            {
                string nonNullType = BufferPoolComponent.GetNonNullableBufferType(col.Leaf);
                builder.AppendLine(
                    $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{nonNullType}>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine(
                    $"{indent}var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine($"{indent}int nonNullCount_{i} = 0;");
            }
            else
            {
                string bufType = BufferPoolComponent.GetWriteBufferElementType(col.Leaf);
                builder.AppendLine(
                    $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{bufType}>.Shared.Rent({sizeExpr});"
                );
            }
        }
    }

    public static void EmitSingleWriteReturn(
        StringBuilder builder,
        LeafColumn col,
        string varPrefix = "buffer_",
        string indent = "                "
    )
    {
        int propIndex = col.Slot;
        if (col.IsCompound || col.IsListLeaf)
        {
            bool growable = col.IsListLeaf;
            builder.AppendLine(
                $"{indent}global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Return({varPrefix}{propIndex}, clearArray: true);"
            );
            builder.AppendLine($"{indent}{varPrefix}{propIndex} = null!;");
            builder.AppendLine(
                $"{indent}global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{propIndex}, clearArray: false);"
            );
            builder.AppendLine($"{indent}defLevels_{propIndex} = null!;");
            if (growable)
            {
                builder.AppendLine(
                    $"{indent}global::System.Buffers.ArrayPool<int>.Shared.Return(repLevels_{propIndex}, clearArray: false);"
                );
                builder.AppendLine($"{indent}repLevels_{propIndex} = null!;");
            }
        }
        else
        {
            BufferPoolComponent.EmitSingleWriteReturn(
                builder,
                col.Leaf,
                propIndex,
                varPrefix,
                indent
            );
        }
    }

    public static void EmitWriteReturns(
        StringBuilder builder,
        TargetClassModel model,
        string varPrefix = "buffer_",
        string indent = "            "
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsCompound || col.IsListLeaf)
            {
                builder.AppendLine(
                    $"{indent}if ({varPrefix}{i} != null) global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Return({varPrefix}{i}, clearArray: true);"
                );
                builder.AppendLine(
                    $"{indent}if (defLevels_{i} != null) global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{i}, clearArray: false);"
                );
                if (col.IsListLeaf)
                    builder.AppendLine(
                        $"{indent}if (repLevels_{i} != null) global::System.Buffers.ArrayPool<int>.Shared.Return(repLevels_{i}, clearArray: false);"
                    );
            }
            else if (BufferPoolComponent.UsesWriteAllParts(col.Leaf))
            {
                string nonNullType = BufferPoolComponent.GetNonNullableBufferType(col.Leaf);
                builder.AppendLine(
                    $"{indent}if ({varPrefix}{i} != null) global::System.Buffers.ArrayPool<{nonNullType}>.Shared.Return({varPrefix}{i}, clearArray: false);"
                );
                builder.AppendLine(
                    $"{indent}if (defLevels_{i} != null) global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{i}, clearArray: false);"
                );
            }
            else
            {
                string bufType = BufferPoolComponent.GetWriteBufferElementType(col.Leaf);
                bool isRef = BufferPoolComponent.IsReferenceTypeBuffer(col.Leaf, isWrite: true);
                string clearArg = isRef ? "clearArray: true" : "clearArray: false";
                builder.AppendLine(
                    $"{indent}if ({varPrefix}{i} != null) global::System.Buffers.ArrayPool<{bufType}>.Shared.Return({varPrefix}{i}, {clearArg});"
                );
            }
        }
    }

    public static void EmitRentals(
        StringBuilder builder,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_",
        string indent = "        ",
        bool isWrite = false
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsCompound || col.IsListLeaf)
            {
                builder.AppendLine(
                    $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Rent({sizeExpr});"
                );
                builder.AppendLine(
                    $"{indent}var defLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                );
                if (col.IsListLeaf)
                    builder.AppendLine(
                        $"{indent}var repLevels_{i} = global::System.Buffers.ArrayPool<int>.Shared.Rent({sizeExpr});"
                    );
                continue;
            }
            string bufType = isWrite
                ? BufferPoolComponent.GetWriteBufferElementType(col.Leaf)
                : BufferPoolComponent.GetBufferElementType(col.Leaf);
            builder.AppendLine(
                $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{bufType}>.Shared.Rent({sizeExpr});"
            );
        }
    }

    public static void EmitReturns(
        StringBuilder builder,
        TargetClassModel model,
        string varPrefix = "buffer_",
        string indent = "            ",
        bool isWrite = false
    )
    {
        foreach (LeafColumn col in EmissionPlan.For(model).Columns)
        {
            int i = col.Slot;
            if (col.IsCompound || col.IsListLeaf)
            {
                builder.AppendLine(
                    $"{indent}global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Return({varPrefix}{i}, clearArray: true);"
                );
                builder.AppendLine(
                    $"{indent}global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{i}, clearArray: false);"
                );
                if (col.IsListLeaf)
                    builder.AppendLine(
                        $"{indent}global::System.Buffers.ArrayPool<int>.Shared.Return(repLevels_{i}, clearArray: false);"
                    );
                continue;
            }
            string bufType = isWrite
                ? BufferPoolComponent.GetWriteBufferElementType(col.Leaf)
                : BufferPoolComponent.GetBufferElementType(col.Leaf);
            bool isRef = BufferPoolComponent.IsReferenceTypeBuffer(col.Leaf, isWrite);
            string clearArg = isRef ? "clearArray: true" : "clearArray: false";
            builder.AppendLine(
                $"{indent}global::System.Buffers.ArrayPool<{bufType}>.Shared.Return({varPrefix}{i}, {clearArg});"
            );
        }
    }
}
