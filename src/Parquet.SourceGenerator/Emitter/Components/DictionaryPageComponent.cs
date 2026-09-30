using System.Text;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Emits the small page-header reader needed to inspect a dictionary page without asking
/// Parquet.Net to materialize the column. Parquet.Net exposes the dictionary-page offset but not
/// the page header itself through <c>ParquetRowGroupReader</c>.
/// </summary>
internal static class DictionaryPageComponent
{
    public static void EmitHelpers(StringBuilder builder)
    {
        builder.AppendLine("    private static int? ReadDictionaryEntryCount(");
        builder.AppendLine("        global::Parquet.ParquetRowGroupReader groupReader,");
        builder.AppendLine("        global::System.IO.Stream stream,");
        builder.AppendLine("        global::Parquet.Schema.DataField field)");
        builder.AppendLine("    {");
        builder.AppendLine("        var metadata = groupReader.GetMetadata(field);");
        builder.AppendLine(
            "        long? offset = metadata?.MetaData?.DictionaryPageOffset ?? metadata?.FileOffset;"
        );
        builder.AppendLine(
            "        if (!offset.HasValue || offset.Value < 0 || !stream.CanSeek) return null;"
        );
        builder.AppendLine();
        builder.AppendLine("        long savedPosition = stream.Position;");
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine("            stream.Position = offset.Value;");
        builder.AppendLine("            return ReadDictionaryPageHeader(stream);");
        builder.AppendLine("        }");
        builder.AppendLine("        finally");
        builder.AppendLine("        {");
        builder.AppendLine("            stream.Position = savedPosition;");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static int? ReadDictionaryPageHeader(global::System.IO.Stream stream)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        int lastFieldId = 0;");
        builder.AppendLine(
            "        while (TryReadCompactField(stream, ref lastFieldId, out int fieldId, out int type))"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            if (fieldId == 7 && type == 12)");
        builder.AppendLine("                return ReadDictionaryPageHeaderBody(stream);");
        builder.AppendLine("            if (!TrySkipCompactValue(stream, type)) return null;");
        builder.AppendLine("        }");
        builder.AppendLine("        return null;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static int? ReadDictionaryPageHeaderBody(global::System.IO.Stream stream)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        int lastFieldId = 0;");
        builder.AppendLine("        int? count = null;");
        builder.AppendLine(
            "        while (TryReadCompactField(stream, ref lastFieldId, out int fieldId, out int type))"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            if (fieldId == 1 && type == 5)");
        builder.AppendLine("            {");
        builder.AppendLine("                int? value = ReadCompactI32(stream);");
        builder.AppendLine("                if (!value.HasValue || value.Value < 0) return null;");
        builder.AppendLine("                count = value.Value;");
        builder.AppendLine("            }");
        builder.AppendLine("            else if (!TrySkipCompactValue(stream, type)) return null;");
        builder.AppendLine("        }");
        builder.AppendLine("        return count;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static bool TryReadCompactField(global::System.IO.Stream stream, ref int lastFieldId, out int fieldId, out int type)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        int header = stream.ReadByte();");
        builder.AppendLine("        if (header <= 0)");
        builder.AppendLine("        {");
        builder.AppendLine("            fieldId = 0;");
        builder.AppendLine("            type = 0;");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine("        int delta = header >> 4;");
        builder.AppendLine("        type = header & 0x0f;");
        builder.AppendLine(
            "        int? explicitFieldId = delta == 0 ? ReadCompactI32(stream) : null;"
        );
        builder.AppendLine("        if (delta == 0 && !explicitFieldId.HasValue)");
        builder.AppendLine("        {");
        builder.AppendLine("            fieldId = 0;");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine(
            "        fieldId = delta == 0 ? explicitFieldId!.Value : lastFieldId + delta;"
        );
        builder.AppendLine("        lastFieldId = fieldId;");
        builder.AppendLine("        return true;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static bool TrySkipCompactValue(global::System.IO.Stream stream, int type)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        return type switch");
        builder.AppendLine("        {");
        builder.AppendLine("            1 or 2 => true,");
        builder.AppendLine("            3 => stream.ReadByte() >= 0,");
        builder.AppendLine("            4 or 5 or 6 => TrySkipCompactVarInt(stream),");
        builder.AppendLine("            7 => TrySkipCompactBytes(stream, 8),");
        builder.AppendLine("            _ => false,");
        builder.AppendLine("        };");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static int? ReadCompactI32(global::System.IO.Stream stream)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        uint? value = ReadCompactVarUInt(stream);");
        builder.AppendLine(
            "        return value.HasValue ? (int)(value.Value >> 1) ^ -(int)(value.Value & 1) : null;"
        );
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static uint? ReadCompactVarUInt(global::System.IO.Stream stream)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        uint value = 0;");
        builder.AppendLine("        for (int shift = 0; shift < 35; shift += 7)");
        builder.AppendLine("        {");
        builder.AppendLine("            int next = stream.ReadByte();");
        builder.AppendLine("            if (next < 0) return null;");
        builder.AppendLine("            value |= (uint)(next & 0x7f) << shift;");
        builder.AppendLine("            if ((next & 0x80) == 0) return value;");
        builder.AppendLine("        }");
        builder.AppendLine("        return null;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(
            "    private static bool TrySkipCompactVarInt(global::System.IO.Stream stream)"
        );
        builder.AppendLine("        => ReadCompactVarUInt(stream).HasValue;");
        builder.AppendLine();
        builder.AppendLine(
            "    private static bool TrySkipCompactBytes(global::System.IO.Stream stream, int count)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        for (int i = 0; i < count; i++)");
        builder.AppendLine("            if (stream.ReadByte() < 0) return false;");
        builder.AppendLine("        return true;");
        builder.AppendLine("    }");
    }
}
