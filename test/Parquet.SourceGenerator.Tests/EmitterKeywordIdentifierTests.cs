using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// Models declared in this project, so the same keyword names also run through a real write and
// read. A keyword-named property, a struct child and a list, and a flat model for the batch path.

[ParquetSerializable]
public partial record KeywordInner
{
    public int @in { get; init; }
}

[ParquetSerializable]
public partial record KeywordRow
{
    public int @class { get; init; }

    public int? @lock { get; init; }

    public string @struct { get; init; } = "";

    public KeywordInner? @params { get; init; }

    public List<int> @checked { get; init; } = new();
}

[ParquetSerializable]
public partial record KeywordFlat
{
    public int @event { get; init; }

    public int? @lock { get; init; }

    public string? @operator { get; init; }

    public int @value { get; init; }
}

/// <summary>
/// Roslyn's <c>ISymbol.Name</c> drops the <c>@</c> from a verbatim identifier, so a property
/// declared <c>@event</c> reached the emitters as the bare keyword and was written into member
/// access, object initialisers and generated members unescaped (#376). Every emitted identifier
/// that comes from the model now goes through <c>EmittedText.Ident</c>.
/// </summary>
public sealed class EmitterKeywordIdentifierTests
{
    private static readonly int[] EventValues = [1, 2];
    private static readonly int?[] LockValues = [10, null];
    private static readonly string?[] OperatorValues = ["a", null];
    private static readonly int[] ValueValues = [7, 8];

    // Reserved keywords, contextual keywords and a type name, in every shape the emitters handle:
    // plain, nullable, string, list, struct, and a target type that is itself a keyword.
    private const string KeywordSource = """
        using System.Collections.Generic;
        using Parquet.SourceGenerator;

        namespace Keywords;

        [ParquetSerializable]
        public partial class @event
        {
            public int @class { get; init; }
            public int? @lock { get; init; }
            public string @string { get; init; } = "";
            public string? @object { get; init; }
            public double @default { get; init; }
            public long @base { get; init; }
            public int @var { get; init; }
            public int @value { get; init; }
            public int @async { get; init; }
            public int @this { get; init; }
        }

        [ParquetSerializable]
        public partial class @namespace
        {
            public int @if { get; init; }
            public @event? @params { get; init; }
            public List<int> @checked { get; init; } = new();
            public List<@event> @foreach { get; init; } = new();
        }
        """;

    [Fact]
    public void KeywordNamedMembersAndTypesGenerateSourceThatCompilesCleanly()
    {
        (Compilation output, var generatorDiagnostics) = GeneratedSourceHarness.Generate(
            KeywordSource
        );

        generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        GeneratedSourceHarness.CompileProblems(output).Take(20).ShouldBeEmpty();
    }

    [Fact]
    public async Task KeywordNamedMembersRoundTripThroughRowsAsync()
    {
        var written = new List<KeywordRow>
        {
            new()
            {
                @class = 1,
                @lock = 2,
                @struct = "s",
                @params = new KeywordInner { @in = 3 },
                @checked = new List<int> { 4, 5 },
            },
            new() { @class = 6, @lock = null },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        KeywordRow[] read = await KeywordRowParquet.From(stream).ToArrayAsync();

        read.Length.ShouldBe(2);
        read[0].@class.ShouldBe(1);
        read[0].@lock.ShouldBe(2);
        read[0].@struct.ShouldBe("s");
        read[0].@params!.@in.ShouldBe(3);
        read[0].@checked.ShouldBe(new List<int> { 4, 5 });
        read[1].@class.ShouldBe(6);
        read[1].@lock.ShouldBeNull();
    }

    [Fact]
    public async Task KeywordNamedColumnsRoundTripThroughBatchesAsync()
    {
        var written = new List<KeywordFlat>
        {
            new()
            {
                @event = 1,
                @lock = 10,
                @operator = "a",
                @value = 7,
            },
            new()
            {
                @event = 2,
                @lock = null,
                @operator = null,
                @value = 8,
            },
        };
        using var source = new MemoryStream();
        await written.WriteParquetAsync(source);

        var rows = new List<KeywordFlat>();
        await foreach (
            KeywordFlatBatch batch in KeywordFlatParquet
                .From(new MemoryStream(source.ToArray()))
                .AsBatches()
        )
        {
            using var rewritten = new MemoryStream();
            await batch.WriteParquetAsync(rewritten);
            rewritten.Position = 0;
            rows.AddRange(await KeywordFlatParquet.From(rewritten).ToArrayAsync());
        }

        rows.Select(r => r.@event).ShouldBe(EventValues);
        rows.Select(r => r.@lock).ShouldBe(LockValues);
        rows.Select(r => r.@operator).ShouldBe(OperatorValues);
        rows.Select(r => r.@value).ShouldBe(ValueValues);
    }
}
