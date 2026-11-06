using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// Column names come from user source and can contain anything a C# string literal can. The emitter
// used to interpolate them straight into the literal it generates, so a name like the ones below
// produced generated code that would not parse — and the error was reported against the generated
// file, not against the attribute responsible for it.

[ParquetSerializable]
public partial record AwkwardColumnNames
{
    [ParquetColumn("he said \"hi\"")]
    public int Quoted { get; init; }

    [ParquetColumn(@"back\slash")]
    public int Backslash { get; init; }

    [ParquetColumn("tab\tseparated")]
    public int Tabbed { get; init; }
}

public sealed class EmitterEscapingTests
{
    [Fact]
    public void ColumnNamesWithQuotesAndBackslashesSurviveCodeGeneration()
    {
        // Reaching this assertion at all is most of the test: unescaped, the generated file would
        // fail to compile and the whole project with it. Comparing the names confirms the escaping
        // is faithful rather than merely syntactically valid — that it did not, say, drop the
        // backslash or turn \t into a literal "t".
        var names = new List<string>();
        foreach (
            global::Parquet.Schema.Field field in AwkwardColumnNamesParquetExtensions.Schema.Fields
        )
        {
            names.Add(field.Name);
        }

        names.ShouldContain("he said \"hi\"");
        names.ShouldContain(@"back\slash");
        names.ShouldContain("tab\tseparated");
    }

    [Fact]
    public async Task AwkwardColumnNamesRoundtripThroughParquet()
    {
        var written = new List<AwkwardColumnNames>
        {
            new()
            {
                Quoted = 7,
                Backslash = 9,
                Tabbed = 11,
            },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        AwkwardColumnNames[] read = await AwkwardColumnNamesParquet.From(stream).ToArrayAsync();

        read.ShouldHaveSingleItem();
        read[0].Quoted.ShouldBe(7);
        read[0].Backslash.ShouldBe(9);
        read[0].Tabbed.ShouldBe(11);
    }
}
