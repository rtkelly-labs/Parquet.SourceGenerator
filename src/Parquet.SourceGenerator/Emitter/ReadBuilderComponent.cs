using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter;

/// <summary>
/// The generated read entry point and its builder structs (issue #217).
/// <para>
/// Reads used to encode source, shape and execution into method names, with pushdown appended as a
/// parameter to some cells and not others — twelve members for a four-axis grid, heading for
/// forty-five as #146, #148 and #178 land. Naming a cross-product does not scale; expressing each
/// axis as a member does. See <c>docs/19-PUBLIC-API-SURFACE.md</c>.
/// </para>
/// <para>
/// The builders are <b>type-state</b> rather than one struct with runtime validation. A cell that
/// does not exist is not a member you can call and then fail on: <c>Parallel()</c> is absent from
/// the stream source because a single <c>ParquetReader</c> cannot be read concurrently, and
/// <c>Where()</c> and <c>Parallel()</c> return types that do not offer each other because no
/// parallel reader accepts a predicate yet. Every one of those would otherwise be a
/// <c>NotSupportedException</c> thrown for a mistake the compiler could have caught.
/// </para>
/// <para>
/// Every struct is <c>readonly</c> and every method returns a new one, so composing a read
/// allocates nothing and the builders stay Native AOT clean.
/// </para>
/// </summary>
internal static class ReadBuilderComponent
{
    private const string Ct = "global::System.Threading.CancellationToken";
    private const string Options = "global::Parquet.SourceGenerator.ParquetSerializerOptions";
    private const string Stream = "global::System.IO.Stream";
    private const string Memory = "global::System.ReadOnlyMemory<byte>";

    private static string EntryPointName(TargetClassModel model) =>
        $"{model.ClassName.Replace(".", string.Empty)}Parquet";

    private static string ExtensionsName(TargetClassModel model) =>
        $"{model.ClassName.Replace(".", string.Empty)}ParquetExtensions";

    /// <summary>Emits the entry point and every builder struct, at namespace level.</summary>
    public static void Emit(StringBuilder builder, TargetClassModel model)
    {
        bool pruning = RowGroupPruningComponent.IsEnabled(model);
        bool batches = ColumnBatchComponent.Supports(model);

        EmitEntryPoint(builder, model);
        builder.AppendLine();
        EmitStreamSource(builder, model, pruning, batches);
        builder.AppendLine();
        EmitMemorySource(builder, model, pruning, batches);
        if (pruning)
        {
            builder.AppendLine();
            EmitFilteredSource(builder, model);
        }
        builder.AppendLine();
        EmitParallelSource(builder, model);
    }

    private static void EmitEntryPoint(StringBuilder builder, TargetClassModel model)
    {
        string name = EntryPointName(model);
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// Entry point for reading <c>{model.ClassName}</c> values from Parquet (issue #217)."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public static partial class {name}");
        builder.AppendLine("{");
        builder.AppendLine(
            "    /// <summary>Reads from a <see cref=\"System.IO.Stream\"/>.</summary>"
        );
        builder.AppendLine(
            $"    public static {EntryPointName(model)}StreamSource From({Stream} stream)"
        );
        builder.AppendLine(
            $"        => new {EntryPointName(model)}StreamSource(stream ?? throw new global::System.ArgumentNullException(nameof(stream)), null);"
        );
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Reads from an in-memory buffer.</summary>");
        builder.AppendLine(
            $"    public static {EntryPointName(model)}MemorySource From({Memory} parquetBytes)"
        );
        builder.AppendLine(
            $"        => new {EntryPointName(model)}MemorySource(parquetBytes, null);"
        );
        builder.AppendLine("}");
    }

    private static void EmitStreamSource(
        StringBuilder builder,
        TargetClassModel model,
        bool pruning,
        bool batches
    )
    {
        string name = $"{EntryPointName(model)}StreamSource";
        string ext = ExtensionsName(model);

        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// A pending read of <c>{model.ClassName}</c> from a stream.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine(
            "/// There is deliberately no <c>Parallel</c> member. A single <c>ParquetReader</c> seeks"
        );
        builder.AppendLine(
            "/// within its stream, so concurrent row-group reads would corrupt one another, and an"
        );
        builder.AppendLine(
            "/// arbitrary stream cannot be handed to more than one reader. Buffer the file and use"
        );
        builder.AppendLine(
            "/// <c>From(ReadOnlyMemory&lt;byte&gt;)</c> for genuine decode parallelism."
        );
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public readonly struct {name}");
        builder.AppendLine("{");
        builder.AppendLine($"    private readonly {Stream} _stream;");
        builder.AppendLine($"    private readonly {Options}? _options;");
        builder.AppendLine();
        builder.AppendLine($"    internal {name}({Stream} stream, {Options}? options)");
        builder.AppendLine("    {");
        builder.AppendLine("        _stream = stream;");
        builder.AppendLine("        _options = options;");
        builder.AppendLine("    }");
        builder.AppendLine();
        EmitWithOptions(builder, name, "_stream");

        if (pruning)
        {
            EmitWhere(builder, model, "_stream", isStream: true);
        }

        EmitTerminal(
            builder,
            $"global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<{model.ClassName}>>",
            "ToListAsync",
            $"{ext}.ReadParquetAsync(_stream, _options, cancellationToken)"
        );
        EmitTerminal(
            builder,
            $"global::System.Threading.Tasks.Task<{model.ClassName}[]>",
            "ToArrayAsync",
            $"{ext}.ReadParquetArrayAsync(_stream, _options, cancellationToken)"
        );
        EmitTerminal(
            builder,
            $"global::System.Collections.Generic.IAsyncEnumerable<{model.ClassName}>",
            "AsAsyncEnumerable",
            $"{ext}.ReadParquetStreamAsync(_stream, _options, cancellationToken)"
        );
        if (batches)
        {
            EmitTerminal(
                builder,
                $"global::System.Collections.Generic.IAsyncEnumerable<{ext}.ColumnBatch>",
                "Batches",
                $"{ext}.ReadParquetBatchesAsync(_stream, _options, cancellationToken)"
            );
        }
        builder.AppendLine("}");
    }

    private static void EmitMemorySource(
        StringBuilder builder,
        TargetClassModel model,
        bool pruning,
        bool batches
    )
    {
        string name = $"{EntryPointName(model)}MemorySource";
        string ext = ExtensionsName(model);

        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// A pending read of <c>{model.ClassName}</c> from an in-memory buffer."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public readonly struct {name}");
        builder.AppendLine("{");
        builder.AppendLine($"    private readonly {Memory} _bytes;");
        builder.AppendLine($"    private readonly {Options}? _options;");
        builder.AppendLine();
        builder.AppendLine($"    internal {name}({Memory} bytes, {Options}? options)");
        builder.AppendLine("    {");
        builder.AppendLine("        _bytes = bytes;");
        builder.AppendLine("        _options = options;");
        builder.AppendLine("    }");
        builder.AppendLine();
        EmitWithOptions(builder, name, "_bytes");

        builder.AppendLine(
            "    /// <summary>Decodes row groups concurrently, each worker over its own view of the buffer.</summary>"
        );
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            "    /// Takes no degree argument. #239 made <c>ParquetSerializerOptions</c> the single home for"
        );
        builder.AppendLine(
            "    /// configuration, and an argument here would give the knob two homes again — the condition"
        );
        builder.AppendLine(
            "    /// #218 existed to remove. Set <c>MaxDegreeOfParallelism</c> on the options; #241 decides"
        );
        builder.AppendLine("    /// whether it moves here now this builder exists.");
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine($"    public {EntryPointName(model)}ParallelSource Parallel()");
        builder.AppendLine(
            $"        => new {EntryPointName(model)}ParallelSource(_bytes, _options);"
        );
        builder.AppendLine();

        if (pruning)
        {
            EmitWhere(builder, model, "_bytes", isStream: false);
        }

        EmitTerminal(
            builder,
            $"global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<{model.ClassName}>>",
            "ToListAsync",
            $"{ext}.ReadParquetAsync(_bytes, _options, cancellationToken)"
        );
        EmitTerminal(
            builder,
            $"global::System.Threading.Tasks.Task<{model.ClassName}[]>",
            "ToArrayAsync",
            $"{ext}.ReadParquetArrayAsync(_bytes, _options, cancellationToken)"
        );
        EmitTerminal(
            builder,
            $"global::System.Collections.Generic.IAsyncEnumerable<{model.ClassName}>",
            "AsAsyncEnumerable",
            $"{ext}.ReadParquetStreamAsync(_bytes, _options, cancellationToken)"
        );
        if (batches)
        {
            EmitTerminal(
                builder,
                $"global::System.Collections.Generic.IAsyncEnumerable<{ext}.ColumnBatch>",
                "Batches",
                $"{ext}.ReadParquetBatchesAsync(_bytes, _options, cancellationToken)"
            );
        }
        builder.AppendLine("}");
    }

    /// <summary>
    /// The filtered source. Pushdown reaches every shape here, including the buffer + <c>List</c>
    /// and buffer + array cells the flat methods never grew a predicate for: those route through
    /// the streaming overload that does accept one and collect, which is the same rows by the same
    /// pruning, rather than leaving the grid unevenly populated (defect 3 in docs/17).
    /// </summary>
    private static void EmitFilteredSource(StringBuilder builder, TargetClassModel model)
    {
        string name = $"{EntryPointName(model)}FilteredSource";
        string ext = ExtensionsName(model);
        string meta = RowGroupPruningComponent.QualifiedMetadataTypeName(model);
        string pred = $"global::System.Func<{meta}, bool>";

        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// A pending read of <c>{model.ClassName}</c> with a row-group predicate applied."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine(
            "/// There is deliberately no <c>Parallel</c> member: no parallel reader accepts a"
        );
        builder.AppendLine(
            "/// predicate yet (issue #222). When one does, this type gains the member."
        );
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public readonly struct {name}");
        builder.AppendLine("{");
        builder.AppendLine($"    private readonly {Stream}? _stream;");
        builder.AppendLine($"    private readonly {Memory} _bytes;");
        builder.AppendLine($"    private readonly {Options}? _options;");
        builder.AppendLine($"    private readonly {pred} _predicate;");
        builder.AppendLine();
        builder.AppendLine(
            $"    internal {name}({Stream}? stream, {Memory} bytes, {Options}? options, {pred} predicate)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        _stream = stream;");
        builder.AppendLine("        _bytes = bytes;");
        builder.AppendLine("        _options = options;");
        builder.AppendLine("        _predicate = predicate;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine($"    /// <summary>Replaces the serializer options.</summary>");
        builder.AppendLine($"    public {name} WithOptions({Options} options)");
        builder.AppendLine(
            $"        => new {name}(_stream, _bytes, options ?? throw new global::System.ArgumentNullException(nameof(options)), _predicate);"
        );
        builder.AppendLine();

        builder.AppendLine(
            $"    /// <summary>Materializes the surviving rows into a list.</summary>"
        );
        builder.AppendLine(
            $"    public async global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<{model.ClassName}>> ToListAsync({Ct} cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine($"        if (_stream is not null)");
        builder.AppendLine(
            $"            return await {ext}.ReadParquetAsync(_stream, _options, cancellationToken, _predicate).ConfigureAwait(false);"
        );
        builder.AppendLine();
        builder.AppendLine(
            $"        var results = new global::System.Collections.Generic.List<{model.ClassName}>();"
        );
        // No ConfigureAwait: the IAsyncEnumerable overload is an extension in
        // System.Threading.Tasks, and the emitted file carries no usings. The existing read
        // emitters omit it on `await foreach` for the same reason.
        builder.AppendLine(
            $"        await foreach (var item in {ext}.ReadParquetStreamAsync(_bytes, _options, cancellationToken, _predicate))"
        );
        builder.AppendLine("            results.Add(item);");
        builder.AppendLine("        return results;");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine(
            $"    /// <summary>Materializes the surviving rows into an array.</summary>"
        );
        builder.AppendLine(
            $"    public async global::System.Threading.Tasks.Task<{model.ClassName}[]> ToArrayAsync({Ct} cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine($"        if (_stream is not null)");
        builder.AppendLine(
            $"            return await {ext}.ReadParquetArrayAsync(_stream, _options, cancellationToken, _predicate).ConfigureAwait(false);"
        );
        builder.AppendLine();
        builder.AppendLine(
            "        return (await ToListAsync(cancellationToken).ConfigureAwait(false)).ToArray();"
        );
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine(
            $"    /// <summary>Streams the surviving rows without materializing them all.</summary>"
        );
        builder.AppendLine(
            $"    public global::System.Collections.Generic.IAsyncEnumerable<{model.ClassName}> AsAsyncEnumerable({Ct} cancellationToken = default)"
        );
        builder.AppendLine($"        => _stream is not null");
        builder.AppendLine(
            $"            ? {ext}.ReadParquetStreamAsync(_stream, _options, cancellationToken, _predicate)"
        );
        builder.AppendLine(
            $"            : {ext}.ReadParquetStreamAsync(_bytes, _options, cancellationToken, _predicate);"
        );
        builder.AppendLine("}");
    }

    private static void EmitParallelSource(StringBuilder builder, TargetClassModel model)
    {
        string name = $"{EntryPointName(model)}ParallelSource";
        string ext = ExtensionsName(model);

        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// A pending parallel read of <c>{model.ClassName}</c> from an in-memory buffer."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine(
            "/// Only the materializing shapes are offered: there is no parallel streaming or"
        );
        builder.AppendLine(
            "/// columnar-batch reader to delegate to, so offering the member would mean throwing."
        );
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public readonly struct {name}");
        builder.AppendLine("{");
        builder.AppendLine($"    private readonly {Memory} _bytes;");
        builder.AppendLine($"    private readonly {Options}? _options;");
        builder.AppendLine();
        builder.AppendLine($"    internal {name}({Memory} bytes, {Options}? options)");
        builder.AppendLine("    {");
        builder.AppendLine("        _bytes = bytes;");
        builder.AppendLine("        _options = options;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Replaces the serializer options.</summary>");
        builder.AppendLine($"    public {name} WithOptions({Options} options)");
        builder.AppendLine(
            $"        => new {name}(_bytes, options ?? throw new global::System.ArgumentNullException(nameof(options)));"
        );
        builder.AppendLine();
        EmitTerminal(
            builder,
            $"global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<{model.ClassName}>>",
            "ToListAsync",
            $"{ext}.ReadParquetParallelAsync(_bytes, _options, cancellationToken)"
        );
        EmitTerminal(
            builder,
            $"global::System.Threading.Tasks.Task<{model.ClassName}[]>",
            "ToArrayAsync",
            $"{ext}.ReadParquetParallelArrayAsync(_bytes, _options, cancellationToken)"
        );
        builder.AppendLine("}");
    }

    private static void EmitWithOptions(StringBuilder builder, string name, string sourceField)
    {
        builder.AppendLine("    /// <summary>Replaces the serializer options.</summary>");
        builder.AppendLine($"    public {name} WithOptions({Options} options)");
        builder.AppendLine(
            $"        => new {name}({sourceField}, options ?? throw new global::System.ArgumentNullException(nameof(options)));"
        );
        builder.AppendLine();
    }

    private static void EmitWhere(
        StringBuilder builder,
        TargetClassModel model,
        string sourceField,
        bool isStream
    )
    {
        string meta = RowGroupPruningComponent.QualifiedMetadataTypeName(model);
        string args = isStream ? $"{sourceField}, default" : $"null, {sourceField}";
        builder.AppendLine(
            "    /// <summary>Skips row groups whose footer statistics cannot satisfy the predicate.</summary>"
        );
        builder.AppendLine(
            $"    public {EntryPointName(model)}FilteredSource Where(global::System.Func<{meta}, bool> predicate)"
        );
        builder.AppendLine(
            $"        => new {EntryPointName(model)}FilteredSource({args}, _options, predicate ?? throw new global::System.ArgumentNullException(nameof(predicate)));"
        );
        builder.AppendLine();
    }

    private static void EmitTerminal(
        StringBuilder builder,
        string returnType,
        string name,
        string call
    )
    {
        builder.AppendLine($"    /// <summary>Executes the read.</summary>");
        builder.AppendLine($"    public {returnType} {name}({Ct} cancellationToken = default)");
        builder.AppendLine($"        => {call};");
        builder.AppendLine();
    }
}
