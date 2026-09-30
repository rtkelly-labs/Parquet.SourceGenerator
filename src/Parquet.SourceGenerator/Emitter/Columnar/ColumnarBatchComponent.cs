using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Columnar;

/// <summary>
/// Emits the direct columnar hand-off surface (issue #137): a per-model batch struct describing
/// one row group as a set of already-contiguous column buffers, plus the writer overloads that
/// pass those buffers straight to <c>ParquetRowGroupWriter.WriteAsync</c> /
/// <c>WriteAllPartsAsync</c>.
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
        $"{model.ClassName.Replace(".", string.Empty)}ColumnarBatch";

    /// <summary>Returns the generated batch's row-count member, avoiding property-name clashes.</summary>
    public static string RowCountMemberName(TargetClassModel model)
    {
        string candidate = "RowCount";
        bool clash = true;
        while (clash)
        {
            clash = false;
            foreach (PropertyModel prop in model.Properties)
            {
                if (
                    string.Equals(prop.Name, candidate, StringComparison.Ordinal)
                    || string.Equals(
                        prop.Name + "DefinitionLevels",
                        candidate,
                        StringComparison.Ordinal
                    )
                )
                {
                    clash = true;
                    break;
                }
            }

            if (clash)
            {
                candidate = "Batch" + candidate;
            }
        }

        return candidate;
    }

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
    /// Parameter names for the batch constructor and the positional writer, which share one
    /// column-to-parameter binding. The fixed parameters (<c>writer</c>, <c>rowCount</c>,
    /// <c>cancellationToken</c>) are reserved, so a column whose camel-cased name lands on one is
    /// renamed instead of producing a duplicate parameter (CS0100, the parameter half of #384).
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
        string rowCountMember = RowCountMemberName(model);
        ParameterNames names = ParameterNamesFor(model);

        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// One row group of <c>{model.ClassName}</c> data held as caller-owned column buffers."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine(
            "/// Buffers are passed to Parquet.Net verbatim — nothing here is rented, copied or pooled."
        );
        builder.AppendLine(
            "/// The struct is immutable: the constructor checks every column against the row count once,"
        );
        builder.AppendLine(
            "/// and nothing can change the row count or swap a column afterwards. The buffers' contents"
        );
        builder.AppendLine(
            "/// stay caller-owned and must not change while a write is in flight. A <c>default</c> batch"
        );
        builder.AppendLine("/// describes zero rows and writes nothing.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public readonly struct {batchType}");
        builder.AppendLine("{");

        EmitConstructor(builder, model, batchType, rowCountMember, names);

        builder.AppendLine();
        builder.AppendLine("    /// <summary>Number of rows this batch describes.</summary>");
        builder.AppendLine($"    public int {rowCountMember} {{ get; }}");

        foreach (PropertyModel prop in model.Properties)
        {
            builder.AppendLine();
            if (BufferPoolComponent.UsesWriteAllParts(prop))
            {
                builder.AppendLine(
                    $"    /// <summary>Packed non-null values for nullable column <c>{prop.Name}</c>; length equals the number of 1s in <c>{prop.Name}DefinitionLevels</c>.</summary>"
                );
                builder.AppendLine($"    public {ColumnMemoryType(prop)} {prop.Name} {{ get; }}");
                builder.AppendLine();
                builder.AppendLine(
                    $"    /// <summary>Definition levels for column <c>{prop.Name}</c>: one entry per row, 1 = present, 0 = null.</summary>"
                );
                builder.AppendLine(
                    $"    public global::System.ReadOnlyMemory<int> {prop.Name}DefinitionLevels {{ get; }}"
                );
            }
            else
            {
                builder.AppendLine(
                    $"    /// <summary>Values for column <c>{prop.Name}</c>; at least <c>{rowCountMember}</c> entries.</summary>"
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
                builder.AppendLine($"    public {ColumnMemoryType(prop)} {prop.Name} {{ get; }}");
            }
        }

        builder.AppendLine("}");
    }

    /// <summary>
    /// Emits the validating constructor: the only way to build a non-default batch. Every column is
    /// checked against the row count here, in O(1), so no instance can disagree with itself.
    /// </summary>
    private static void EmitConstructor(
        StringBuilder builder,
        TargetClassModel model,
        string batchType,
        string rowCountMember,
        ParameterNames names
    )
    {
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
        var parameters = new List<string>();
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            parameters.Add($"        {ColumnMemoryType(prop)} {names.Value(i)}");
            if (names.Level(i) is string levels)
            {
                parameters.Add($"        global::System.ReadOnlyMemory<int> {levels}");
            }
        }
        builder.AppendLine(string.Join(",\n", parameters) + ")");
        builder.AppendLine("    {");
        builder.AppendLine(
            // The (paramName, actualValue, message) overload takes the value as object, which
            // boxes the int — and the boxing guard in ZeroBoxingSerializationTests counts it.
            $"        if ({names.RowCount} < 0) throw new global::System.ArgumentOutOfRangeException(nameof({names.RowCount}), \"{rowCountMember} cannot be negative.\");"
        );

        // O(1) shape validation. Packed value lanes are intentionally not counted against the
        // definition levels: that would be an O(n) pass, which is the cost this API removes.
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            if (names.Level(i) is string levels)
            {
                builder.AppendLine(
                    $"        if ({levels}.Length < {names.RowCount}) throw new global::System.ArgumentException(\"Column '{prop.Name}' supplied \" + {levels}.Length + \" definition levels for \" + {names.RowCount} + \" rows.\", nameof({levels}));"
                );
            }
            else
            {
                string value = names.Value(i);
                builder.AppendLine(
                    $"        if ({value}.Length < {names.RowCount}) throw new global::System.ArgumentException(\"Column '{prop.Name}' supplied \" + {value}.Length + \" values for \" + {names.RowCount} + \" rows.\", nameof({value}));"
                );
            }
        }

        builder.AppendLine();
        builder.AppendLine($"        {rowCountMember} = {names.RowCount};");
        for (int i = 0; i < model.Properties.Length; i++)
        {
            PropertyModel prop = model.Properties[i];
            builder.AppendLine($"        {prop.Name} = {names.Value(i)};");
            if (names.Level(i) is string levels)
            {
                builder.AppendLine($"        {prop.Name}DefinitionLevels = {levels};");
            }
        }

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
        string rowCountMember = RowCountMemberName(model);

        EmitNullPreservingConverters(builder, model);
        EmitBatchRowGroupWriter(builder, model, batchType, rowCountMember);
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
        string batchType,
        string rowCountMember
    )
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            $"    /// Writes one row group directly from caller-owned column buffers, with no row traversal and no pooled rentals."
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
        builder.AppendLine($"        int count = batch.{rowCountMember};");
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
                builder.AppendLine(
                    $"                batch.{prop.Name}DefinitionLevels.Slice(0, count),"
                );
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
