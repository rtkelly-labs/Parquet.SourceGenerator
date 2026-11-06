using System;
using System.Collections.Generic;
using System.Linq;
using Parquet.Schema;

namespace Parquet.SourceGenerator.Tests.PropertyBased;

/// <summary>
/// The flat model that the property-based suite fuzzes. It deliberately spans the whole documented
/// supported flat envelope — every primitive width, both signedness families, strings, binary,
/// decimals, both timestamp units, dates, GUIDs and enums — in required and optional form, so a
/// single random case can exercise null patterns, boundary numerics, Unicode and large payloads at
/// once.
/// </summary>
/// <remarks>
/// Properties are <c>set</c> rather than <c>init</c> because the generator drives them through the
/// <see cref="FuzzColumn.Assign"/> delegates rather than an object initialiser.
/// </remarks>
[ParquetSerializable]
public partial class FuzzWideRecord
{
    [ParquetColumn("row_index")]
    public int RowIndex { get; set; }

    [ParquetColumn("flag")]
    public bool Flag { get; set; }

    [ParquetColumn("opt_flag")]
    public bool? OptFlag { get; set; }

    [ParquetColumn("i8")]
    public sbyte I8 { get; set; }

    [ParquetColumn("u8")]
    public byte U8 { get; set; }

    [ParquetColumn("i16")]
    public short I16 { get; set; }

    [ParquetColumn("u16")]
    public ushort U16 { get; set; }

    [ParquetColumn("i32")]
    public int I32 { get; set; }

    [ParquetColumn("opt_i32")]
    public int? OptI32 { get; set; }

    [ParquetColumn("u32")]
    public uint U32 { get; set; }

    [ParquetColumn("i64")]
    public long I64 { get; set; }

    [ParquetColumn("opt_i64")]
    public long? OptI64 { get; set; }

    [ParquetColumn("f32")]
    public float F32 { get; set; }

    [ParquetColumn("f64")]
    public double F64 { get; set; }

    [ParquetColumn("opt_f64")]
    public double? OptF64 { get; set; }

    [ParquetColumn("text")]
    public string Text { get; set; } = string.Empty;

    [ParquetColumn("opt_text")]
    public string? OptText { get; set; }

    [ParquetColumn("blob")]
    public byte[]? Blob { get; set; }

    [ParquetColumn("money")]
    [ParquetDecimal(18, 6)]
    public decimal Money { get; set; }

    [ParquetColumn("opt_money")]
    [ParquetDecimal(9, 2)]
    public decimal? OptMoney { get; set; }

    [ParquetColumn("ts_millis")]
    public DateTime TsMillis { get; set; }

    [ParquetColumn("ts_micros")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime TsMicros { get; set; }

    [ParquetColumn("opt_ts")]
    public DateTime? OptTs { get; set; }

    [ParquetColumn("day")]
    public DateOnly Day { get; set; }

    [ParquetColumn("duration")]
    public TimeSpan Duration { get; set; }

    [ParquetColumn("guid")]
    public Guid RowGuid { get; set; }

    [ParquetColumn("opt_guid")]
    public Guid? OptGuid { get; set; }

    [ParquetColumn("status")]
    public EventStatus Status { get; set; }

    [ParquetColumn("opt_status")]
    public EventStatus? OptStatus { get; set; }
}

/// <summary>
/// How a column's values are shaped for one generated case. Profiles are what turn a uniformly
/// random generator into one that actually visits the interesting corners of the format.
/// </summary>
public enum FuzzValueProfile
{
    /// <summary>Ordinary in-range values with occasional nulls.</summary>
    Typical,

    /// <summary>Type extremes: min, max, zero, NaN, infinities, empty strings and buffers.</summary>
    Boundary,

    /// <summary>Unicode-heavy strings and large binary/string payloads.</summary>
    Extreme,

    /// <summary>Roughly half the rows null (optional columns only).</summary>
    Sparse,

    /// <summary>Every row null (optional columns only).</summary>
    AllNull,

    /// <summary>One repeated value, which pushes the writer towards dictionary encoding.</summary>
    Constant,
}

/// <summary>
/// One fuzzable column: how to generate a value for it, how to put it on and read it off the model,
/// how to normalise it for comparison, and how to hand it to the independent engine as a typed
/// column array.
/// </summary>
public sealed class FuzzColumn
{
    public FuzzColumn(
        string name,
        bool optional,
        Type canonicalType,
        Func<FuzzRandom, FuzzValueProfile, int, object?> generate,
        Action<FuzzWideRecord, object?> assign,
        Func<FuzzWideRecord, object?> read,
        Func<object?, object?> normalize
    )
    {
        Name = name;
        Optional = optional;
        CanonicalType = canonicalType;
        Generate = generate;
        Assign = assign;
        Read = read;
        Normalize = normalize;
    }

    /// <summary>Gets the Parquet column name.</summary>
    public string Name { get; }

    /// <summary>Gets a value indicating whether this column may hold nulls.</summary>
    public bool Optional { get; }

    /// <summary>
    /// Gets the CLR type that comparisons run in. It is the type <see cref="Normalize"/> produces,
    /// which is not always the property type: an enum column compares as its underlying integer and
    /// a <see cref="DateOnly"/> column compares as a midnight <see cref="DateTime"/>, because that
    /// is what the Parquet column actually carries.
    /// </summary>
    public Type CanonicalType { get; }

    /// <summary>Gets the value factory: (rng, profile, rowIndex) to a CLR property value.</summary>
    public Func<FuzzRandom, FuzzValueProfile, int, object?> Generate { get; }

    /// <summary>Gets the setter that puts a generated value on a model instance.</summary>
    public Action<FuzzWideRecord, object?> Assign { get; }

    /// <summary>Gets the getter that reads the value back off a model instance.</summary>
    public Func<FuzzWideRecord, object?> Read { get; }

    /// <summary>
    /// Gets the projection into the value the Parquet format can actually carry — enum to
    /// underlying integer, timestamp truncated to its column unit, and so on. Comparisons run on
    /// the normalised form so that a legitimate precision loss is never reported as a bug.
    /// </summary>
    public Func<object?, object?> Normalize { get; }

    /// <summary>Gets the normalised value of this column on one record.</summary>
    public object? NormalizedValue(FuzzWideRecord record) => Normalize(Read(record));

    /// <summary>Gets the generated schema's field for this column.</summary>
    public DataField Field => FuzzSchema.FieldsByName[Name];
}

/// <summary>
/// The generated schema, indexed by column name. Taken from the generated
/// <c>FuzzWideRecordParquetExtensions.Schema</c> rather than restated, so the fuzz harness can never
/// drift from what the generator actually emits.
/// </summary>
public static class FuzzSchema
{
    public static IReadOnlyDictionary<string, DataField> FieldsByName { get; } =
        FuzzWideRecordParquetExtensions.Schema.DataFields.ToDictionary(
            f => f.Name,
            StringComparer.Ordinal
        );
}
