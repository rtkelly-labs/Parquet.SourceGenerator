using System.Text;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter;

/// <summary>
/// The generated read entry point and its reader struct (issues #217, #478).
/// <para>
/// Reads used to encode source, shape and execution into method names, with pushdown appended as a
/// parameter to some cells and not others. Naming a cross-product does not scale; expressing each
/// axis as a member does. See <c>docs/19-PUBLIC-API-SURFACE.md</c>.
/// </para>
/// <para>
/// The first builder pass (#217) was <b>type-state</b>: four public structs (stream, memory,
/// filtered, parallel source), each offering only the members valid for its state. That removed the
/// method-name cross-product and replaced it with a public state-type cross-product. #478 collapses
/// the four into one public <c>readonly struct &lt;Model&gt;ParquetReader</c> whose state — source
/// kind, options, predicate and the parallel flag — is private
/// (<c>docs/47-0.1-CONTRACT-AND-DESIGN-GOALS.md</c> §4.2).
/// </para>
/// <para>
/// Combinations the separate types made unrepresentable are now callable, so each one has defined
/// semantics rather than being silently ignored. The rule is: <b>the call that completes an
/// unsupported combination throws <c>NotSupportedException</c></b>. <c>Parallel()</c> on a stream
/// source or a filtered reader throws in <c>Parallel()</c>; <c>Where()</c> on a parallel reader
/// throws in <c>Where()</c>; a streaming or batch terminal on a parallel reader, and
/// <c>Batches()</c> on a filtered reader, throw at that terminal. A reader value therefore never
/// holds a state that no terminal can execute.
/// </para>
/// <para>
/// The struct is <c>readonly</c> and every method returns a copy, so composing a read allocates
/// nothing and the reader stays Native AOT clean. Every terminal dispatches to the internal
/// <c>Read*CoreAsync</c> implementations the removed flat methods left behind (#480).
/// </para>
/// </summary>
internal static class ReadBuilderComponent
{
    private const string Ct = "global::System.Threading.CancellationToken";
    private const string Options = "global::Parquet.SourceGenerator.ParquetSerializerOptions";
    private const string Stream = "global::System.IO.Stream";
    private const string Memory = "global::System.ReadOnlyMemory<byte>";
    private const string NotSupported = "global::System.NotSupportedException";

    private static string EntryPointName(TargetClassModel model) =>
        $"{model.ClassName.Replace(".", string.Empty)}Parquet";

    private static string ReaderName(TargetClassModel model) => $"{EntryPointName(model)}Reader";

    private static string ExtensionsName(TargetClassModel model) =>
        $"{model.ClassName.Replace(".", string.Empty)}ParquetExtensions";

    /// <summary>Emits the entry point and the reader struct, at namespace level.</summary>
    public static void Emit(StringBuilder builder, TargetClassModel model)
    {
        bool pruning = RowGroupPruningComponent.IsEnabled(model);
        bool batches = ColumnBatchComponent.Supports(model);

        EmitEntryPoint(builder, model, pruning);
        builder.AppendLine();
        EmitReader(builder, model, pruning, batches);
    }

    private static void EmitEntryPoint(StringBuilder builder, TargetClassModel model, bool pruning)
    {
        string name = EntryPointName(model);
        string reader = ReaderName(model);
        string noPredicate = pruning ? "null, " : string.Empty;
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            $"/// Entry point for reading <c>{model.ClassName}</c> values from Parquet (issues #217, #478)."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public static partial class {name}");
        builder.AppendLine("{");
        builder.AppendLine(
            "    /// <summary>Reads from a <see cref=\"System.IO.Stream\"/>. Row groups are decoded sequentially.</summary>"
        );
        builder.AppendLine($"    public static {reader} From({Stream} stream)");
        builder.AppendLine(
            $"        => new {reader}(stream ?? throw new global::System.ArgumentNullException(nameof(stream)), default, null, {noPredicate}false);"
        );
        builder.AppendLine();
        builder.AppendLine(
            "    /// <summary>Reads from an in-memory buffer. The only source that supports <c>Parallel()</c>.</summary>"
        );
        builder.AppendLine($"    public static {reader} From({Memory} parquetBytes)");
        builder.AppendLine(
            $"        => new {reader}(null, parquetBytes, null, {noPredicate}false);"
        );
        builder.AppendLine("}");
    }

    private static void EmitReader(
        StringBuilder builder,
        TargetClassModel model,
        bool pruning,
        bool batches
    )
    {
        string name = ReaderName(model);
        string entry = EntryPointName(model);
        string ext = ExtensionsName(model);
        string meta = pruning ? RowGroupPruningComponent.QualifiedMetadataTypeName(model) : "";
        string pred = $"global::System.Func<{meta}, bool>";
        // Forwarded unchanged by every copy and passed to the cores that accept a predicate.
        string predicateArg = pruning ? ", _predicate" : string.Empty;

        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// A pending read of <c>{model.ClassName}</c> (issue #478).");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine(
            "/// One reader expresses every read: the source is chosen by <c>From(...)</c>, and"
        );
        builder.AppendLine(
            "/// <c>WithOptions</c>, <c>Where</c> and <c>Parallel</c> return copies with updated state."
        );
        builder.AppendLine(
            "/// A combination no backend can execute throws <see cref=\"System.NotSupportedException\"/>"
        );
        builder.AppendLine(
            "/// from the call that completes it, rather than being silently degraded."
        );
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public readonly struct {name}");
        builder.AppendLine("{");
        builder.AppendLine(
            "    // Source kind is discriminated by _stream: non-null means a stream source, null a buffer."
        );
        builder.AppendLine($"    private readonly {Stream}? _stream;");
        builder.AppendLine($"    private readonly {Memory} _bytes;");
        builder.AppendLine($"    private readonly {Options}? _options;");
        if (pruning)
        {
            builder.AppendLine($"    private readonly {pred}? _predicate;");
        }
        builder.AppendLine("    private readonly bool _parallel;");
        builder.AppendLine();
        string predicateParam = pruning ? $", {pred}? predicate" : string.Empty;
        builder.AppendLine(
            $"    internal {name}({Stream}? stream, {Memory} bytes, {Options}? options{predicateParam}, bool parallel)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        _stream = stream;");
        builder.AppendLine("        _bytes = bytes;");
        builder.AppendLine("        _options = options;");
        if (pruning)
        {
            builder.AppendLine("        _predicate = predicate;");
        }
        builder.AppendLine("        _parallel = parallel;");
        builder.AppendLine("    }");
        builder.AppendLine();

        // WithOptions — valid in every state.
        builder.AppendLine("    /// <summary>Replaces the serializer options.</summary>");
        builder.AppendLine($"    public {name} WithOptions({Options} options)");
        builder.AppendLine(
            $"        => new {name}(_stream, _bytes, options ?? throw new global::System.ArgumentNullException(nameof(options)){predicateArg}, _parallel);"
        );
        builder.AppendLine();

        if (pruning)
        {
            EmitWhere(builder, name, pred);
        }

        EmitParallel(builder, name, entry, pruning);

        // ToArrayAsync — every representable state has a materialising path.
        builder.AppendLine(
            "    /// <summary>Materializes every surviving row into an array.</summary>"
        );
        builder.AppendLine(
            $"    public global::System.Threading.Tasks.Task<{model.ClassName}[]> ToArrayAsync({Ct} cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        if (_parallel)");
        builder.AppendLine(
            $"            return {ext}.ReadParallelArrayCoreAsync(_bytes, _options, cancellationToken);"
        );
        builder.AppendLine("        if (_stream is not null)");
        builder.AppendLine(
            $"            return {ext}.ReadArrayCoreAsync(_stream, _options, cancellationToken{predicateArg});"
        );
        if (pruning)
        {
            builder.AppendLine("        if (_predicate is not null)");
            builder.AppendLine(
                "            return CollectFilteredBufferAsync(_bytes, _options, _predicate, cancellationToken);"
            );
        }
        builder.AppendLine(
            $"        return {ext}.ReadArrayCoreAsync(_bytes, _options, cancellationToken);"
        );
        builder.AppendLine("    }");
        builder.AppendLine();

        // AsAsyncEnumerable — sequential by definition.
        builder.AppendLine(
            "    /// <summary>Streams the surviving rows without materializing them all.</summary>"
        );
        builder.AppendLine(
            "    /// <exception cref=\"System.NotSupportedException\">The reader is <c>Parallel()</c>: streaming is sequential by definition.</exception>"
        );
        builder.AppendLine(
            $"    public global::System.Collections.Generic.IAsyncEnumerable<{model.ClassName}> AsAsyncEnumerable({Ct} cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        if (_parallel)");
        builder.AppendLine(
            $"            throw new {NotSupported}(\"AsAsyncEnumerable() cannot follow Parallel(): the parallel reader only produces materialised results and streaming is sequential by definition. Use ToArrayAsync(), or drop Parallel() to stream.\");"
        );
        builder.AppendLine("        return _stream is not null");
        builder.AppendLine(
            $"            ? {ext}.ReadEnumerableCoreAsync(_stream, _options, cancellationToken{predicateArg})"
        );
        builder.AppendLine(
            $"            : {ext}.ReadEnumerableCoreAsync(_bytes, _options, cancellationToken{predicateArg});"
        );
        builder.AppendLine("    }");
        builder.AppendLine();

        if (batches)
        {
            EmitBatches(builder, ext, pruning);
        }

        if (pruning)
        {
            EmitCollectFilteredBuffer(builder, model, ext, pred);
        }

        builder.AppendLine("}");
    }

    private static void EmitWhere(StringBuilder builder, string name, string pred)
    {
        builder.AppendLine(
            "    /// <summary>Skips row groups whose footer statistics cannot satisfy the predicate.</summary>"
        );
        builder.AppendLine(
            "    /// <exception cref=\"System.NotSupportedException\">The reader is already <c>Parallel()</c> (no parallel reader accepts a predicate yet, #222), or already has a predicate (combine the conditions into one).</exception>"
        );
        builder.AppendLine($"    public {name} Where({pred} predicate)");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        if (predicate is null) throw new global::System.ArgumentNullException(nameof(predicate));"
        );
        builder.AppendLine("        if (_parallel)");
        builder.AppendLine(
            $"            throw new {NotSupported}(\"Where() cannot be combined with Parallel(): no parallel reader accepts a row-group predicate yet (#222). Drop Parallel() to filter sequentially.\");"
        );
        builder.AppendLine("        if (_predicate is not null)");
        builder.AppendLine(
            $"            throw new {NotSupported}(\"Where() has already been applied to this reader. Combine the conditions into a single predicate.\");"
        );
        builder.AppendLine(
            $"        return new {name}(_stream, _bytes, _options, predicate, _parallel);"
        );
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitParallel(StringBuilder builder, string name, string entry, bool pruning)
    {
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
        builder.AppendLine(
            $"    /// <exception cref=\"System.NotSupportedException\">The source is a stream (use <c>{entry}.From(ReadOnlyMemory&lt;byte&gt;)</c>){(pruning ? ", or the reader has a <c>Where()</c> predicate" : string.Empty)}.</exception>"
        );
        builder.AppendLine($"    public {name} Parallel()");
        builder.AppendLine("    {");
        builder.AppendLine("        if (_stream is not null)");
        builder.AppendLine(
            $"            throw new {NotSupported}(\"Parallel() is not supported on a Stream source: a single ParquetReader seeks within its stream, so row groups cannot be decoded concurrently. Buffer the file and read it with {entry}.From(ReadOnlyMemory<byte>), the parallel source.\");"
        );
        if (pruning)
        {
            builder.AppendLine("        if (_predicate is not null)");
            builder.AppendLine(
                $"            throw new {NotSupported}(\"Parallel() cannot be combined with Where(): no parallel reader accepts a row-group predicate yet (#222). Drop Where() to read in parallel, or drop Parallel() to filter.\");"
            );
            builder.AppendLine(
                $"        return new {name}(_stream, _bytes, _options, _predicate, true);"
            );
        }
        else
        {
            builder.AppendLine($"        return new {name}(_stream, _bytes, _options, true);");
        }
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitBatches(StringBuilder builder, string ext, bool pruning)
    {
        builder.AppendLine(
            "    /// <summary>Streams struct-of-arrays column batches, one per row group (#147).</summary>"
        );
        builder.AppendLine(
            $"    /// <exception cref=\"System.NotSupportedException\">The reader is <c>Parallel()</c>{(pruning ? ", or has a <c>Where()</c> predicate" : string.Empty)}.</exception>"
        );
        builder.AppendLine(
            $"    public global::System.Collections.Generic.IAsyncEnumerable<{ext}.ColumnBatch> Batches({Ct} cancellationToken = default)"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        if (_parallel)");
        builder.AppendLine(
            $"            throw new {NotSupported}(\"Batches() cannot follow Parallel(): there is no parallel column-batch reader and batch streaming is sequential by definition. Drop Parallel().\");"
        );
        if (pruning)
        {
            builder.AppendLine("        if (_predicate is not null)");
            builder.AppendLine(
                $"            throw new {NotSupported}(\"Batches() cannot follow Where(): the column-batch reader does not accept a row-group predicate. Use AsAsyncEnumerable() or ToArrayAsync() to filter.\");"
            );
        }
        builder.AppendLine("        return _stream is not null");
        builder.AppendLine(
            $"            ? {ext}.ReadBatchesCoreAsync(_stream, _options, cancellationToken)"
        );
        builder.AppendLine(
            $"            : {ext}.ReadBatchesCoreAsync(_bytes, _options, cancellationToken);"
        );
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>
    /// Buffer + predicate + array. The buffer array core takes no predicate, so this routes through
    /// the streaming core that does and collects — the same rows by the same pruning — rather than
    /// leaving the cell unavailable (defect 3 in docs/19). Unchanged from the #217 filtered source.
    /// </summary>
    private static void EmitCollectFilteredBuffer(
        StringBuilder builder,
        TargetClassModel model,
        string ext,
        string pred
    )
    {
        builder.AppendLine(
            $"    private static async global::System.Threading.Tasks.Task<{model.ClassName}[]> CollectFilteredBufferAsync({Memory} bytes, {Options}? options, {pred} predicate, {Ct} cancellationToken)"
        );
        builder.AppendLine("    {");
        builder.AppendLine(
            $"        var results = new global::System.Collections.Generic.List<{model.ClassName}>();"
        );
        // No ConfigureAwait: the IAsyncEnumerable overload is an extension in
        // System.Threading.Tasks, and the emitted file carries no usings. The existing read
        // emitters omit it on `await foreach` for the same reason.
        builder.AppendLine(
            $"        await foreach (var item in {ext}.ReadEnumerableCoreAsync(bytes, options, cancellationToken, predicate))"
        );
        builder.AppendLine("            results.Add(item);");
        builder.AppendLine("        return results.ToArray();");
        builder.AppendLine("    }");
    }
}
