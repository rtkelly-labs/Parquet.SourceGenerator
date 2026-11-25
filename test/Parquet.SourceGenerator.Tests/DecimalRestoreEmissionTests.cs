extern alias LegacyGenerator;

using System;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The emitted half of the .NET 11 decimal correction (<see cref="ParquetDecimalScale"/>): both
/// backends emit the physical-type check and a restore after every decimal read, and nothing at
/// all for a model without decimals. The behaviour is covered end to end by
/// <see cref="DecimalScaleRoundTripTests"/> on CI's .NET 11 job; this pins the shape on every runtime.
/// </summary>
public sealed class DecimalRestoreEmissionTests
{
    private const string DecimalSource = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Invoice
        {
            [ParquetColumn("id")]
            public int Id { get; init; }

            [ParquetColumn("total")]
            [ParquetDecimal(18, 2)]
            public decimal Total { get; init; }

            [ParquetColumn("discount")]
            [ParquetDecimal(9, 4)]
            public decimal? Discount { get; init; }
        }
        """;

    private const string DecimalFreeSource = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class Reading
        {
            [ParquetColumn("id")]
            public int Id { get; init; }

            [ParquetColumn("value")]
            public double? Value { get; init; }
        }
        """;

    private const string Helper = "private static int IntegerDecimalScale(";
    private const string Restore =
        "global::Parquet.SourceGenerator.ParquetDecimalScale.RestoreIntegerScaled(";

    [Fact]
    public void ModernBackendRestoresEveryDecimalReadOfARequiredAndANullableColumn()
    {
        string emitted = Generate(DecimalSource, legacy: false);

        Count(emitted, Helper).ShouldBe(1);
        emitted.ShouldContain(
            "global::Parquet.Meta.Type.INT32 || type == global::Parquet.Meta.Type.INT64"
        );
        // The required column's buffer is decimal, the nullable one's decimal?; the batch reader's
        // packed lane is restored over its packed count.
        emitted.ShouldContain($"{Restore}new global::System.Span<decimal>(");
        emitted.ShouldContain($"{Restore}new global::System.Span<decimal?>(");
        emitted.ShouldContain("packed_");
    }

    [Fact]
    public void LegacyBackendRestoresEveryDecimalRead()
    {
        string emitted = Generate(DecimalSource, legacy: true);

        Count(emitted, Helper).ShouldBe(1);
        // One restore per decimal column; the nullable one is skipped when the column is absent.
        Count(emitted, Restore).ShouldBe(2);
        emitted.ShouldContain($"if (!missing_2) {Restore}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AModelWithoutDecimalsEmitsNoCorrection(bool legacy)
    {
        string emitted = Generate(DecimalFreeSource, legacy);

        emitted.ShouldNotContain("IntegerDecimalScale");
        emitted.ShouldNotContain("ParquetDecimalScale");
    }

    private static string Generate(string source, bool legacy)
    {
        var compilation = CSharpCompilation.Create(
            "DecimalRestoreEmission",
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
                MetadataReference.CreateFromFile(
                    typeof(ParquetSerializableAttribute).Assembly.Location
                ),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(
                    typeof(global::Parquet.ParquetWriter).Assembly.Location
                ),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        IIncrementalGenerator generator = legacy
            ? new LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator()
            : new ParquetIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);

        return string.Concat(
            driver
                .GetRunResult()
                .Results.Single()
                .GeneratedSources.Select(s => s.SourceText.ToString())
        );
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (
            int i = text.IndexOf(needle, StringComparison.Ordinal);
            i >= 0;
            i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }
}
