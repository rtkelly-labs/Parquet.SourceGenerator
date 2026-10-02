using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Models;
using Parquet.SourceGenerator.Parser;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial struct FahrenheitBacked
{
    private float _fahrenheit;

    [ParquetColumn("celsius")]
    public float Value
    {
        get => (_fahrenheit - 32f) / 1.8f;
        set => _fahrenheit = (value * 1.8f) + 32f;
    }
}

[ParquetSerializable]
public partial struct PlainCelsius
{
    [ParquetColumn("celsius")]
    public float Value { get; set; }
}

/// <summary>
/// Issue #389: the blittable fast path reinterprets <c>TStruct[]</c> as <c>TField[]</c>, which is
/// only sound when the single serialized member IS the single field. Eligibility used to check the
/// fields and the properties separately, so a struct whose property computes its value from a
/// differently-meaning field took the cast and wrote the field's bytes.
/// </summary>
public sealed class SingleFieldFastPathTests
{
    private const string Source = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial struct Plain
        {
            public float Value { get; set; }
        }

        [ParquetSerializable]
        public partial struct PublicField
        {
            public int Value;
        }

        [ParquetSerializable]
        public partial struct Celsius
        {
            private float _fahrenheit;

            public float Value
            {
                get => (_fahrenheit - 32f) / 1.8f;
                set => _fahrenheit = (value * 1.8f) + 32f;
            }
        }

        [ParquetSerializable]
        public partial struct Flag
        {
            private byte _b;

            public bool Value
            {
                get => _b != 0;
                set => _b = value ? (byte)1 : (byte)0;
            }
        }

        [ParquetSerializable]
        public partial struct Widened
        {
            private int _raw;

            public long Value
            {
                get => _raw;
                set => _raw = (int)value;
            }
        }

        [ParquetSerializable]
        public partial record struct PlainRecord(int Value);
        """;

    private static TargetParserResult Parse(string name)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "FastPathAssembly",
            [CSharpSyntaxTree.ParseText(Source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
                MetadataReference.CreateFromFile(
                    typeof(ParquetSerializableAttribute).Assembly.Location
                ),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        INamedTypeSymbol symbol = compilation.GetTypeByMetadataName("Demo." + name)!;
        return TargetParser.GetTargetModel(symbol, ParquetApiLevel.V6, allowCompoundTypes: false);
    }

    [Theory]
    [InlineData("Plain", true)]
    [InlineData("PublicField", true)]
    [InlineData("PlainRecord", true)]
    [InlineData("Celsius", false)]
    [InlineData("Flag", false)]
    [InlineData("Widened", false)]
    public void TheFastPathNeedsTheSerializedMemberToBeTheSingleField(string name, bool eligible)
    {
        TargetParserResult result = Parse(name);

        result.Model.ShouldNotBeNull(
            string.Join("; ", result.Diagnostics.Select(d => d.Descriptor.Id))
        );
        result.Model.HasSingleInstanceField.ShouldBe(eligible);
        PropertyMappingComponent.IsSingleFieldBlittableStruct(result.Model).ShouldBe(eligible);
    }

    [Theory]
    [InlineData("Plain", true)]
    [InlineData("Celsius", false)]
    [InlineData("Flag", false)]
    public void EmittedCodeCastsTheBufferOnlyWhenTheFastPathIsEligible(string name, bool cast)
    {
        TargetParserResult result = Parse(name);

        string emitted = CodeEmitter.EmitSource(result.Model!, GeneratorConfiguration.Default);

        (emitted.Contains("MemoryMarshal.Cast", StringComparison.Ordinal)).ShouldBe(cast);
    }

    [Fact]
    public async Task EveryCollectionPathWritesThePropertyValueAsync()
    {
        // 100 is stored as 212 in the field. Each path (List<T> and T[]) is read back through a plain auto-property
        // struct on the same column, so the file's values are seen without the symmetric cast on
        // the read side of the model that wrote them.
        var rows = new List<FahrenheitBacked>
        {
            new() { Value = 100f },
            new() { Value = 0f },
        };

        using var fromList = new MemoryStream();
        await rows.WriteParquetAsync(fromList);
        using var fromArray = new MemoryStream();
        await rows.ToArray().WriteParquetAsync(fromArray);

        foreach (MemoryStream stream in new[] { fromList, fromArray })
        {
            stream.Position = 0;
            PlainCelsius[] read = await PlainCelsiusParquet.From(stream).ToArrayAsync();
            read.Select(r => (float)Math.Round(r.Value, 3)).ShouldBe([100f, 0f]);
        }
    }
}
