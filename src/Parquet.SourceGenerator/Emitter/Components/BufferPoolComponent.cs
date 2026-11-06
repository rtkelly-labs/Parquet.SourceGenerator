using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Composable emitter component for ArrayPool column buffer rental and return lifecycles.
/// </summary>
internal static class BufferPoolComponent
{
    public static bool UsesWriteAllParts(PropertyModel prop)
    {
        bool isString = prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string");
        bool isByteArray = prop.Kind == PropertyKind.ByteArray;
        return prop.IsNullable && !isString && !isByteArray;
    }

    public static string GetNonNullableBufferType(PropertyModel prop)
    {
        return prop.Kind switch
        {
            PropertyKind.Guid => "global::System.Guid",
            PropertyKind.DateOnly => "global::System.DateTime",
            PropertyKind.Enum => prop.EnumUnderlyingTypeName ?? "int",
            PropertyKind.TimeSpan => "int",
            PropertyKind.TimeOnly => "long",
            _ => prop.TypeName.TrimEnd('?'),
        };
    }

    public static string GetBufferElementType(PropertyModel prop)
    {
        return prop.Kind switch
        {
            PropertyKind.TimeSpan when prop.IsNullable => "int?",
            PropertyKind.TimeSpan => "int",
            PropertyKind.Guid when prop.IsNullable => "global::System.Guid?",
            PropertyKind.DateOnly when prop.IsNullable => "global::System.DateTime?",
            PropertyKind.DateOnly => "global::System.DateTime",
            PropertyKind.Guid => "global::System.Guid",
            PropertyKind.Enum when prop.IsNullable => $"{prop.EnumUnderlyingTypeName ?? "int"}?",
            PropertyKind.Enum => prop.EnumUnderlyingTypeName ?? "int",
            PropertyKind.TimeOnly when prop.IsNullable => "long?",
            PropertyKind.TimeOnly => "long",
            _ => prop.TypeName,
        };
    }

    public static string GetWriteBufferElementType(PropertyModel prop)
    {
        if (prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string"))
        {
            return prop.IsNullable
                ? "global::System.ReadOnlyMemory<char>?"
                : "global::System.ReadOnlyMemory<char>";
        }

        if (prop.Kind == PropertyKind.ByteArray)
        {
            return prop.IsNullable
                ? "global::System.ReadOnlyMemory<byte>?"
                : "global::System.ReadOnlyMemory<byte>";
        }

        return GetBufferElementType(prop);
    }

    public static bool IsReferenceTypeBuffer(PropertyModel prop, bool isWrite = false)
    {
        if (isWrite)
        {
            if (
                prop.Kind == PropertyKind.ByteArray
                || (prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string"))
            )
            {
                return true;
            }
        }

        return prop.Kind switch
        {
            PropertyKind.Guid => false,
            PropertyKind.ByteArray => true,
            PropertyKind.Primitive when prop.TypeName.Contains("string") => true,
            _ => false,
        };
    }

    /// <summary>
    /// Emits ArrayPool rentals for write property column buffers.
    /// For nullable value types, rentals include both packed non-null values and definition levels for WriteAllPartsAsync.
    /// </summary>
    public static void EmitWriteRentals(
        StringBuilder builder,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_",
        string indent = "        "
    )
    {
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            if (UsesWriteAllParts(prop))
            {
                string nonNullType = GetNonNullableBufferType(prop);
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
                string bufType = GetWriteBufferElementType(prop);
                builder.AppendLine(
                    $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{bufType}>.Shared.Rent({sizeExpr});"
                );
            }
        }
    }

    /// <summary>
    /// Emits eager ArrayPool return and nulling for a single write property column buffer immediately after writing.
    /// </summary>
    public static void EmitSingleWriteReturn(
        StringBuilder builder,
        PropertyModel prop,
        int propIndex,
        string varPrefix = "buffer_",
        string indent = "                "
    )
    {
        if (UsesWriteAllParts(prop))
        {
            string nonNullType = GetNonNullableBufferType(prop);
            builder.AppendLine(
                $"{indent}global::System.Buffers.ArrayPool<{nonNullType}>.Shared.Return({varPrefix}{propIndex}, clearArray: false);"
            );
            builder.AppendLine($"{indent}{varPrefix}{propIndex} = null!;");
            builder.AppendLine(
                $"{indent}global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{propIndex}, clearArray: false);"
            );
            builder.AppendLine($"{indent}defLevels_{propIndex} = null!;");
        }
        else
        {
            string bufType = GetWriteBufferElementType(prop);
            bool isRef = IsReferenceTypeBuffer(prop, isWrite: true);
            string clearArg = isRef ? "clearArray: true" : "clearArray: false";
            builder.AppendLine(
                $"{indent}global::System.Buffers.ArrayPool<{bufType}>.Shared.Return({varPrefix}{propIndex}, {clearArg});"
            );
            builder.AppendLine($"{indent}{varPrefix}{propIndex} = null!;");
        }
    }

    /// <summary>
    /// Emits ArrayPool returns for write property column buffers in the finally block with null checks
    /// for exception safety (handling any buffers that were not yet eagerly returned).
    /// </summary>
    public static void EmitWriteReturns(
        StringBuilder builder,
        TargetClassModel model,
        string varPrefix = "buffer_",
        string indent = "            "
    )
    {
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            if (UsesWriteAllParts(prop))
            {
                string nonNullType = GetNonNullableBufferType(prop);
                builder.AppendLine(
                    $"{indent}if ({varPrefix}{i} != null) global::System.Buffers.ArrayPool<{nonNullType}>.Shared.Return({varPrefix}{i}, clearArray: false);"
                );
                builder.AppendLine(
                    $"{indent}if (defLevels_{i} != null) global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{i}, clearArray: false);"
                );
            }
            else
            {
                string bufType = GetWriteBufferElementType(prop);
                bool isRef = IsReferenceTypeBuffer(prop, isWrite: true);
                string clearArg = isRef ? "clearArray: true" : "clearArray: false";
                builder.AppendLine(
                    $"{indent}if ({varPrefix}{i} != null) global::System.Buffers.ArrayPool<{bufType}>.Shared.Return({varPrefix}{i}, {clearArg});"
                );
            }
        }
    }

    /// <summary>
    /// Emits ArrayPool rentals for read property column buffers (always uses GetBufferElementType).
    /// </summary>
    public static void EmitRentals(
        StringBuilder builder,
        TargetClassModel model,
        string sizeExpr,
        string varPrefix = "buffer_",
        string indent = "        ",
        bool isWrite = false
    )
    {
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            string bufType = isWrite ? GetWriteBufferElementType(prop) : GetBufferElementType(prop);
            builder.AppendLine(
                $"{indent}var {varPrefix}{i} = global::System.Buffers.ArrayPool<{bufType}>.Shared.Rent({sizeExpr});"
            );
        }
    }

    /// <summary>
    /// Emits ArrayPool returns for read property column buffers.
    /// </summary>
    public static void EmitReturns(
        StringBuilder builder,
        TargetClassModel model,
        string varPrefix = "buffer_",
        string indent = "            ",
        bool isWrite = false
    )
    {
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            string bufType = isWrite ? GetWriteBufferElementType(prop) : GetBufferElementType(prop);
            bool isRef = IsReferenceTypeBuffer(prop, isWrite);
            string clearArg = isRef ? "clearArray: true" : "clearArray: false";
            builder.AppendLine(
                $"{indent}global::System.Buffers.ArrayPool<{bufType}>.Shared.Return({varPrefix}{i}, {clearArg});"
            );
        }
    }
}
