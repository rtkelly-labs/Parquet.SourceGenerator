using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// The byte budget for what a read allocates from file-declared counts (#361).
/// <c>MaxAllocationValues</c> bounds a count, and a count is the same number for a
/// <c>bool</c> column and a <c>Guid</c> column and applies per column, so a wide model multiplied it.
/// <c>MaxAllocationBytes</c> bounds the bytes: the emitter knows each column's element size, the
/// emitted reader multiplies it by the row count it is about to rent for, and refuses before renting.
/// </summary>
/// <remarks>
/// The sizes here are deliberately an upper estimate of the nominal element storage (the pooled
/// arrays, not the objects later built from them), so a file is refused if it could not fit, never
/// because the estimate was low. Strings, byte arrays and <c>decimal</c>/<c>Guid</c> count 16.
/// </remarks>
internal static class AllocationBudgetComponent
{
    /// <summary>Bytes one entry of a repeated column occupies: the widest element plus two level ints.</summary>
    public const int BytesPerListEntry = 24;

    /// <summary>The nominal bytes one element of the column's buffer occupies.</summary>
    public static int ElementBytes(PropertyModel prop)
    {
        string type = prop.Kind switch
        {
            PropertyKind.Enum => prop.EnumUnderlyingTypeName ?? "int",
            PropertyKind.TimeSpan => "int",
            PropertyKind.TimeOnly => "long",
            PropertyKind.DateOnly => "System.DateTime",
            _ => prop.TypeName,
        };
        type = type.TrimEnd('?');
        int dot = type.LastIndexOf('.');
        if (dot >= 0)
        {
            type = type.Substring(dot + 1);
        }

        return type switch
        {
            "bool" or "Boolean" or "byte" or "Byte" or "sbyte" or "SByte" => 1,
            "short" or "Int16" or "ushort" or "UInt16" or "char" or "Char" => 2,
            "int" or "Int32" or "uint" or "UInt32" or "float" or "Single" => 4,
            "long" or "Int64" or "ulong" or "UInt64" or "double" or "Double" or "DateTime" => 8,
            _ => 16,
        };
    }

    /// <summary>
    /// The per-row bytes of a flat model (the classic backend, which has no emission plan): each
    /// column's element, 4 for a definition level where it is nullable, and 16 more for the lane text
    /// and binary columns carry.
    /// </summary>
    public static int FlatBytesPerRow(EquatableArray<PropertyModel> properties)
    {
        int total = 0;
        foreach (PropertyModel prop in properties)
        {
            total += ElementBytes(prop) + (prop.IsNullable ? 4 : 0);
            if (prop.Kind == PropertyKind.ByteArray || prop.TypeName.Contains("string"))
            {
                total += 16;
            }
        }

        return total;
    }

    /// <summary>Emits the helper every budget check calls. One helper, so a call site adds no branch.</summary>
    public static void EmitHelper(StringBuilder builder)
    {
        builder.AppendLine(
            "    private static void CheckAllocationBudget(long units, int bytesPerUnit, global::Parquet.SourceGenerator.ParquetSerializerOptions options)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        long projected = checked(units * bytesPerUnit);");
        builder.AppendLine("        if (projected > options.MaxAllocationBytes)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.IO.InvalidDataException($\"Reading this file would allocate about {projected} bytes ({units} x {bytesPerUnit}), exceeding MaxAllocationBytes ({options.MaxAllocationBytes}). Raise ParquetSerializerOptions.MaxAllocationBytes if the file is legitimate.\");"
        );
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }
}
