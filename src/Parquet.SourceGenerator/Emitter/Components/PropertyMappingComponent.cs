using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Composable emitter component for property extraction (writing) and object materialization (reading).
/// </summary>
internal static class PropertyMappingComponent
{
    private static readonly global::System.Collections.Generic.HashSet<string> BlittablePrimitiveTypeNames =
        new(global::System.StringComparer.Ordinal)
        {
            "sbyte",
            "System.SByte",
            "global::System.SByte",
            "byte",
            "System.Byte",
            "global::System.Byte",
            "short",
            "System.Int16",
            "global::System.Int16",
            "ushort",
            "System.UInt16",
            "global::System.UInt16",
            "int",
            "System.Int32",
            "global::System.Int32",
            "uint",
            "System.UInt32",
            "global::System.UInt32",
            "long",
            "System.Int64",
            "global::System.Int64",
            "ulong",
            "System.UInt64",
            "global::System.UInt64",
            "float",
            "System.Single",
            "global::System.Single",
            "double",
            "System.Double",
            "global::System.Double",
            "bool",
            "System.Boolean",
            "global::System.Boolean",
        };

    /// <summary>
    /// Returns true if the model is an unmanaged/blittable struct containing exactly one non-nullable primitive property.
    /// In this case, the in-memory array layout of TStruct[] is 100% bit-for-bit identical to TField[], enabling
    /// zero-copy hardware memory copying via MemoryMarshal.Cast.
    /// </summary>
    public static bool IsSingleFieldBlittableStruct(TargetClassModel model)
    {
        if (
            !model.IsValueType
            || !model.IsUnmanaged
            || !model.HasSingleInstanceField
            || model.Properties.Length != 1
        )
            return false;

        PropertyModel prop = model.Properties[0];
        if (prop.IsNullable)
            return false;

        return prop.Kind == PropertyKind.Primitive
            && BlittablePrimitiveTypeNames.Contains(prop.TypeName);
    }

    /// <summary>
    /// Returns the buffer element type of the single primitive field in a single-field blittable struct.
    /// </summary>
    public static string GetSingleFieldBufferElementType(TargetClassModel model)
    {
        return model.Properties[0].TypeName;
    }

    /// <summary>
    /// Emits either a zero-copy MemoryMarshal.Cast block (if single-field blittable struct) or a standard loop calling EmitObjectMaterialization.
    /// </summary>
    public static void EmitArrayMaterialization(
        StringBuilder builder,
        TargetClassModel model,
        string targetArrayVar,
        string startOffsetVar,
        string rowCountVar = "rowCount",
        string indexVar = "i",
        string bufferPrefix = "buffer_",
        string indent = "                "
    )
    {
        if (IsSingleFieldBlittableStruct(model))
        {
            string elemType = GetSingleFieldBufferElementType(model);
            PropertyModel prop = model.Properties[0];
            builder.AppendLine($"{indent}#if NET6_0_OR_GREATER");
            builder.AppendLine(
                $"{indent}if (global::System.Runtime.CompilerServices.Unsafe.SizeOf<{model.ClassName}>() == global::System.Runtime.CompilerServices.Unsafe.SizeOf<{elemType}>())"
            );
            builder.AppendLine($"{indent}{{");
            builder.AppendLine(
                $"{indent}    global::System.Runtime.InteropServices.MemoryMarshal.Cast<{elemType}, {model.ClassName}>({bufferPrefix}0.AsSpan(0, {rowCountVar})).CopyTo({targetArrayVar}.AsSpan({startOffsetVar}, {rowCountVar}));"
            );
            builder.AppendLine($"{indent}}}");
            builder.AppendLine($"{indent}else");
            builder.AppendLine($"{indent}{{");
            builder.AppendLine(
                $"{indent}    for (int {indexVar} = 0; {indexVar} < {rowCountVar}; {indexVar}++)"
            );
            builder.AppendLine($"{indent}    {{");
            builder.AppendLine(
                $"{indent}        {targetArrayVar}[{startOffsetVar} + {indexVar}] = new {model.ClassName} {{ {prop.Name} = {bufferPrefix}0[{indexVar}] }};"
            );
            builder.AppendLine($"{indent}    }}");
            builder.AppendLine($"{indent}}}");
            builder.AppendLine($"{indent}#else");
            builder.AppendLine(
                $"{indent}for (int {indexVar} = 0; {indexVar} < {rowCountVar}; {indexVar}++)"
            );
            builder.AppendLine($"{indent}{{");
            builder.AppendLine(
                $"{indent}    {targetArrayVar}[{startOffsetVar} + {indexVar}] = new {model.ClassName} {{ {prop.Name} = {bufferPrefix}0[{indexVar}] }};"
            );
            builder.AppendLine($"{indent}}}");
            builder.AppendLine($"{indent}#endif");
        }
        else
        {
            builder.AppendLine(
                $"{indent}for (int {indexVar} = 0; {indexVar} < {rowCountVar}; {indexVar}++)"
            );
            builder.AppendLine($"{indent}{{");
            EmitObjectMaterialization(
                builder,
                model,
                targetArrayVar,
                startOffsetVar,
                indexVar,
                bufferPrefix: bufferPrefix,
                indent: indent + "    "
            );
            builder.AppendLine($"{indent}}}");
        }
    }

    /// <summary>
    /// The expression that converts a model member into its column/buffer representation (handling enum conversions).
    /// </summary>
    public static string GetWriteExpression(
        PropertyModel prop,
        string valueExpression,
        bool useMemoryForTextAndBinary = true
    )
    {
        if (useMemoryForTextAndBinary)
        {
            if (prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string"))
            {
                return prop.IsNullable
                    ? $"{valueExpression} is null ? (global::System.ReadOnlyMemory<char>?)null : global::System.MemoryExtensions.AsMemory({valueExpression})"
                    : $"{valueExpression} is null ? default(global::System.ReadOnlyMemory<char>) : global::System.MemoryExtensions.AsMemory({valueExpression})";
            }

            if (prop.Kind == PropertyKind.ByteArray)
            {
                return prop.IsNullable
                    ? $"{valueExpression} is null ? (global::System.ReadOnlyMemory<byte>?)null : global::System.MemoryExtensions.AsMemory({valueExpression})"
                    : $"{valueExpression} is null ? default(global::System.ReadOnlyMemory<byte>) : global::System.MemoryExtensions.AsMemory({valueExpression})";
            }
        }

        if (prop.Kind == PropertyKind.Enum)
        {
            string underlying = prop.EnumUnderlyingTypeName ?? "int";
            return prop.IsNullable
                ? $"{valueExpression} is null ? ({underlying}?)null : ({underlying}){valueExpression}.Value"
                : $"({underlying}){valueExpression}";
        }

        if (prop.Kind == PropertyKind.TimeSpan)
        {
            return prop.IsNullable
                ? $"{valueExpression} is null ? (int?)null : checked((int){valueExpression}.Value.TotalMilliseconds)"
                : $"checked((int){valueExpression}.TotalMilliseconds)";
        }

        if (prop.Kind == PropertyKind.TimeOnly)
        {
            return prop.IsNullable
                ? $"{valueExpression} is null ? (long?)null : {valueExpression}.Value.Ticks / 10L"
                : $"{valueExpression}.Ticks / 10L";
        }

        if (prop.Kind == PropertyKind.DateOnly)
        {
            return prop.IsNullable
                ? $"{valueExpression} is null ? (global::System.DateTime?)null : {valueExpression}.Value.ToDateTime(global::System.TimeOnly.MinValue)"
                : $"{valueExpression}.ToDateTime(global::System.TimeOnly.MinValue)";
        }

        return valueExpression;
    }

    /// <summary>
    /// The expression that converts a column/buffer value back into the model member's type (handling enum conversions).
    /// </summary>
    public static string GetReadExpression(PropertyModel prop, string valueExpression)
    {
        if (prop.Kind == PropertyKind.Enum)
        {
            return prop.IsNullable
                ? $"{valueExpression} is null ? ({prop.TypeName})null : ({prop.TypeName.TrimEnd('?')}){valueExpression}!"
                : $"({prop.TypeName.TrimEnd('?')}){valueExpression}";
        }

        if (prop.Kind == PropertyKind.TimeSpan)
        {
            return prop.IsNullable
                ? $"{valueExpression} is null ? (global::System.TimeSpan?)null : global::System.TimeSpan.FromMilliseconds({valueExpression}.Value)"
                : $"global::System.TimeSpan.FromMilliseconds({valueExpression})";
        }

        if (prop.Kind == PropertyKind.TimeOnly)
        {
            return prop.IsNullable
                ? $"{valueExpression} is null ? (global::System.TimeOnly?)null : new global::System.TimeOnly({valueExpression}.Value * 10L)"
                : $"new global::System.TimeOnly({valueExpression} * 10L)";
        }

        if (prop.Kind == PropertyKind.DateOnly)
        {
            return prop.IsNullable
                ? $"{valueExpression} is null ? (global::System.DateOnly?)null : global::System.DateOnly.FromDateTime({valueExpression}.Value)"
                : $"global::System.DateOnly.FromDateTime({valueExpression})";
        }

        if (prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string"))
        {
            if (prop.Deduplicate)
            {
                return $"stringDeduplicator.Deduplicate({valueExpression})";
            }
            return $"(deduplicateStrings ? stringDeduplicator.Deduplicate({valueExpression}) : {valueExpression})";
        }

        return valueExpression;
    }

    /// <summary>
    /// Emits object materialization instantiation:
    /// target[targetOffset + index] = new T { Prop1 = ..., Prop2 = ... };
    /// </summary>
    public static void EmitObjectMaterialization(
        StringBuilder builder,
        TargetClassModel model,
        string targetArray,
        string offsetExpr,
        string indexVar,
        string bufferPrefix = "buffer_",
        string indent = "                "
    )
    {
        string targetSlot = string.IsNullOrEmpty(offsetExpr)
            ? $"{targetArray}[{indexVar}]"
            : $"{targetArray}[{offsetExpr} + {indexVar}]";

        builder.AppendLine($"{indent}{targetSlot} = new {model.ClassName}");
        builder.AppendLine($"{indent}{{");

        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            string readExpr = GetReadExpression(prop, $"{bufferPrefix}{i}[{indexVar}]");
            builder.AppendLine($"{indent}    {prop.Name} = {readExpr},");
        }

        builder.AppendLine($"{indent}}};");
    }

    /// <summary>
    /// Emits property extraction from an item into column buffers:
    /// buffer_{i}[index] = item.Prop;
    /// </summary>
    public static void EmitPropertyAssignments(
        StringBuilder builder,
        TargetClassModel model,
        string itemVar = "item",
        string indexVar = "i",
        string bufferPrefix = "buffer_",
        string indent = "                    ",
        bool useMemoryForTextAndBinary = true
    )
    {
        for (int p = 0; p < model.Properties.Length; p++)
        {
            PropertyModel prop = model.Properties[p];
            string writeExpr = GetWriteExpression(
                prop,
                $"{itemVar}.{prop.Name}",
                useMemoryForTextAndBinary
            );
            builder.AppendLine($"{indent}{bufferPrefix}{p}[{indexVar}] = {writeExpr};");
        }
    }
}
