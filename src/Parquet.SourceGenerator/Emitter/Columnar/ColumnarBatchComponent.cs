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

    /// <summary>The member holding the row count, avoiding a clash with a column of that name.</summary>
    private static string RowCountMemberName(TargetClassModel model)
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

    private static string CamelCase(string name)
    {
        if (name.Length == 0)
            return name;
        string camel =
            char.ToLowerInvariant(name[0]).ToString(CultureInfo.InvariantCulture)
            + name.Substring(1);
        return CSharpKeywords.Contains(camel) ? "@" + camel : camel;
    }

    /// <summary>
    /// Emits the batch struct at namespace scope (after the extensions class closes).
    /// </summary>
    public static void EmitBatchStruct(StringBuilder builder, TargetClassModel model)
    {
        string batchType = BatchTypeName(model);
        string rowCountMember = RowCountMemberName(model);

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
            "/// Members are public fields rather than <c>init</c> properties so the type needs no"
        );
        builder.AppendLine(
            "/// <c>IsExternalInit</c> polyfill on downstream targets, and object-initializer syntax keeps the"
        );
        builder.AppendLine("/// column-to-buffer binding by name instead of by position.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public struct {batchType}");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>Number of rows this batch describes.</summary>");
        builder.AppendLine($"    public int {rowCountMember};");

        foreach (PropertyModel prop in model.Properties)
        {
            builder.AppendLine();
            if (BufferPoolComponent.UsesWriteAllParts(prop))
            {
                builder.AppendLine(
                    $"    /// <summary>Packed non-null values for nullable column <c>{prop.Name}</c>; length equals the number of 1s in <c>{prop.Name}DefinitionLevels</c>.</summary>"
                );
                builder.AppendLine($"    public {ColumnMemoryType(prop)} {prop.Name};");
                builder.AppendLine();
                builder.AppendLine(
                    $"    /// <summary>Definition levels for column <c>{prop.Name}</c>: one entry per row, 1 = present, 0 = null.</summary>"
                );
                builder.AppendLine(
                    $"    public global::System.ReadOnlyMemory<int> {prop.Name}DefinitionLevels;"
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
                builder.AppendLine($"    public {ColumnMemoryType(prop)} {prop.Name};");
            }
        }

        builder.AppendLine("}");
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
        EmitPositionalWriter(builder, model, batchType, rowCountMember);
        builder.AppendLine();
        EmitBatchStreamWriter(builder, model, batchType);
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
                "    public static global::System.ReadOnlyMemory<char>? AsColumnarText(string? value)"
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
                "    public static global::System.ReadOnlyMemory<byte>? AsColumnarBinary(byte[]? value)"
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
            "    public static async global::System.Threading.Tasks.Task WriteParquetRowGroupAsync("
        );
        builder.AppendLine("        this global::Parquet.ParquetWriter writer,");
        builder.AppendLine($"        {batchType} batch,");
        builder.AppendLine(
            "        global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine(
            "        if (writer == null) throw new global::System.ArgumentNullException(nameof(writer));"
        );
        builder.AppendLine();
        builder.AppendLine($"        int count = batch.{rowCountMember};");
        builder.AppendLine(
            // The (paramName, actualValue, message) overload takes the value as object, which
            // boxes the int — and the boxing guard in ZeroBoxingSerializationTests counts it.
            $"        if (count < 0) throw new global::System.ArgumentOutOfRangeException(nameof(batch), \"{rowCountMember} cannot be negative.\");"
        );
        builder.AppendLine("        if (count == 0) return;");
        builder.AppendLine();

        // O(1) shape validation. Packed value lanes are intentionally not counted against the
        // definition levels: that would be an O(n) pass, which is the cost this API removes.
        foreach (PropertyModel prop in model.Properties)
        {
            if (BufferPoolComponent.UsesWriteAllParts(prop))
            {
                builder.AppendLine(
                    $"        if (batch.{prop.Name}DefinitionLevels.Length < count) throw new global::System.ArgumentException(\"Column '{prop.Name}' supplied \" + batch.{prop.Name}DefinitionLevels.Length + \" definition levels for \" + count + \" rows.\", nameof(batch));"
                );
            }
            else
            {
                builder.AppendLine(
                    $"        if (batch.{prop.Name}.Length < count) throw new global::System.ArgumentException(\"Column '{prop.Name}' supplied \" + batch.{prop.Name}.Length + \" values for \" + count + \" rows.\", nameof(batch));"
                );
            }
        }

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
                builder.AppendLine("                cancellationToken: cancellationToken);");
            }
            else
            {
                string generic = ColumnElementType(prop).TrimEnd('?');
                builder.AppendLine($"            await groupWriter.WriteAsync<{generic}>(");
                builder.AppendLine($"                {fieldAccess},");
                builder.AppendLine($"                batch.{prop.Name}.Slice(0, count),");
                builder.AppendLine("                cancellationToken: cancellationToken);");
            }
        }

        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static void EmitPositionalWriter(
        StringBuilder builder,
        TargetClassModel model,
        string batchType,
        string rowCountMember
    )
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Positional form of the columnar hand-off: one parameter per schema column, in schema order."
        );
        builder.AppendLine(
            $"    /// Prefer the <c>{batchType}</c> overload — it binds buffers to columns by name."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    public static global::System.Threading.Tasks.Task WriteParquetRowGroupColumnarAsync("
        );
        builder.AppendLine("        this global::Parquet.ParquetWriter writer,");
        builder.AppendLine("        int rowCount,");

        foreach (PropertyModel prop in model.Properties)
        {
            builder.AppendLine($"        {ColumnMemoryType(prop)} {CamelCase(prop.Name)},");
            if (BufferPoolComponent.UsesWriteAllParts(prop))
            {
                builder.AppendLine(
                    $"        global::System.ReadOnlyMemory<int> {CamelCase(prop.Name + "DefinitionLevels")},"
                );
            }
        }

        builder.AppendLine(
            "        global::System.Threading.CancellationToken cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine($"        var batch = new {batchType}");
        builder.AppendLine("        {");
        builder.AppendLine($"            {rowCountMember} = rowCount,");
        foreach (PropertyModel prop in model.Properties)
        {
            builder.AppendLine($"            {prop.Name} = {CamelCase(prop.Name)},");
            if (BufferPoolComponent.UsesWriteAllParts(prop))
            {
                builder.AppendLine(
                    $"            {prop.Name}DefinitionLevels = {CamelCase(prop.Name + "DefinitionLevels")},"
                );
            }
        }
        builder.AppendLine("        };");
        builder.AppendLine(
            "        return writer.WriteParquetRowGroupAsync(batch, cancellationToken);"
        );
        builder.AppendLine("    }");
    }

    private static void EmitBatchStreamWriter(
        StringBuilder builder,
        TargetClassModel model,
        string batchType
    )
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
        builder.AppendLine(
            "        if (stream == null) throw new global::System.ArgumentNullException(nameof(stream));"
        );
        builder.AppendLine("        cancellationToken.ThrowIfCancellationRequested();");
        builder.AppendLine();
        builder.AppendLine(
            "        options ??= global::Parquet.SourceGenerator.ParquetSerializerOptions.Default;"
        );
        builder.AppendLine();
        builder.AppendLine(
            "        await using var writer = await global::Parquet.ParquetWriter.CreateAsync("
        );
        builder.AppendLine("            Schema,");
        builder.AppendLine("            stream,");
        builder.AppendLine("            BuildFormatOptions(options),");
        builder.AppendLine("            cancellationToken: cancellationToken);");
        builder.AppendLine(
            "        await writer.WriteParquetRowGroupAsync(batch, cancellationToken);"
        );
        builder.AppendLine("    }");
    }
}
