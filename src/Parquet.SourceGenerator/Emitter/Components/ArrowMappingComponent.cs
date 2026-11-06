using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// How one Arrow column's values reach the Parquet column buffer.
/// </summary>
internal enum ArrowExtractionMode
{
    /// <summary>
    /// The Arrow physical value type is bit-identical to the Parquet buffer element type, so the
    /// Arrow value buffer is handed to the writer as a <c>ReadOnlyMemory&lt;T&gt;</c> view with no
    /// rental and no copy.
    /// </summary>
    Direct,

    /// <summary>Per-value conversion into a rented buffer (timestamps, decimals, GUIDs, booleans).</summary>
    Convert,

    /// <summary>Utf8 offsets materialize into one rented <c>char[]</c>, sliced per value.</summary>
    Utf8,

    /// <summary>Binary offsets slice the Arrow value buffer directly (no copy of the payload).</summary>
    Binary,
}

/// <summary>
/// One row of the Arrow ↔ Parquet physical-type table.
/// </summary>
internal sealed class ArrowLeafMapping
{
    /// <summary>Fully-qualified Apache.Arrow array class the column is cast to.</summary>
    public string ArrowArrayType { get; set; } = "";

    /// <summary>Member name on <c>Apache.Arrow.Types.ArrowTypeId</c> the field's type must carry.</summary>
    public string ArrowTypeId { get; set; } = "";

    /// <summary>Human-readable Arrow type used in validation messages ("Timestamp(Microsecond)").</summary>
    public string ExpectedDescription { get; set; } = "";

    /// <summary>Generic argument handed to <c>WriteAsync</c>/<c>WriteAllPartsAsync</c>.</summary>
    public string ParquetElementType { get; set; } = "";

    public ArrowExtractionMode Mode { get; set; }

    /// <summary>
    /// Extra parameterisation check (timestamp unit, decimal precision/scale, GUID byte width),
    /// written against a local named <c>dataType</c>. Null when the type id alone is decisive.
    /// </summary>
    public string? ParameterCheck { get; set; }

    /// <summary>Message appended when <see cref="ParameterCheck"/> fails; a C# expression.</summary>
    public string? ParameterMessage { get; set; }

    /// <summary>
    /// Conversion expression for <see cref="ArrowExtractionMode.Convert"/>. <c>{ARR}</c> is the
    /// Arrow array local, <c>{VALS}</c> the values span local, <c>{I}</c> the row index.
    /// </summary>
    public string? ConvertExpression { get; set; }

    /// <summary>Whether <see cref="ConvertExpression"/> reads through the <c>.Values</c> span.</summary>
    public bool UsesValuesSpan { get; set; }
}

/// <summary>
/// The physical-type mapping table shared by both directions of the Apache Arrow bridge
/// (RecordBatch ingestion today, RecordBatch export later). It mirrors the leaf allowlist the
/// parser accepts: anything the parser lets through that has no row here simply gets no Arrow
/// overload, rather than a half-working one.
/// </summary>
internal static class ArrowMappingComponent
{
    /// <summary>Apache.Arrow version floor the emitted bridge is written against.</summary>
    public const string SupportedArrowFloor = "23.0.0";

    private const string ArrowNs = "global::Apache.Arrow";
    private const string TypesNs = "global::Apache.Arrow.Types";

    /// <summary>
    /// Whether every column of the model has an Arrow representation, so a bridge can be emitted.
    /// Compound members (#176) and the exotic passthrough leaves have none.
    /// </summary>
    public static bool IsSupported(TargetClassModel model)
    {
        if (model.Properties.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < model.Properties.Length; i++)
        {
            if (TryMap(model.Properties[i]) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Maps one leaf property onto its Arrow column, or null when the leaf has no representation.
    /// </summary>
    public static ArrowLeafMapping? TryMap(PropertyModel prop)
    {
        switch (prop.Kind)
        {
            case PropertyKind.Struct:
            case PropertyKind.List:
            case PropertyKind.Map:
                return null;

            case PropertyKind.ByteArray:
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.BinaryArray",
                    ArrowTypeId = "Binary",
                    ExpectedDescription = "Binary",
                    ParquetElementType = "global::System.ReadOnlyMemory<byte>",
                    Mode = ArrowExtractionMode.Binary,
                };

            case PropertyKind.Decimal:
            {
                int precision = prop.DecimalPrecision ?? 38;
                int scale = prop.DecimalScale ?? 18;
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.Decimal128Array",
                    ArrowTypeId = "Decimal128",
                    ExpectedDescription = $"Decimal128({precision},{scale})",
                    ParquetElementType = "decimal",
                    Mode = ArrowExtractionMode.Convert,
                    ParameterCheck =
                        $"(({TypesNs}.Decimal128Type)dataType).Precision == {precision} && (({TypesNs}.Decimal128Type)dataType).Scale == {scale}",
                    ParameterMessage =
                        "\"decimal precision/scale mismatch: the Parquet column is Decimal128("
                        + precision
                        + ","
                        + scale
                        + ") but the Arrow column is Decimal128(\" + (("
                        + TypesNs
                        + ".Decimal128Type)dataType).Precision + \",\" + (("
                        + TypesNs
                        + ".Decimal128Type)dataType).Scale + \").\"",
                    ConvertExpression = "{ARR}.GetValue({I})!.Value",
                };
            }

            case PropertyKind.DateTime:
            {
                bool micros =
                    prop.TimestampUnit == "1"
                    || prop.TimestampUnit?.Contains("Microseconds") == true;
                string unit = micros ? "Microsecond" : "Millisecond";
                long ticksPerUnit = micros ? 10L : 10_000L;
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.TimestampArray",
                    ArrowTypeId = "Timestamp",
                    ExpectedDescription = $"Timestamp({unit})",
                    ParquetElementType = "global::System.DateTime",
                    Mode = ArrowExtractionMode.Convert,
                    UsesValuesSpan = true,
                    ParameterCheck =
                        $"(({TypesNs}.TimestampType)dataType).Unit == {TypesNs}.TimeUnit.{unit}",
                    ParameterMessage =
                        "\"timestamp unit mismatch: the Parquet column is "
                        + unit
                        + " but the Arrow column is \" + (("
                        + TypesNs
                        + ".TimestampType)dataType).Unit + \".\"",
                    ConvertExpression =
                        $"_arrowUnixEpoch.AddTicks({{VALS}}[{{I}}] * {ticksPerUnit}L)",
                };
            }

            case PropertyKind.DateOnly:
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.Date32Array",
                    ArrowTypeId = "Date32",
                    ExpectedDescription = "Date32",
                    // The Parquet column for DateOnly is a DateTimeDataField, so the buffer is
                    // DateTime — the same representation the POCO path produces via ToDateTime.
                    ParquetElementType = "global::System.DateTime",
                    Mode = ArrowExtractionMode.Convert,
                    UsesValuesSpan = true,
                    ConvertExpression = "_arrowUnixEpoch.AddDays({VALS}[{I}])",
                };

            case PropertyKind.TimeOnly:
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.Time64Array",
                    ArrowTypeId = "Time64",
                    ExpectedDescription = "Time64(Microsecond)",
                    // TimeOnly columns are written as microseconds-since-midnight Int64, which is
                    // exactly Arrow's Time64(Microsecond) payload — so this one is zero-copy.
                    ParquetElementType = "long",
                    Mode = ArrowExtractionMode.Direct,
                    ParameterCheck =
                        $"(({TypesNs}.Time64Type)dataType).Unit == {TypesNs}.TimeUnit.Microsecond",
                    ParameterMessage =
                        "\"time unit mismatch: the Parquet column is Microsecond but the Arrow column is \" + (("
                        + TypesNs
                        + ".Time64Type)dataType).Unit + \".\"",
                };

            case PropertyKind.TimeSpan:
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.DurationArray",
                    ArrowTypeId = "Duration",
                    ExpectedDescription = "Duration(Millisecond)",
                    ParquetElementType = "int",
                    Mode = ArrowExtractionMode.Convert,
                    UsesValuesSpan = true,
                    ParameterCheck =
                        $"(({TypesNs}.DurationType)dataType).Unit == {TypesNs}.TimeUnit.Millisecond",
                    ParameterMessage =
                        "\"duration unit mismatch: the Parquet column is Millisecond but the Arrow column is \" + (("
                        + TypesNs
                        + ".DurationType)dataType).Unit + \".\"",
                    ConvertExpression = "checked((int){VALS}[{I}])",
                };

            case PropertyKind.Guid:
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.Arrays.FixedSizeBinaryArray",
                    ArrowTypeId = "FixedSizedBinary",
                    ExpectedDescription = "FixedSizeBinary(16)",
                    ParquetElementType = "global::System.Guid",
                    Mode = ArrowExtractionMode.Convert,
                    ParameterCheck = $"(({TypesNs}.FixedSizeBinaryType)dataType).ByteWidth == 16",
                    ParameterMessage =
                        "\"fixed-size binary width mismatch: a Guid column needs 16 bytes but the Arrow column is \" + (("
                        + TypesNs
                        + ".FixedSizeBinaryType)dataType).ByteWidth + \" bytes.\"",
                    ConvertExpression = "ArrowGuidFromBigEndian({ARR}.GetBytes({I}))",
                };

            case PropertyKind.Enum:
                return MapIntegral(prop.EnumUnderlyingTypeName ?? "int");

            default:
                break;
        }

        string typeName = prop.TypeName.TrimEnd('?');
        switch (typeName)
        {
            case "bool":
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.BooleanArray",
                    ArrowTypeId = "Boolean",
                    ExpectedDescription = "Boolean",
                    ParquetElementType = "bool",
                    // Arrow booleans are bit-packed, Parquet.Net wants bool[]; a copy is unavoidable.
                    Mode = ArrowExtractionMode.Convert,
                    ConvertExpression = "{ARR}.GetValue({I})!.Value",
                };

            case "float":
                return Fixed($"{ArrowNs}.FloatArray", "Float", "Float", "float");

            case "double":
                return Fixed($"{ArrowNs}.DoubleArray", "Double", "Double", "double");

            case "string":
                return new ArrowLeafMapping
                {
                    ArrowArrayType = $"{ArrowNs}.StringArray",
                    ArrowTypeId = "String",
                    ExpectedDescription = "Utf8",
                    ParquetElementType = "global::System.ReadOnlyMemory<char>",
                    Mode = ArrowExtractionMode.Utf8,
                };

            default:
                return MapIntegral(typeName);
        }
    }

    private static ArrowLeafMapping? MapIntegral(string typeName)
    {
        return typeName switch
        {
            "sbyte" => Fixed($"{ArrowNs}.Int8Array", "Int8", "Int8", "sbyte"),
            "byte" => Fixed($"{ArrowNs}.UInt8Array", "UInt8", "UInt8", "byte"),
            "short" => Fixed($"{ArrowNs}.Int16Array", "Int16", "Int16", "short"),
            "ushort" => Fixed($"{ArrowNs}.UInt16Array", "UInt16", "UInt16", "ushort"),
            "int" => Fixed($"{ArrowNs}.Int32Array", "Int32", "Int32", "int"),
            "uint" => Fixed($"{ArrowNs}.UInt32Array", "UInt32", "UInt32", "uint"),
            "long" => Fixed($"{ArrowNs}.Int64Array", "Int64", "Int64", "long"),
            "ulong" => Fixed($"{ArrowNs}.UInt64Array", "UInt64", "UInt64", "ulong"),
            _ => null,
        };
    }

    private static ArrowLeafMapping Fixed(
        string arrayType,
        string typeId,
        string description,
        string element
    )
    {
        return new ArrowLeafMapping
        {
            ArrowArrayType = arrayType,
            ArrowTypeId = typeId,
            ExpectedDescription = description,
            ParquetElementType = element,
            Mode = ArrowExtractionMode.Direct,
        };
    }
}
