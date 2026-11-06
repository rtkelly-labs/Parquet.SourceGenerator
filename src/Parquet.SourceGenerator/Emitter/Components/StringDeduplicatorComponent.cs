using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Composable emitter component for emitting the span-keyed L1 StringDeduplicator struct,
/// the raw UTF-16 span read helpers that feed it, and the deduplicator state setup within
/// reading methods.
/// </summary>
internal static class StringDeduplicatorComponent
{
    public static bool HasStringProperties(TargetClassModel model) =>
        global::System.Linq.Enumerable.Any(
            model.Properties,
            p => p.Kind == PropertyKind.Primitive && p.TypeName.Contains("string")
        );

    public static void EmitStringDeduplicator(
        StringBuilder builder,
        bool emitRawSpanColumnReader = true
    )
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Lightweight L1 string cache keyed directly on <see cref=\"global::System.ReadOnlySpan{T}\"/>"
        );
        builder.AppendLine(
            "    /// of <see cref=\"char\"/>, backed by a pooled open-addressed table."
        );
        builder.AppendLine(
            "    /// A cache hit returns the previously materialized <see cref=\"string\"/> instance without"
        );
        builder.AppendLine(
            "    /// allocating, so repeated (categorical) column values cost one string per distinct value"
        );
        builder.AppendLine("    /// rather than one string per row.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            "    /// Hash equality is never trusted on its own: every candidate is confirmed with a full"
        );
        builder.AppendLine(
            "    /// ordinal span comparison before it is returned, so collisions can only cost a probe,"
        );
        builder.AppendLine("    /// never correctness.");
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine("    private struct StringDeduplicator : global::System.IDisposable");
        builder.AppendLine("    {");
        builder.AppendLine("        private const int ProbeLimit = 4;");
        builder.AppendLine();
        builder.AppendLine("        private string?[]? _entries;");
        builder.AppendLine("        private readonly int _mask;");
        builder.AppendLine();
        builder.AppendLine("        public StringDeduplicator(int capacity = 512)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            _entries = global::System.Buffers.ArrayPool<string?>.Shared.Rent(capacity);"
        );
        builder.AppendLine("            _mask = capacity - 1;");
        builder.AppendLine("            global::System.Array.Clear(_entries, 0, capacity);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(");
        builder.AppendLine(
            "            global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]"
        );
        builder.AppendLine("        public string? Deduplicate(string? value)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (value is null) return null;");
        builder.AppendLine("            if (value.Length == 0) return string.Empty;");
        builder.AppendLine();
        builder.AppendLine("            var entries = _entries;");
        builder.AppendLine("            if (entries is null) return value;");
        builder.AppendLine();
        builder.AppendLine("            int mask = _mask;");
        builder.AppendLine("            int index = (int)(HashString(value) & (uint)mask);");
        builder.AppendLine("            for (int probe = 0; probe < ProbeLimit; probe++)");
        builder.AppendLine("            {");
        builder.AppendLine("                int slot = (index + probe) & mask;");
        builder.AppendLine("                string? candidate = entries[slot];");
        builder.AppendLine("                if (candidate is null)");
        builder.AppendLine("                {");
        builder.AppendLine("                    entries[slot] = value;");
        builder.AppendLine("                    return value;");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine(
            "                // A matching hash is never trusted on its own: compare in full."
        );
        builder.AppendLine(
            "                if (string.Equals(candidate, value, global::System.StringComparison.Ordinal))"
        );
        builder.AppendLine("                {");
        builder.AppendLine("                    return candidate;");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine(
            "            // Probe window exhausted: evict the primary slot so hot values stay resident."
        );
        builder.AppendLine("            entries[index] = value;");
        builder.AppendLine("            return value;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        /// <summary>");
        builder.AppendLine(
            "        /// Returns the cached string equal to <paramref name=\"value\"/>. A cache hit allocates"
        );
        builder.AppendLine(
            "        /// nothing at all — no string instance is created for a value already seen."
        );
        builder.AppendLine("        /// </summary>");
        // Materialise via span.ToString(), never `new string(span)`: the ReadOnlySpan<char>
        // string constructor does not exist on netstandard2.0/net472, where the call binds to
        // `new string(char*)` instead and fails to compile in the consumer's build.
        builder.AppendLine(
            "        public string GetOrAdd(global::System.ReadOnlySpan<char> value)"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            if (value.Length == 0) return string.Empty;");
        builder.AppendLine();
        builder.AppendLine("            var entries = _entries;");
        builder.AppendLine("            if (entries is null) return value.ToString();");
        builder.AppendLine();
        builder.AppendLine("            int mask = _mask;");
        builder.AppendLine("            int index = (int)(HashSpan(value) & (uint)mask);");
        builder.AppendLine("            for (int probe = 0; probe < ProbeLimit; probe++)");
        builder.AppendLine("            {");
        builder.AppendLine("                int slot = (index + probe) & mask;");
        builder.AppendLine("                string? candidate = entries[slot];");
        builder.AppendLine("                if (candidate is null)");
        builder.AppendLine("                {");
        builder.AppendLine("                    string inserted = value.ToString();");
        builder.AppendLine("                    entries[slot] = inserted;");
        builder.AppendLine("                    return inserted;");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine(
            "                // A matching hash is never trusted on its own: compare byte-for-byte."
        );
        builder.AppendLine("                if (SpanEqualsOrdinal(value, candidate))");
        builder.AppendLine("                {");
        builder.AppendLine("                    return candidate;");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            string replacement = value.ToString();");
        builder.AppendLine("            entries[index] = replacement;");
        builder.AppendLine("            return replacement;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine(
            "        private static bool SpanEqualsOrdinal(global::System.ReadOnlySpan<char> value, string candidate)"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            if (value.Length != candidate.Length) return false;");
        builder.AppendLine("            for (int i = 0; i < value.Length; i++)");
        builder.AppendLine("            {");
        builder.AppendLine("                if (value[i] != candidate[i]) return false;");
        builder.AppendLine("            }");
        builder.AppendLine("            return true;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine(
            "        /// <summary>FNV-1a over UTF-16 code units; allocation free and span/string identical.</summary>"
        );
        builder.AppendLine(
            "        private static uint HashSpan(global::System.ReadOnlySpan<char> value)"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            uint hash = 2166136261u;");
        builder.AppendLine("            for (int i = 0; i < value.Length; i++)");
        builder.AppendLine("            {");
        builder.AppendLine("                hash ^= value[i];");
        builder.AppendLine("                hash *= 16777619u;");
        builder.AppendLine("            }");
        builder.AppendLine("            return hash;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static uint HashString(string value)");
        builder.AppendLine("        {");
        builder.AppendLine("            uint hash = 2166136261u;");
        builder.AppendLine("            for (int i = 0; i < value.Length; i++)");
        builder.AppendLine("            {");
        builder.AppendLine("                hash ^= value[i];");
        builder.AppendLine("                hash *= 16777619u;");
        builder.AppendLine("            }");
        builder.AppendLine("            return hash;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Dispose()");
        builder.AppendLine("        {");
        builder.AppendLine("            var entries = _entries;");
        builder.AppendLine("            if (entries != null)");
        builder.AppendLine("            {");
        builder.AppendLine("                _entries = null;");
        builder.AppendLine(
            "                global::System.Buffers.ArrayPool<string?>.Shared.Return(entries, clearArray: true);"
        );
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        if (emitRawSpanColumnReader)
        {
            builder.AppendLine();
            EmitRawStringColumnReader(builder);
        }
    }

    /// <summary>
    /// Emits the helper that reads a string column as raw UTF-16 spans and materializes it
    /// through the deduplicator, so cache hits never instantiate a string.
    /// </summary>
    private static void EmitRawStringColumnReader(StringBuilder builder)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Reads a string column through Parquet.Net's raw <c>ReadOnlyMemory&lt;char&gt;</c> surface and"
        );
        builder.AppendLine(
            "    /// materializes it via <see cref=\"StringDeduplicator\"/>, so only distinct values allocate."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    private static async global::System.Threading.Tasks.ValueTask ReadDeduplicatedStringColumnAsync("
        );
        builder.AppendLine("        global::Parquet.ParquetRowGroupReader groupReader,");
        builder.AppendLine("        global::Parquet.Schema.DataField field,");
        builder.AppendLine("        string?[] destination,");
        builder.AppendLine("        int rowCount,");
        builder.AppendLine("        StringDeduplicator deduplicator,");
        builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        var raw = global::System.Buffers.ArrayPool<global::System.ReadOnlyMemory<char>>.Shared.Rent(rowCount);"
        );
        builder.AppendLine("        int maxDefinitionLevel = field.MaxDefinitionLevel;");
        builder.AppendLine("        int[]? definitionLevels = maxDefinitionLevel > 0");
        builder.AppendLine(
            "            ? global::System.Buffers.ArrayPool<int>.Shared.Rent(rowCount)"
        );
        builder.AppendLine("            : null;");
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine("            await groupReader.ReadRawAsync(");
        builder.AppendLine("                field,");
        builder.AppendLine(
            "                new global::System.Memory<global::System.ReadOnlyMemory<char>>(raw, 0, rowCount),"
        );
        builder.AppendLine("                definitionLevels is null");
        builder.AppendLine("                    ? (global::System.Memory<int>?)null");
        builder.AppendLine(
            "                    : new global::System.Memory<int>(definitionLevels, 0, rowCount),"
        );
        builder.AppendLine("                null,");
        builder.AppendLine("                cancellationToken);");
        builder.AppendLine();
        builder.AppendLine(
            "            MaterializeDeduplicatedStrings(raw, definitionLevels, destination, rowCount, maxDefinitionLevel, deduplicator);"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        finally");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            global::System.Buffers.ArrayPool<global::System.ReadOnlyMemory<char>>.Shared.Return(raw, clearArray: true);"
        );
        builder.AppendLine("            if (definitionLevels is not null)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                global::System.Buffers.ArrayPool<int>.Shared.Return(definitionLevels, clearArray: false);"
        );
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Spreads the packed raw span lane back over the row lane, interning each value."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    private static void MaterializeDeduplicatedStrings(");
        builder.AppendLine("        global::System.ReadOnlyMemory<char>[] raw,");
        builder.AppendLine("        int[]? definitionLevels,");
        builder.AppendLine("        string?[] destination,");
        builder.AppendLine("        int rowCount,");
        builder.AppendLine("        int maxDefinitionLevel,");
        builder.AppendLine("        StringDeduplicator deduplicator)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (definitionLevels is null)");
        builder.AppendLine("        {");
        builder.AppendLine("            for (int i = 0; i < rowCount; i++)");
        builder.AppendLine("            {");
        builder.AppendLine("                destination[i] = deduplicator.GetOrAdd(raw[i].Span);");
        builder.AppendLine("            }");
        builder.AppendLine("            return;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        int packed = 0;");
        builder.AppendLine("        for (int i = 0; i < rowCount; i++)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (definitionLevels[i] == maxDefinitionLevel)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                destination[i] = deduplicator.GetOrAdd(raw[packed++].Span);"
        );
        builder.AppendLine("            }");
        builder.AppendLine("            else");
        builder.AppendLine("            {");
        builder.AppendLine("                destination[i] = null;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    public static void EmitDeduplicatorDeclaration(
        StringBuilder builder,
        TargetClassModel model,
        string indent = "        "
    )
    {
        if (HasStringProperties(model))
        {
            builder.AppendLine(
                $"{indent}using var stringDeduplicator = new StringDeduplicator(512);"
            );
            builder.AppendLine($"{indent}bool deduplicateStrings = options.DeduplicateStrings;");
            builder.AppendLine();
        }
    }
}
