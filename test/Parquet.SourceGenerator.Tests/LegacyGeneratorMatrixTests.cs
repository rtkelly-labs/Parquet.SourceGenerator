extern alias LegacyGenerator;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Runs the legacy generator in process over the exact models the executed legacy suite compiles
/// (test/Shared/Behaviour and the legacy hostile models, #597).
/// </summary>
/// <remarks>
/// <c>Parquet.SourceGenerator.Legacy.Tests</c> executes what the legacy generator emits for these
/// models against Parquet.Net 4.25, but there the generator itself runs inside the compiler, where no
/// coverage collector sees it. Running the same generator over the same sources here is what makes
/// the generator code behind that suite count in the coverage report's
/// <c>Parquet.SourceGenerator.Legacy</c> row, and it fails fast (no Windows runner, no Parquet.Net
/// 4.25) when a model stops generating.
/// </remarks>
public sealed class LegacyGeneratorMatrixTests
{
    private static readonly string[] ModelFiles =
    [
        Path.Combine("test", "Shared", "Behaviour", "BehaviourModels.cs"),
        Path.Combine("test", "Parquet.SourceGenerator.Legacy.Tests", "Hostile", "HostileModels.cs"),
    ];

    private static readonly string[] Net8Symbols =
    [
        "NET",
        "NET6_0_OR_GREATER",
        "NET8_0_OR_GREATER",
    ];

    private static readonly string[] Net472Symbols = ["NETFRAMEWORK", "NET472"];

    public static TheoryData<string> Targets => new() { "net8.0", "net472" };

    [Theory]
    [MemberData(nameof(Targets))]
    public void EveryExecutedLegacyModelGeneratesWithoutDiagnostics(string target)
    {
        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<SyntaxTree> trees) = Run(target);

        diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ShouldBeEmpty();

        // One serializer per [ParquetSerializable] model: three shared, four hostile.
        trees.Length.ShouldBe(7);
        foreach (SyntaxTree tree in trees)
        {
            SyntaxTree reparsed = CSharpSyntaxTree.ParseText(
                tree.ToString(),
                Parse(target),
                tree.FilePath
            );
            reparsed
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ShouldBeEmpty(tree.FilePath);
        }
    }

    [Fact]
    public void TheTypeMatrixModelUsesTheParquetNet425TimeFieldsOnNet8()
    {
        string code = TypeMatrix(Run("net8.0").Trees);

        code.ShouldContain(
            "new global::Parquet.Schema.TimeSpanDataField(\"timespan\", global::Parquet.Schema.TimeSpanFormat.MilliSeconds, isNullable: false)"
        );
        code.ShouldContain(
            "new global::Parquet.Schema.TimeSpanDataField(\"timeonly_opt\", global::Parquet.Schema.TimeSpanFormat.MicroSeconds, isNullable: true)"
        );
        code.ShouldContain("global::Parquet.Schema.DateTimeFormat.DateAndTimeMicros");
        code.ShouldContain(
            "#error Parquet.SourceGenerator.Legacy: [ParquetTimestamp(Microseconds)]"
        );
        code.ShouldContain("ReadClassicTimeOnly(");
        code.ShouldNotContain("global::Parquet.Schema.TimeDataField(");
    }

    [Fact]
    public void BelowNet8TheMatrixModelHasNoMicrosecondOrTimeOnlyColumn()
    {
        // The shared model declares those members for net8.0+ only, as the legacy suite needs.
        string code = TypeMatrix(Run("net472").Trees);

        code.ShouldNotContain("\"datetime_micros\"");
        code.ShouldNotContain("\"timeonly\"");
        code.ShouldNotContain("ReadClassicTimeOnly(");
        code.ShouldContain("TimeSpanFormat.MilliSeconds");
    }

    private static string TypeMatrix(ImmutableArray<SyntaxTree> trees) =>
        trees
            .Single(t =>
                t.FilePath.EndsWith(
                    "Behaviour.TypeMatrixRow.ParquetLegacySerializer.g.cs",
                    StringComparison.Ordinal
                )
            )
            .ToString();

    private static CSharpParseOptions Parse(string target) =>
        CSharpParseOptions.Default.WithPreprocessorSymbols(
            target == "net8.0" ? Net8Symbols : Net472Symbols
        );

    private static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<SyntaxTree> Trees) Run(
        string target
    )
    {
        string root = FindRepositoryRoot();
        CSharpParseOptions parse = Parse(target);
        IEnumerable<SyntaxTree> sources = ModelFiles.Select(file =>
            CSharpSyntaxTree.ParseText(IOFile.ReadAllText(Path.Combine(root, file)), parse, file)
        );

        IEnumerable<MetadataReference> references = (
            (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!
        )
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(
                MetadataReference.CreateFromFile(
                    typeof(ParquetSerializableAttribute).Assembly.Location
                )
            )
            .Append(
                MetadataReference.CreateFromFile(
                    typeof(Parquet.File.Values.Primitives.Interval).Assembly.Location
                )
            );

        CSharpCompilation compilation = CSharpCompilation.Create(
            "LegacyMatrixModels",
            sources,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        var generator =
            new LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            parseOptions: parse
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out ImmutableArray<Diagnostic> diagnostics
        );
        return (diagnostics, driver.GetRunResult().GeneratedTrees);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (IOFile.Exists(Path.Combine(directory.FullName, "Parquet.SourceGenerator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
