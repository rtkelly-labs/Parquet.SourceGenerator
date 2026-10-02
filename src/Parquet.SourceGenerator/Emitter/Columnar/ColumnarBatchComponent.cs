using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Columnar;

/// <summary>
/// Emits the unified <c>&lt;Model&gt;Batch</c> (#137, #508): one per-model struct describing one row group
/// as a set of column lanes, which is both what the columnar read path yields (see
/// <see cref="BatchReadComponent"/>) and what the writer overloads here take, passing the lanes
/// straight to <c>ParquetRowGroupWriter.WriteAsync</c> / <c>WriteAllPartsAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// The row-oriented <c>WriteParquetRowGroupAsync(IReadOnlyCollection&lt;T&gt;)</c> path rents one
/// <see cref="System.Buffers.ArrayPool{T}"/> buffer per column and transposes the row collection
/// into them. A caller whose data is <em>already</em> columnar (Arrow, a query engine, a
/// pre-split <c>ReadOnlyMemory&lt;T&gt;[]</c>) pays for a transpose it does not need. The methods
/// emitted here rent nothing and copy nothing: every buffer the caller supplies is handed to
/// Parquet.Net verbatim (sliced, which is free).
/// </para>
/// <para>
/// <b>Element types are the Parquet.Net wire shapes, not the POCO member types.</b>
/// <c>ParquetRowGroupWriter.WriteAsync&lt;T&gt;</c> is constrained <c>where T : struct</c>, so a
/// <c>string</c> column's zero-copy shape is <c>ReadOnlyMemory&lt;ReadOnlyMemory&lt;char&gt;?&gt;</c>
/// — the <c>IReadOnlyCollection&lt;string?&gt;</c> convenience overload upstream rents and copies
/// internally, which is exactly what this API exists to avoid.
/// </para>
/// <para>
/// Only all-leaf (non-compound) models get the surface. Struct / list / map members need a
/// definition–repetition ladder that a caller cannot supply positionally, so those models keep the
/// row-oriented API only.
/// </para>
/// </remarks>
internal static class ColumnarBatchComponent
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract",
        "as",
        "base",
        "bool",
        "break",
        "byte",
        "case",
        "catch",
        "char",
        "checked",
        "class",
        "const",
        "continue",
        "decimal",
        "default",
        "delegate",
        "do",
        "double",
        "else",
        "enum",
        "event",
        "explicit",
        "extern",
        "false",
        "finally",
        "fixed",
        "float",
        "for",
        "foreach",
        "goto",
        "if",
        "implicit",
        "in",
        "int",
        "interface",
        "internal",
        "is",
        "lock",
        "long",
        "namespace",
        "new",
        "null",
        "object",
        "operator",
        "out",
        "override",
        "params",
        "private",
        "protected",
        "public",
        "readonly",
        "ref",
        "return",
        "sbyte",
        "sealed",
        "short",
        "sizeof",
        "stackalloc",
        "static",
        "string",
        "struct",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "typeof",
        "uint",
        "ulong",
        "unchecked",
        "unsafe",
        "ushort",
        "using",
        "virtual",
        "void",
        "volatile",
        "while",
    };

    /// <summary>
    /// Whether the model gets a columnar hand-off surface. Compound (struct / list / map) members
    /// are excluded — their level ladders are not expressible as flat caller-owned buffers.
    /// </summary>
    public static bool IsSupported(TargetClassModel model) =>
        model.Properties.Length > 0 && !EmissionPlan.For(model).HasCompound;

    /// <summary>The generated batch struct's simple name for a model.</summary>
    public static string BatchTypeName(TargetClassModel model) =>
        $"{model.ClassName.Replace(".", string.Empty)}Batch";

    /// <summary>The nested lease class the read path hands to every batch it yields.</summary>
    public static string LeaseTypeName(TargetClassModel model) => new MemberNames(model).Lease;

    /// <summary>
    /// The shape a column takes inside the batch, which decides how the read path decodes it and
    /// how the writer hands it to Parquet.Net.
    /// </summary>
    public enum LaneKind
    {
        /// <summary>A required fixed-width column: one value per row.</summary>
        Direct,

        /// <summary>A nullable value column: packed non-null values plus definition levels.</summary>
        Packed,

        /// <summary>A string column: inline-nullable (or plain) text memory per row.</summary>
        Text,

        /// <summary>A byte-array column: inline-nullable (or plain) binary memory per row.</summary>
        Binary,
    }

    /// <summary>Classifies a column's lane.</summary>
    public static LaneKind KindOf(PropertyModel prop)
    {
        if (BufferPoolComponent.UsesWriteAllParts(prop))
        {
            return LaneKind.Packed;
        }

        if (prop.Kind == PropertyKind.ByteArray)
        {
            return LaneKind.Binary;
        }

        return prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string")
            ? LaneKind.Text
            : LaneKind.Direct;
    }

    /// <summary>
    /// The element type of a column's lane. Nullable value columns carry packed non-null payloads,
    /// so their element type is the non-nullable one.
    /// </summary>
    public static string LaneElementType(PropertyModel prop) => ColumnElementType(prop);

    /// <summary>Returns the generated batch's row-count member, avoiding property-name clashes.</summary>
    public static string RowCountMemberName(TargetClassModel model) =>
        new MemberNames(model).RowCount;

    /// <summary>
    /// The element type of the caller-supplied buffer for a column. Nullable value columns carry
    /// packed non-null payloads, so their element type is the non-nullable one.
    /// </summary>
    private static string ColumnElementType(PropertyModel prop) =>
        BufferPoolComponent.UsesWriteAllParts(prop)
            ? BufferPoolComponent.GetNonNullableBufferType(prop)
            : BufferPoolComponent.GetWriteBufferElementType(prop);

    /// <summary>Whether the column's element type is itself a nullable memory (string / binary).</summary>
    private static bool IsNullableMemoryElement(PropertyModel prop) =>
        !BufferPoolComponent.UsesWriteAllParts(prop)
        && ColumnElementType(prop).EndsWith("?", StringComparison.Ordinal);

    private static string ColumnMemoryType(PropertyModel prop) =>
        $"global::System.ReadOnlyMemory<{ColumnElementType(prop)}>";

    private static string Unescape(string name) =>
        name.Length > 0 && name[0] == '@' ? name.Substring(1) : name;

    /// <summary>
    /// Every member name the batch struct declares, resolved in one pass (#384). The model's own
    /// property names are claimed first and never renamed, because they are the public lane names.
    /// Every derived name (the row count, a column's definition levels, a fill method, the
    /// private plumbing) is then claimed against the same set and moved aside on a clash, so a
    /// model with a nullable <c>Value</c> next to a <c>ValueDefinitionLevels</c> property no longer
    /// emits the same member twice.
    /// </summary>
    private sealed class MemberNames
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        private readonly string[] _laneFields;

        private readonly string?[] _levelFields;

        private readonly string?[] _levels;

        private readonly string?[] _fills;

        public string RowCount { get; }

        public string Lease { get; }

        public string LeaseField { get; }

        public string GenerationField { get; }

        public string Live { get; }

        public string ThrowExpired { get; }

        public string CheckLane { get; }

        public string Expand { get; }

        public string LaneField(int index) => _laneFields[index];

        public string? LevelField(int index) => _levelFields[index];

        public string? Levels(int index) => _levels[index];

        public string? Fill(int index) => _fills[index];

        public MemberNames(TargetClassModel model)
        {
            _used.Add(BatchTypeName(model));
            foreach (PropertyModel prop in model.Properties)
            {
                _used.Add(Unescape(prop.Name));
            }

            // The row count keeps its established fallback spelling when a column is called RowCount.
            string rowCount = "RowCount";
            while (_used.Contains(rowCount))
            {
                rowCount = "Batch" + rowCount;
            }

            _used.Add(rowCount);
            RowCount = rowCount;

            _laneFields = new string[model.Properties.Length];
            _levelFields = new string?[model.Properties.Length];
            _levels = new string?[model.Properties.Length];
            _fills = new string?[model.Properties.Length];
            for (int i = 0; i < model.Properties.Length; i++)
            {
                PropertyModel prop = model.Properties[i];
                string name = Unescape(prop.Name);
                if (BufferPoolComponent.UsesWriteAllParts(prop))
                {
                    _levels[i] = Claim(name + "DefinitionLevels");
                    _fills[i] = Claim("Fill" + name + "Nullable");
                    _levelFields[i] = Claim("_levels" + i.ToString(CultureInfo.InvariantCulture));
                }

                _laneFields[i] = Claim("_lane" + i.ToString(CultureInfo.InvariantCulture));
            }

            Lease = Claim("Lease");
            LeaseField = Claim("_lease");
            GenerationField = Claim("_generation");
            Live = Claim("Live");
            ThrowExpired = Claim("ThrowExpired");
            CheckLane = Claim("CheckLane");
            Expand = Claim("ExpandNullable");
        }

        private string Claim(string desired)
        {
            string candidate = desired;
            while (!_used.Add(candidate))
            {
                candidate += "_";
            }

            return candidate;
        }
    }

    /// <summary>
    /// Parameter names for the batch constructors and the positional writer, which share one
    /// column-to-parameter binding. The fixed parameters (<c>writer</c>, <c>rowCount</c>,
    /// <c>cancellationToken</c>, and the internal constructor's <c>lease</c>) are reserved, so a
    /// column whose camel-cased name lands on one is renamed instead of producing a duplicate
    /// parameter (CS0100, the parameter half of #384).
    /// </summary>
    private sealed class ParameterNames
    {
        public string RowCount { get; } = "rowCount";

        private readonly string[] _values;

        private readonly string?[] _levels;

        public string Value(int index) => _values[index];

        public string? Level(int index) => _levels[index];

        public ParameterNames(TargetClassModel model)
        {
            var used = new HashSet<string>(StringComparer.Ordinal)
            {
                "writer",
                RowCount,
                "cancellationToken",
                "lease",
            };
            _values = new string[model.Properties.Length];
            _levels = new string?[model.Properties.Length];
            for (int i = 0; i < model.Properties.Length; i++)
            {
                PropertyModel prop = model.Properties[i];
                _values[i] = Unique(used, prop.Name);
                if (BufferPoolComponent.UsesWriteAllParts(prop))
                {
                    _levels[i] = Unique(used, prop.Name + "DefinitionLevels");
                }
            }
        }

        private static string CamelCase(string name)
        {
            if (name.Length == 0)
                return name;
            string camel =
                char.ToLowerInvariant(name[0]).ToString(CultureInfo.InvariantCulture)
                + name.Substring(1);
            return CSharpKeywords.Contains(camel) ? "@" + camel : camel;
        }

        private static string Unique(HashSet<string> used, string propertyName)
        {
            string candidate = Unescape(CamelCase(propertyName));
            while (!used.Add(candidate))
            {
                candidate += "_";
            }

            return CSharpKeywords.Contains(candidate) ? "@" + candidate : candidate;
        }
    }

    private static ParameterNames ParameterNamesFor(TargetClassModel model) => new(model);

    /// <summary>
    /// Emits the batch struct at namespace scope (after the extensions class closes).
    /// </summary>
    public static void EmitBatchStruct(StringBuilder builder, TargetClassModel model)
    {
        string batchType = BatchTypeName(model);
        var members = new MemberNames(model);
        ParameterNames names = ParameterNamesFor(model);

        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// One row group of <c>{model.ClassName}</c> data as a set of column lanes: what <c>AsBatches()</c> yields"
        );
        builder.AppendLine(
            "/// and what the columnar <c>WriteParquetAsync</c> takes, so a read batch can be written straight back."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine(
            "/// <para>The struct is immutable: the constructor checks every column against the row count once,"
        );
        builder.AppendLine(
            "/// and nothing can change the row count or swap a column afterwards. A <c>default</c> batch"
        );
        builder.AppendLine("/// describes zero rows and writes nothing.</para>");
        builder.AppendLine(
            "/// <para><b>Layout.</b> A nullable value column is a packed lane of its non-null values plus a"
        );
        builder.AppendLine(
            "/// definition-level lane (one entry per row, 1 = present, 0 = null). String and byte-array columns"
        );
        builder.AppendLine(
            "/// hold one inline-nullable memory per row. This is the shape the writer consumes without a"
        );
        builder.AppendLine(
            "/// transpose, and the cheapest one to read, store and hand back. Each nullable value column also has"
        );
        builder.AppendLine(
            "/// an explicit <c>Fill…Nullable</c> method that expands it into a <c>T?</c> buffer you supply."
        );
        builder.AppendLine("/// </para>");
        builder.AppendLine(
            "/// <para><b>Lifetime.</b> A batch you construct holds only your buffers and stays valid as long as"
        );
        builder.AppendLine(
            "/// they do. A batch yielded by <c>AsBatches()</c> is <b>borrowed</b>: its lanes alias pooled buffers"
        );
        builder.AppendLine(
            "/// that are returned when the enumerator advances (<c>MoveNextAsync</c>) or is disposed. From that"
        );
        builder.AppendLine(
            "/// moment every lane property of that batch (and the fill methods, and writing it) throws"
        );
        builder.AppendLine(
            "/// <see cref=\"global::System.ObjectDisposedException\"/> instead of reading recycled memory. The check"
        );
        builder.AppendLine(
            "/// is made when you read a lane property; a <c>ReadOnlyMemory&lt;T&gt;</c> you already copied out of the"
        );
        builder.AppendLine(
            "/// batch is a plain view over the pooled array and is not protected, so use it inside the loop body only."
        );
        builder.AppendLine(
            "/// Copy anything that has to outlive the iteration. The lanes of a borrowed batch are not guaranteed to be"
        );
        builder.AppendLine(
            "/// array-backed, so do not rely on <c>MemoryMarshal.TryGetArray</c> or on a pin outliving the batch."
        );
        builder.AppendLine(
            "/// A batch is not thread-safe: the check assumes one consumer advancing the enumerator."
        );
        builder.AppendLine(
            "/// To keep data, copy each lane with <c>ToArray()</c> into the public constructor.</para>"
        );
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public readonly struct {batchType}");
        builder.AppendLine("{");

        EmitLease(builder, members);
        builder.AppendLine();
        builder.AppendLine($"    private readonly {members.Lease}? {members.LeaseField};");
        builder.AppendLine();
        builder.AppendLine($"    private readonly int {members.GenerationField};");
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            builder.AppendLine();
            builder.AppendLine(
                $"    private readonly {ColumnMemoryType(prop)} {members.LaneField(i)};"
            );
            if (members.LevelField(i) is string levelField)
            {
                builder.AppendLine();
                builder.AppendLine(
                    $"    private readonly global::System.ReadOnlyMemory<int> {levelField};"
                );
            }
        }

        EmitConstructors(builder, model, batchType, members, names);

        builder.AppendLine();
        builder.AppendLine("    /// <summary>Number of rows this batch describes.</summary>");
        builder.AppendLine($"    public int {members.RowCount} {{ get; }}");

        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            builder.AppendLine();
            if (members.Levels(i) is string levelsName)
            {
                builder.AppendLine(
                    $"    /// <summary>Packed non-null values for nullable column <c>{prop.Name}</c>; length equals the number of 1s in <c>{levelsName}</c>.</summary>"
                );
                builder.AppendLine(
                    $"    public {ColumnMemoryType(prop)} {prop.Name} => {members.Live}({members.LaneField(i)});"
                );
                builder.AppendLine();
                builder.AppendLine(
                    $"    /// <summary>Definition levels for column <c>{prop.Name}</c>: one entry per row, 1 = present, 0 = null.</summary>"
                );
                builder.AppendLine(
                    $"    public global::System.ReadOnlyMemory<int> {levelsName} => {members.Live}({members.LevelField(i)});"
                );
                builder.AppendLine();
                EmitFill(builder, prop, i, members);
            }
            else
            {
                builder.AppendLine(
                    $"    /// <summary>Values for column <c>{prop.Name}</c>; at least <c>{members.RowCount}</c> entries.</summary>"
                );
                if (IsNullableMemoryElement(prop))
                {
                    // ReadOnlyMemory<T> has an implicit conversion from T[], so `cond ? null : x`
                    // has natural type ReadOnlyMemory<T> — the null branch silently becomes a
                    // present-but-empty value. The cast is the difference between a NULL and a "".
                    string helper =
                        prop.Kind == PropertyKind.ByteArray ? "AsColumnarBinary" : "AsColumnarText";
                    builder.AppendLine(
                        $"    /// <remarks>Build entries with <c>{helper}</c>, or cast an explicit null to"
                    );
                    builder.AppendLine(
                        $"    /// <c>{ColumnElementType(prop)}</c>: a bare conditional binds to the non-nullable memory type and"
                    );
                    builder.AppendLine(
                        "    /// stores an empty value where a null was meant.</remarks>"
                    );
                }
                builder.AppendLine(
                    $"    public {ColumnMemoryType(prop)} {prop.Name} => {members.Live}({members.LaneField(i)});"
                );
            }
        }

        EmitPlumbing(builder, model, batchType, members);
        builder.AppendLine("}");
    }

    /// <summary>
    /// The lease: a counter the producing enumerator bumps when it takes the buffers back. A batch
    /// remembers the value it was created under and compares it on every lane read.
    /// </summary>
    private static void EmitLease(StringBuilder builder, MemberNames members)
    {
        builder.AppendLine(
            "    /// <summary>Tracks whether the buffers behind a borrowed batch are still out on loan.</summary>"
        );
        builder.AppendLine($"    internal sealed class {members.Lease}");
        builder.AppendLine("    {");
        builder.AppendLine("        private int _generation;");
        builder.AppendLine();
        builder.AppendLine("        internal int Generation => _generation;");
        builder.AppendLine();
        builder.AppendLine("        internal void Expire() => _generation++;");
        builder.AppendLine("    }");
    }

    /// <summary>
    /// Emits the public constructor (the only way a caller builds a non-default batch) and the
    /// internal one the read path uses. Both validate: the public one delegates to the internal one
    /// with no lease, so there is one copy of the checks.
    /// </summary>
    private static void EmitConstructors(
        StringBuilder builder,
        TargetClassModel model,
        string batchType,
        MemberNames members,
        ParameterNames names
    )
    {
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Creates a batch after checking every column against the row count."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            $"    /// <param name=\"{names.RowCount}\">Number of rows; must not be negative.</param>"
        );
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            builder.AppendLine(
                $"    /// <param name=\"{Unescape(names.Value(i))}\">Column <c>{prop.Name}</c>: at least <paramref name=\"{names.RowCount}\"/> entries{(BufferPoolComponent.UsesWriteAllParts(prop) ? " of packed non-null values, sized by the definition levels" : string.Empty)}.</param>"
            );
            if (names.Level(i) is string levels)
            {
                builder.AppendLine(
                    $"    /// <param name=\"{Unescape(levels)}\">Definition levels for <c>{prop.Name}</c>: at least <paramref name=\"{names.RowCount}\"/> entries.</param>"
                );
            }
        }
        builder.AppendLine(
            $"    /// <exception cref=\"global::System.ArgumentOutOfRangeException\"><paramref name=\"{names.RowCount}\"/> is negative.</exception>"
        );
        builder.AppendLine(
            "    /// <exception cref=\"global::System.ArgumentException\">A column is shorter than the row count.</exception>"
        );
        builder.AppendLine($"    public {batchType}(");
        builder.AppendLine($"        int {names.RowCount},");
        builder.AppendLine(string.Join(",\n", ParameterDeclarations(model, names)) + ")");
        var forwarded = new List<string> { "null", names.RowCount };
        forwarded.AddRange(ParameterArguments(model, names));
        builder.AppendLine($"        : this({string.Join(", ", forwarded)})");
        builder.AppendLine("    {");
        builder.AppendLine("    }");

        builder.AppendLine();
        builder.AppendLine(
            "    /// <summary>Creates a batch over buffers the read path rented; <paramref name=\"lease\"/> expires when it returns them.</summary>"
        );
        builder.AppendLine($"    internal {batchType}(");
        builder.AppendLine($"        {members.Lease}? lease,");
        builder.AppendLine($"        int {names.RowCount},");
        builder.AppendLine(string.Join(",\n", ParameterDeclarations(model, names)) + ")");
        builder.AppendLine("    {");
        builder.AppendLine(
            // The (paramName, actualValue, message) overload takes the value as object, which
            // boxes the int — and the boxing guard in ZeroBoxingSerializationTests counts it.
            $"        if ({names.RowCount} < 0) throw new global::System.ArgumentOutOfRangeException(nameof({names.RowCount}), \"{members.RowCount} cannot be negative.\");"
        );

        // O(1) shape validation, one helper call per column so the constructor's complexity does not
        // grow with the column count. Packed value lanes are intentionally not counted against the
        // definition levels: that would be an O(n) pass, which is the cost this API removes.
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            if (names.Level(i) is string levels)
            {
                builder.AppendLine(
                    $"        {members.CheckLane}({levels}.Length, {names.RowCount}, \"{prop.Name}\", \"definition levels\", nameof({levels}));"
                );
            }
            else
            {
                string value = names.Value(i);
                builder.AppendLine(
                    $"        {members.CheckLane}({value}.Length, {names.RowCount}, \"{prop.Name}\", \"values\", nameof({value}));"
                );
            }
        }

        builder.AppendLine();
        builder.AppendLine($"        {members.LeaseField} = lease;");
        builder.AppendLine($"        {members.GenerationField} = lease?.Generation ?? 0;");
        builder.AppendLine($"        {members.RowCount} = {names.RowCount};");
        for (int i = 0; i < model.Properties.Length; i++)
        {
            builder.AppendLine($"        {members.LaneField(i)} = {names.Value(i)};");
            if (names.Level(i) is string levels && members.LevelField(i) is string levelField)
            {
                builder.AppendLine($"        {levelField} = {levels};");
            }
        }

        builder.AppendLine("    }");
    }

    private static IEnumerable<string> ParameterDeclarations(
        TargetClassModel model,
        ParameterNames names
    )
    {
        for (int i = 0; i < model.Properties.Length; i++)
        {
            yield return $"        {ColumnMemoryType(model.Properties[i])} {names.Value(i)}";
            if (names.Level(i) is string levels)
            {
                yield return $"        global::System.ReadOnlyMemory<int> {levels}";
            }
        }
    }

    private static IEnumerable<string> ParameterArguments(
        TargetClassModel model,
        ParameterNames names
    )
    {
        for (int i = 0; i < model.Properties.Length; i++)
        {
            yield return names.Value(i);
            if (names.Level(i) is string levels)
            {
                yield return levels;
            }
        }
    }

    /// <summary>
    /// The opt-in expansion of a packed column into a caller-supplied <c>T?</c> buffer. It is a
    /// method, not a cached property, on purpose: a cached <c>T?</c> accessor allocates a
    /// row-count-sized array per batch and measured 2x slower under Server GC (#561).
    /// </summary>
    private static void EmitFill(
        StringBuilder builder,
        PropertyModel prop,
        int index,
        MemberNames members
    )
    {
        string element = ColumnElementType(prop);
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Expands column <c>{prop.Name}</c> into <paramref name=\"destination\"/> as one <c>{element}?</c> per row, null where the row is null."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            "    /// A convenience with a cost: it makes an O(rows) pass and needs a buffer you own, and a nullable"
        );
        builder.AppendLine(
            "    /// value is wider than the value it wraps. Reading the packed lane and its definition levels directly"
        );
        builder.AppendLine(
            "    /// is cheaper; use this when a row-aligned <c>T?</c> view is worth that to you. Nothing is allocated or cached."
        );
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine(
            "    /// <param name=\"destination\">At least the batch's row count of entries.</param>"
        );
        builder.AppendLine(
            "    /// <exception cref=\"global::System.ArgumentException\"><paramref name=\"destination\"/> is shorter than the row count.</exception>"
        );
        builder.AppendLine(
            "    /// <exception cref=\"global::System.InvalidOperationException\">The definition levels mark more rows present than the packed lane holds.</exception>"
        );
        builder.AppendLine(
            $"    public void {members.Fill(index)}(global::System.Span<{element}?> destination) =>"
        );
        builder.AppendLine(
            $"        {members.Expand}<{element}>({members.Live}({members.LaneField(index)}).Span, {members.Live}({members.LevelField(index)}).Span, destination, {members.RowCount}, \"{prop.Name}\");"
        );
    }

    /// <summary>
    /// The private helpers every batch carries: the lane-liveness check, the per-column length
    /// check, and (when there is a packed column) the shared expansion loop.
    /// </summary>
    private static void EmitPlumbing(
        StringBuilder builder,
        TargetClassModel model,
        string batchType,
        MemberNames members
    )
    {
        builder.AppendLine();
        builder.AppendLine(
            $"    private global::System.ReadOnlyMemory<T> {members.Live}<T>(global::System.ReadOnlyMemory<T> lane)"
        );
        builder.AppendLine("    {");
        builder.AppendLine(
            $"        if ({members.LeaseField} is not null && {members.LeaseField}.Generation != {members.GenerationField})"
        );
        builder.AppendLine("        {");
        builder.AppendLine($"            {members.ThrowExpired}();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return lane;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine($"    private static void {members.ThrowExpired}() =>");
        builder.AppendLine(
            $"        throw new global::System.ObjectDisposedException(nameof({batchType}), \"This batch was borrowed from AsBatches(): its buffers were returned to the pool when the enumerator advanced or was disposed. Copy what you need inside the loop body.\");"
        );
        builder.AppendLine();
        builder.AppendLine(
            $"    private static void {members.CheckLane}(int length, int rowCount, string column, string what, string parameter)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        if (length < rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.ArgumentException(\"Column '\" + column + \"' supplied \" + length + \" \" + what + \" for \" + rowCount + \" rows.\", parameter);"
        );
        builder.AppendLine("        }");
        builder.AppendLine("    }");

        bool anyPacked = false;
        foreach (PropertyModel prop in model.Properties)
        {
            anyPacked |= BufferPoolComponent.UsesWriteAllParts(prop);
        }

        if (!anyPacked)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"    private static void {members.Expand}<T>(");
        builder.AppendLine("        global::System.ReadOnlySpan<T> packed,");
        builder.AppendLine("        global::System.ReadOnlySpan<int> levels,");
        builder.AppendLine("        global::System.Span<T?> destination,");
        builder.AppendLine("        int rowCount,");
        builder.AppendLine("        string column)");
        builder.AppendLine("        where T : struct");
        builder.AppendLine("    {");
        builder.AppendLine("        if (destination.Length < rowCount)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            throw new global::System.ArgumentException(\"Column '\" + column + \"' needs a destination of at least \" + rowCount + \" entries, got \" + destination.Length + \".\", nameof(destination));"
        );
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        int next = 0;");
        builder.AppendLine("        for (int i = 0; i < rowCount; i++)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (levels[i] == 0)");
        builder.AppendLine("            {");
        builder.AppendLine("                destination[i] = null;");
        builder.AppendLine("                continue;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (next >= packed.Length)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                throw new global::System.InvalidOperationException(\"Column '\" + column + \"' marks more rows present than its packed lane holds.\");"
        );
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            destination[i] = packed[next++];");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    /// <summary>
    /// Emits the batch-taking row group writer (Option B) plus the positional
    /// <c>WriteParquetRowGroupColumnarAsync</c> overload (Option A) and an end-to-end
    /// <c>WriteParquetAsync</c> convenience.
    /// </summary>
    public static void EmitWriters(StringBuilder builder, TargetClassModel model)
    {
        string batchType = BatchTypeName(model);
        EmitNullPreservingConverters(builder, model);
        EmitBatchRowGroupWriter(builder, model, batchType);
        builder.AppendLine();
        EmitPositionalWriter(builder, model, batchType);
        builder.AppendLine();
        EmitBatchStreamWriter(builder, batchType);
    }

    /// <summary>
    /// Emits null-preserving converters for text and binary column entries.
    /// </summary>
    /// <remarks>
    /// <c>ReadOnlyMemory&lt;T&gt;</c> converts implicitly from <c>T[]</c>, so in
    /// <c>value is null ? null : value.AsMemory()</c> the compiler gives the conditional the
    /// natural type <c>ReadOnlyMemory&lt;T&gt;</c> — the null branch becomes a present-but-empty
    /// value and the column silently records <c>""</c> where the caller meant NULL. These helpers
    /// remove the trap; without them every caller has to remember the cast.
    /// </remarks>
    private static void EmitNullPreservingConverters(StringBuilder builder, TargetClassModel model)
    {
        bool hasText = false;
        bool hasBinary = false;
        foreach (PropertyModel prop in model.Properties)
        {
            if (BufferPoolComponent.UsesWriteAllParts(prop))
                continue;
            if (prop.Kind == PropertyKind.Primitive && prop.TypeName.Contains("string"))
                hasText = true;
            if (prop.Kind == PropertyKind.ByteArray)
                hasBinary = true;
        }

        if (hasText)
        {
            builder.AppendLine("    /// <summary>");
            builder.AppendLine(
                "    /// Converts a string into a text column entry, preserving null."
            );
            builder.AppendLine(
                "    /// A bare <c>cond ? null : value.AsMemory()</c> does not: the conditional's natural type is the"
            );
            builder.AppendLine(
                "    /// non-nullable memory, so the null branch stores an empty value instead."
            );
            builder.AppendLine("    /// </summary>");
            builder.AppendLine(
                "    internal static global::System.ReadOnlyMemory<char>? AsColumnarText(string? value)"
            );
            builder.AppendLine(
                "        => value is null ? (global::System.ReadOnlyMemory<char>?)null : global::System.MemoryExtensions.AsMemory(value);"
            );
            builder.AppendLine();
        }

        if (hasBinary)
        {
            builder.AppendLine("    /// <summary>");
            builder.AppendLine(
                "    /// Converts a byte array into a binary column entry, preserving null."
            );
            builder.AppendLine("    /// </summary>");
            builder.AppendLine(
                "    internal static global::System.ReadOnlyMemory<byte>? AsColumnarBinary(byte[]? value)"
            );
            builder.AppendLine(
                "        => value is null ? (global::System.ReadOnlyMemory<byte>?)null : global::System.MemoryExtensions.AsMemory(value);"
            );
            builder.AppendLine();
        }
    }

    private static void EmitBatchRowGroupWriter(
        StringBuilder builder,
        TargetClassModel model,
        string batchType
    )
    {
        var members = new MemberNames(model);
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Writes one row group directly from caller-owned column buffers, with no row traversal and no pooled rentals."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    internal static async global::System.Threading.Tasks.Task WriteParquetRowGroupAsync("
        );
        builder.AppendLine("        this global::Parquet.ParquetWriter writer,");
        builder.AppendLine($"        {batchType} batch,");
        builder.AppendLine(
            "        global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(writer);");
        builder.AppendLine();
        // The batch is a readonly struct built by a validating constructor, so its lanes already
        // agree with RowCount. The one instance the constructor never saw is default(T): it has
        // RowCount 0 and empty lanes, which is consistent, and the early return below makes it a
        // no-op. Nothing needs re-validating here.
        builder.AppendLine($"        int count = batch.{members.RowCount};");
        builder.AppendLine("        if (count == 0) return;");
        builder.AppendLine();
        builder.AppendLine("        using (var groupWriter = writer.CreateRowGroup())");
        builder.AppendLine("        {");

        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            string fieldAccess = $"_field_{i}";
            if (BufferPoolComponent.UsesWriteAllParts(prop))
            {
                string nonNull = BufferPoolComponent.GetNonNullableBufferType(prop);
                builder.AppendLine($"            await groupWriter.WriteAllPartsAsync<{nonNull}>(");
                builder.AppendLine($"                {fieldAccess},");
                builder.AppendLine($"                batch.{prop.Name},");
                builder.AppendLine($"                batch.{members.Levels(i)}.Slice(0, count),");
                builder.AppendLine("                null,");
                builder.AppendLine(
                    "                cancellationToken: cancellationToken).ConfigureAwait(false);"
                );
            }
            else
            {
                string generic = ColumnElementType(prop).TrimEnd('?');
                builder.AppendLine($"            await groupWriter.WriteAsync<{generic}>(");
                builder.AppendLine($"                {fieldAccess},");
                builder.AppendLine($"                batch.{prop.Name}.Slice(0, count),");
                builder.AppendLine(
                    "                cancellationToken: cancellationToken).ConfigureAwait(false);"
                );
            }
        }

        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static void EmitPositionalWriter(
        StringBuilder builder,
        TargetClassModel model,
        string batchType
    )
    {
        ParameterNames names = ParameterNamesFor(model);
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Positional form of the columnar hand-off: one parameter per schema column, in schema order."
        );
        builder.AppendLine(
            $"    /// Prefer the <c>{batchType}</c> overload — it binds buffers to columns by name, and its constructor validates them."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    internal static global::System.Threading.Tasks.Task WriteParquetRowGroupColumnarAsync("
        );
        builder.AppendLine("        this global::Parquet.ParquetWriter writer,");
        builder.AppendLine($"        int {names.RowCount},");

        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            builder.AppendLine($"        {ColumnMemoryType(prop)} {names.Value(i)},");
            if (names.Level(i) is string levels)
            {
                builder.AppendLine($"        global::System.ReadOnlyMemory<int> {levels},");
            }
        }

        builder.AppendLine(
            "        global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine($"        var batch = new {batchType}(");
        var args = new List<string> { $"            {names.RowCount}" };
        for (int i = 0; i < model.Properties.Length; i++)
        {
            args.Add($"            {names.Value(i)}");
            if (names.Level(i) is string levels)
            {
                args.Add($"            {levels}");
            }
        }
        builder.AppendLine(string.Join(",\n", args) + ");");
        builder.AppendLine(
            "        return writer.WriteParquetRowGroupAsync(batch, cancellationToken);"
        );
        builder.AppendLine("    }");
    }

    private static void EmitBatchStreamWriter(StringBuilder builder, string batchType)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Writes a complete single-row-group Parquet stream from one <c>{batchType}</c>."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    public static async global::System.Threading.Tasks.Task WriteParquetAsync("
        );
        builder.AppendLine($"        this {batchType} batch,");
        builder.AppendLine("        global::System.IO.Stream stream,");
        builder.AppendLine(
            "        global::Parquet.SourceGenerator.ParquetSerializerOptions? options = null,"
        );
        builder.AppendLine(
            "        global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(stream);");
        builder.AppendLine("        cancellationToken.ThrowIfCancellationRequested();");
        builder.AppendLine();
        builder.AppendLine(
            "        options ??= global::Parquet.SourceGenerator.ParquetSerializerOptions.Default;"
        );
        builder.AppendLine();
        builder.AppendLine("        var writer = await global::Parquet.ParquetWriter.CreateAsync(");
        builder.AppendLine("            Schema,");
        builder.AppendLine("            stream,");
        builder.AppendLine("            BuildFormatOptions(options),");
        builder.AppendLine(
            "            cancellationToken: cancellationToken).ConfigureAwait(false);"
        );
        // try/finally instead of an `await using` scope: see the CA1506 note on EmitReadAsync.
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine(
            "        await writer.WriteParquetRowGroupAsync(batch, cancellationToken).ConfigureAwait(false);"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        finally");
        builder.AppendLine("        {");
        builder.AppendLine("            await writer.DisposeAsync().ConfigureAwait(false);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }
}
