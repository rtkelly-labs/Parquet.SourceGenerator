using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// A <c>[ParquetColumn]</c> name is arbitrary user text. Every place the emitters write one into
/// generated source (a string literal, a <c>//</c> comment, an XML doc comment) has to escape it
/// for that place (#363, #364, #372). The names below are hostile in different ways; the model is
/// run through the real generator and the output must compile cleanly, carry no code the name
/// smuggled in, and still describe the column the author named.
/// </summary>
public sealed class EmitterNameInjectionTests
{
    // Written with C# escapes inside a raw string, so the generated source holds the escape
    // sequences, not the characters themselves.
    private static readonly string HostileSource = Escapes(
        """
        using System.Collections.Generic;
        using Parquet.SourceGenerator;

        namespace Hostile;

        [ParquetSerializable]
        public partial class Flat
        {
            [ParquetColumn("he said \"hi\"", Encoding = ParquetColumnEncoding.Dictionary)]
            public int Quoted { get; init; }

            [ParquetColumn(@"back\slash", Encoding = ParquetColumnEncoding.Dictionary)]
            public int Backslash { get; init; }

            [ParquetColumn("line1\nSystem.Environment.Exit(1);\n//", Encoding = ParquetColumnEncoding.Dictionary)]
            public int Newline { get; init; }

            [ParquetColumn("cr\rlf", Encoding = ParquetColumnEncoding.Dictionary)]
            public int CarriageReturn { get; init; }

            [ParquetColumn("sep@@LS@@para@@PS@@next@@NEL@@", Encoding = ParquetColumnEncoding.Dictionary)]
            public int UnicodeLineEnds { get; init; }

            [ParquetColumn("end */ System.Environment.Exit(2); /*", Encoding = ParquetColumnEncoding.Dictionary)]
            public int CommentClose { get; init; }

            [ParquetColumn("}{ ; }", Encoding = ParquetColumnEncoding.Dictionary)]
            public int Braces { get; init; }

            [ParquetColumn("a<b>&c</c></summary>", Encoding = ParquetColumnEncoding.Dictionary)]
            public int Markup { get; init; }

            [ParquetColumn("\"] = global::Parquet.EncodingHint.Default; System.Environment.Exit(3); x[\"y", Encoding = ParquetColumnEncoding.Dictionary)]
            public int Breakout { get; init; }

            [ParquetColumn("caf@@EACUTE@@ @@CHECK@@ @@SMILE@@", Encoding = ParquetColumnEncoding.Dictionary)]
            public int Unicode { get; init; }

            [ParquetColumn("tab\there", Encoding = ParquetColumnEncoding.Dictionary)]
            public string? Text { get; init; }
        }

        [ParquetSerializable]
        public partial class Inner
        {
            [ParquetColumn("in\nner\"; System.Environment.Exit(4); //")]
            public int Value { get; init; }
        }

        [ParquetSerializable]
        public partial class Compound
        {
            [ParquetColumn("id\n")]
            public int Id { get; init; }

            [ParquetColumn("child*/&<\"")]
            public Inner? Child { get; init; }

            [ParquetColumn("list\\\n\"")]
            public List<int> Items { get; init; } = new();
        }
        """
    );

    private static readonly string LineSeparator = char.ConvertFromUtf32(0x2028);
    private static readonly string ParagraphSeparator = char.ConvertFromUtf32(0x2029);
    private static readonly string NextLine = char.ConvertFromUtf32(0x85);

    private static readonly string[] CompoundColumnNames = { "id\n", "child*/&<\"", "list\\\n\"" };

    private static readonly string[] FlatColumnNames =
    {
        "he said \"hi\"",
        @"back\slash",
        "line1\nSystem.Environment.Exit(1);\n//",
        "cr\rlf",
        "sep" + LineSeparator + "para" + ParagraphSeparator + "next" + NextLine,
        "end */ System.Environment.Exit(2); /*",
        "}{ ; }",
        "a<b>&c</c></summary>",
        "\"] = global::Parquet.EncodingHint.Default; System.Environment.Exit(3); x[\"y",
        "caf"
            + char.ConvertFromUtf32(0xE9)
            + " "
            + char.ConvertFromUtf32(0x2713)
            + " "
            + char.ConvertFromUtf32(0x1F600),
        "tab\there",
    };

    // The tokens stand for characters that cannot sit literally in a C# string literal (the line
    // separators) or in this file; the generated source gets their \u escape sequences.
    private static string Escapes(string source) =>
        source
            .Replace("@@LS@@", "\\" + "u2028")
            .Replace("@@PS@@", "\\" + "u2029")
            .Replace("@@NEL@@", "\\" + "u0085")
            .Replace("@@EACUTE@@", "\\" + "u00e9")
            .Replace("@@CHECK@@", "\\" + "u2713")
            .Replace("@@SMILE@@", "\\" + "ud83d" + "\\" + "ude00");

    [Fact]
    public void HostileColumnNamesGenerateSourceThatCompilesCleanly()
    {
        (Compilation output, var generatorDiagnostics) = GeneratedSourceHarness.Generate(
            HostileSource
        );

        generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        GeneratedSourceHarness.CompileProblems(output).ShouldBeEmpty();
    }

    [Fact]
    public void HostileColumnNamesSmuggleNoCodeIntoTheGeneratedSource()
    {
        (Compilation output, _) = GeneratedSourceHarness.Generate(HostileSource);

        // The injected statements all call System.Environment.Exit. As data they appear only
        // inside string literals and comments; as code `Exit` would be an identifier.
        foreach (SyntaxTree tree in output.SyntaxTrees.Skip(1))
        {
            SyntaxNode root = tree.GetRoot();
            root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(i => i.Identifier.ValueText == "Exit")
                .Select(i => $"{tree.FilePath}: {i}")
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public void HostileColumnNamesRoundTripIntoTheEmittedSchema()
    {
        (Compilation output, _) = GeneratedSourceHarness.Generate(HostileSource);
        System.Reflection.Assembly assembly = GeneratedSourceHarness.Load(output);

        Type flat = assembly.GetType("Hostile.FlatParquetExtensions", throwOnError: true)!;
        var schema = (global::Parquet.Schema.ParquetSchema)
            flat.GetField("Schema", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        schema.Fields.Select(f => f.Name).ShouldBe(FlatColumnNames, ignoreOrder: true);

        Type compound = assembly.GetType("Hostile.CompoundParquetExtensions", throwOnError: true)!;
        var compoundSchema = (global::Parquet.Schema.ParquetSchema)
            compound
                .GetField("Schema", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
        compoundSchema.Fields.Select(f => f.Name).ShouldBe(CompoundColumnNames, ignoreOrder: true);
    }
}
