extern alias LegacyGenerator;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Adapter discovery, precedence and validation (docs/44-TYPE-ADAPTERS.md §5, §6, §11, §12, §18).
/// Every malformed adapter must surface as a generator diagnostic at the member — never as broken
/// generated C# — and every accepted one must produce generated code that compiles.
/// </summary>
public sealed class TypeAdapterDiagnosticTests
{
    private const string Types = """

        public sealed class Temperature { public double Celsius { get; init; } }

        """;

    private const string Prelude = "using Parquet.SourceGenerator;\n" + Types;

    // Assembly attributes must precede every type declaration in the file.
    private const string Registered =
        "using Parquet.SourceGenerator;\n"
        + "[assembly: ParquetTypeAdapter(typeof(TemperatureAdapter))]\n"
        + Types;

    private const string ValidAdapter = """
        [ParquetTypeAdapter(typeof(Temperature), typeof(double))]
        public static class TemperatureAdapter
        {
            public static double ToStorage(this Temperature value) => value.Celsius;
            public static Temperature FromStorage(this double value) => new() { Celsius = value };
        }
        """;

    private const string Model = """
        [ParquetSerializable]
        public partial class Reading
        {
            public int Id { get; init; }
            public Temperature Temp { get; init; } = new();
            public Temperature? Maybe { get; init; }
        }
        """;

    [Fact]
    public void WithoutARegistrationTheTypeIsUnsupported()
    {
        GeneratorRun run = Run(Prelude + ValidAdapter + Model);

        run.Ids.ShouldContain(DiagnosticDescriptors.UnsupportedPropertyType.Id);
        run.HasShadowFile.ShouldBeFalse();
    }

    [Fact]
    public void AProjectRegistrationMakesTheTypeSerializableAndTheOutputCompiles()
    {
        GeneratorRun run = Run(Registered + ValidAdapter + Model);

        run.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", run.GeneratorDiagnostics));
        run.CompileErrors.ShouldBeEmpty(string.Join("\n", run.CompileErrors));
        run.HasShadowFile.ShouldBeTrue();
        run.ShadowSource!.ShouldContain("internal double TempParquetStorage");
        run.ShadowSource!.ShouldContain("global::TemperatureAdapter.ToStorage(this.Temp)");
    }

    [Fact]
    public void AReferencedPackageRegistrationIsDiscoveredAndRemovingItRestoresTheDiagnostic()
    {
        MetadataReference package = CompileLibrary(Registered + ValidAdapter);
        const string consumer = "using Parquet.SourceGenerator;\n" + Model;

        GeneratorRun with = Run(consumer, package);
        with.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", with.GeneratorDiagnostics));
        with.CompileErrors.ShouldBeEmpty(string.Join("\n", with.CompileErrors));

        GeneratorRun without = Run(consumer, CompileLibrary(Prelude + ValidAdapter));
        without.Ids.ShouldContain(DiagnosticDescriptors.UnsupportedPropertyType.Id);
    }

    [Fact]
    public void AnExplicitMemberAdapterOverridesABuiltInMapping()
    {
        const string source = """
            using System;
            using Parquet.SourceGenerator;

            [ParquetTypeAdapter(typeof(DateTime), typeof(long))]
            public static class TicksAdapter
            {
                public static long ToStorage(DateTime value) => value.Ticks;
                public static DateTime FromStorage(long value) => new(value, DateTimeKind.Utc);
            }

            [ParquetSerializable]
            public partial class Row
            {
                [ParquetAdapter(typeof(TicksAdapter))]
                public DateTime At { get; set; }
            }
            """;

        GeneratorRun run = Run(source);

        run.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", run.GeneratorDiagnostics));
        run.CompileErrors.ShouldBeEmpty(string.Join("\n", run.CompileErrors));
        run.ShadowSource!.ShouldContain("internal long AtParquetStorage");
        run.ShadowSource!.ShouldContain(
            "set => this.At = global::TicksAdapter.FromStorage(value);"
        );
    }

    [Fact]
    public void AProjectRegistrationForABuiltInTypeIsIgnoredWithAWarning()
    {
        const string source = """
            using System;
            using Parquet.SourceGenerator;

            [assembly: ParquetTypeAdapter(typeof(TicksAdapter))]

            [ParquetTypeAdapter(typeof(DateTime), typeof(long))]
            public static class TicksAdapter
            {
                public static long ToStorage(DateTime value) => value.Ticks;
                public static DateTime FromStorage(long value) => new(value, DateTimeKind.Utc);
            }

            [ParquetSerializable]
            public partial class Row { public DateTime At { get; set; } }
            """;

        GeneratorRun run = Run(source);

        run.Ids.ShouldBe([DiagnosticDescriptors.BuiltInAdapterRegistrationIgnored.Id]);
        run.HasShadowFile.ShouldBeFalse();
        run.CompileErrors.ShouldBeEmpty(string.Join("\n", run.CompileErrors));
    }

    [Fact]
    public void TwoDefaultRegistrationsAreAmbiguous()
    {
        const string second = """
            [ParquetTypeAdapter(typeof(Temperature), typeof(string))]
            public static class TemperatureTextAdapter
            {
                public static string ToStorage(Temperature value) => value.Celsius.ToString(System.Globalization.CultureInfo.InvariantCulture);
                public static Temperature FromStorage(string value) => new() { Celsius = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
            }
            """;

        GeneratorRun run = Run(
            "using Parquet.SourceGenerator;\n"
                + "[assembly: ParquetTypeAdapter(typeof(TemperatureAdapter))]\n"
                + "[assembly: ParquetTypeAdapter(typeof(TemperatureTextAdapter))]\n"
                + Types
                + ValidAdapter
                + second
                + Model
        );

        Diagnostic ambiguous = run.GeneratorDiagnostics.First(d =>
            d.Id == DiagnosticDescriptors.AmbiguousTypeAdapter.Id
        );
        ambiguous
            .GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("TemperatureAdapter, TemperatureTextAdapter");
        run.HasShadowFile.ShouldBeFalse();
    }

    [Fact]
    public void AProjectRegistrationWinsOverAPackageDefaultWithoutAmbiguity()
    {
        MetadataReference package = CompileLibrary(Registered + ValidAdapter);
        const string local = """
            using Parquet.SourceGenerator;

            [assembly: ParquetTypeAdapter(typeof(LocalTemperatureAdapter))]

            [ParquetTypeAdapter(typeof(Temperature), typeof(float))]
            public static class LocalTemperatureAdapter
            {
                public static float ToStorage(Temperature value) => (float)value.Celsius;
                public static Temperature FromStorage(float value) => new() { Celsius = value };
            }
            """;

        GeneratorRun run = Run(local + Model, package);

        run.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", run.GeneratorDiagnostics));
        run.CompileErrors.ShouldBeEmpty(string.Join("\n", run.CompileErrors));
        run.ShadowSource!.ShouldContain("internal float TempParquetStorage");
    }

    public static IEnumerable<object[]> MalformedAdapters() =>
        new[]
        {
            // No descriptor attribute.
            new object[]
            {
                "public static class Bad { public static double ToStorage(Temperature v) => 0; public static Temperature FromStorage(double v) => new(); }",
                "does not carry",
            },
            // Missing FromStorage.
            [
                "[ParquetTypeAdapter(typeof(Temperature), typeof(double))] public static class Bad { public static double ToStorage(Temperature v) => 0; }",
                "FromStorage",
            ],
            // Reversed conversion.
            [
                "[ParquetTypeAdapter(typeof(Temperature), typeof(double))] public static class Bad { public static Temperature ToStorage(double v) => new(); public static double FromStorage(Temperature v) => 0; }",
                "ToStorage",
            ],
            // Instance methods do not qualify.
            [
                "[ParquetTypeAdapter(typeof(Temperature), typeof(double))] public class Bad { public double ToStorage(Temperature v) => 0; public Temperature FromStorage(double v) => new(); }",
                "ToStorage",
            ],
            // Inaccessible conversion.
            [
                "[ParquetTypeAdapter(typeof(Temperature), typeof(double))] public static class Bad { private static double ToStorage(Temperature v) => 0; public static Temperature FromStorage(double v) => new(); }",
                "ToStorage",
            ],
            // Unsupported contract version.
            [
                "[ParquetTypeAdapter(typeof(Temperature), typeof(double), ContractVersion = 2)] public static class Bad { public static double ToStorage(Temperature v) => 0; public static Temperature FromStorage(double v) => new(); }",
                "contract version 2",
            ],
            // A type mapped to itself is a cycle.
            [
                "[ParquetTypeAdapter(typeof(Temperature), typeof(Temperature))] public static class Bad { public static Temperature ToStorage(Temperature v) => v; public static Temperature FromStorage(Temperature v) => v; }",
                "itself",
            ],
            // Nullable<T> surrogates are refused: the generator owns nulls.
            [
                "[ParquetTypeAdapter(typeof(Temperature), typeof(double?))] public static class Bad { public static double? ToStorage(Temperature v) => 0; public static Temperature FromStorage(double? v) => new(); }",
                "Nullable<T>",
            ],
            // Descriptor names a different source type from the member's.
            [
                "[ParquetTypeAdapter(typeof(string), typeof(int))] public static class Bad { public static int ToStorage(string v) => 0; public static string FromStorage(int v) => \"\"; }",
                "member's type",
            ],
        };

    [Theory]
    [MemberData(nameof(MalformedAdapters))]
    public void MalformedAdaptersReportPARQ016AtTheMember(string adapter, string expected)
    {
        string source =
            Prelude
            + adapter
            + """

                [ParquetSerializable]
                public partial class Row
                {
                    [ParquetAdapter(typeof(Bad))]
                    public Temperature Temp { get; init; } = new();
                }
                """;

        GeneratorRun run = Run(source);

        Diagnostic diagnostic = run.GeneratorDiagnostics.Single();
        diagnostic.Id.ShouldBe(DiagnosticDescriptors.InvalidTypeAdapter.Id);
        diagnostic
            .GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain(expected);
        diagnostic
            .Location.SourceTree!.GetText()
            .ToString(diagnostic.Location.SourceSpan)
            .ShouldBe("Temp");
        run.HasShadowFile.ShouldBeFalse();
    }

    [Fact]
    public void AnUnrepresentableSurrogateReportsPARQ018()
    {
        const string source = """
            using System.Collections.Generic;
            using Parquet.SourceGenerator;

            public sealed class Temperature { public double Celsius { get; init; } }

            [ParquetTypeAdapter(typeof(Temperature), typeof(char))]
            public static class CharAdapter
            {
                public static char ToStorage(Temperature v) => 'c';
                public static Temperature FromStorage(char v) => new();
            }

            [ParquetSerializable]
            public partial class Row
            {
                [ParquetAdapter(typeof(CharAdapter))]
                public Temperature Temp { get; init; } = new();
            }
            """;

        GeneratorRun run = Run(source);

        run.Ids.ShouldBe([DiagnosticDescriptors.UnsupportedAdapterSurrogate.Id]);
    }

    [Fact]
    public void AdaptersDoNotChainThroughASurrogate()
    {
        const string source = """
            using Parquet.SourceGenerator;

            [assembly: ParquetTypeAdapter(typeof(InnerAdapter))]

            public sealed class Temperature { public double Celsius { get; init; } }
            // Only an adapter could represent Inner: it has no public members of its own.
            public sealed class Inner { private int x; public static Inner Of(int v) => new() { x = v }; public int Get() => x; }
            public struct Holder { public Inner Value { get; init; } }

            [ParquetTypeAdapter(typeof(Inner), typeof(int))]
            public static class InnerAdapter
            {
                public static int ToStorage(Inner v) => v.Get();
                public static Inner FromStorage(int v) => Inner.Of(v);
            }

            [ParquetTypeAdapter(typeof(Temperature), typeof(Holder))]
            public static class HolderAdapter
            {
                public static Holder ToStorage(Temperature v) => default;
                public static Temperature FromStorage(Holder v) => new();
            }

            [ParquetSerializable]
            public partial class Row
            {
                [ParquetAdapter(typeof(HolderAdapter))]
                public Temperature Temp { get; init; } = new();
            }
            """;

        GeneratorRun run = Run(source);

        // One hop only (docs/44 §10): inside the surrogate, Inner is planned by the ordinary
        // rules — as an empty group — and its registered adapter is not consulted.
        run.Ids.ShouldContain(DiagnosticDescriptors.NoPropertiesFound.Id);
        run.HasShadowFile.ShouldBeFalse();
    }

    [Fact]
    public void RequiredAndCollidingMembersAreRejectedAtTheMember()
    {
        string source =
            Registered
            + ValidAdapter
            + """

                [ParquetSerializable]
                public partial class Row
                {
                    public required Temperature Temp { get; init; }
                    public Temperature Other { get; init; } = new();
                    public int OtherParquetStorage { get; init; }
                }
                """;

        GeneratorRun run = Run(source);

        run.GeneratorDiagnostics.Count(d => d.Id == DiagnosticDescriptors.InvalidTypeAdapter.Id)
            .ShouldBe(2);
        run.GeneratorDiagnostics.ShouldContain(d =>
            d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("required")
        );
        run.GeneratorDiagnostics.ShouldContain(d =>
            d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("OtherParquetStorage")
        );
    }

    [Fact]
    public void ANestedTargetInsideANonPartialTypeIsRejectedRatherThanEmittingBrokenCode()
    {
        string source =
            Registered
            + ValidAdapter
            + """

                public class Outer
                {
                    [ParquetSerializable]
                    public partial class Row { public Temperature Temp { get; init; } = new(); }
                }
                """;

        GeneratorRun run = Run(source);

        run.GeneratorDiagnostics.ShouldContain(d =>
            d.Id == DiagnosticDescriptors.InvalidTypeAdapter.Id
            && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("'Outer'")
        );
        run.CompileErrors.ShouldBeEmpty(string.Join("\n", run.CompileErrors));
    }

    [Fact]
    public void NestedAndStructTargetsEmitTheirPartialChainAndCompile()
    {
        string source =
            Registered
            + ValidAdapter
            + """

                namespace Deep.Space
                {
                    public static partial class Outer
                    {
                        [ParquetSerializable]
                        public readonly partial record struct Row
                        {
                            public Temperature? Temp { get; init; }
                        }
                    }
                }
                """;

        GeneratorRun run = Run(source);

        run.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", run.GeneratorDiagnostics));
        run.CompileErrors.ShouldBeEmpty(string.Join("\n", run.CompileErrors));
        run.ShadowSource!.ShouldContain("partial class Outer");
        run.ShadowSource!.ShouldContain("partial record struct Row");
        run.ShadowSource!.ShouldContain("init");
    }

    [Fact]
    public void TheLegacyBackendSupportsScalarSurrogatesAndRejectsGroupSurrogates()
    {
        string scalar = Registered + ValidAdapter + Model;
        GeneratorRun legacy = Run(scalar, legacy: true);
        legacy.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", legacy.GeneratorDiagnostics));
        legacy.HasShadowFile.ShouldBeTrue();

        const string group = """
            using Parquet.SourceGenerator;

            public sealed class Temperature { public double Celsius { get; init; } }
            public readonly record struct TemperatureStorage(double Celsius, string Scale);

            [ParquetTypeAdapter(typeof(Temperature), typeof(TemperatureStorage))]
            public static class GroupAdapter
            {
                public static TemperatureStorage ToStorage(Temperature v) => new(v.Celsius, "C");
                public static Temperature FromStorage(TemperatureStorage v) => new() { Celsius = v.Celsius };
            }

            [ParquetSerializable]
            public partial class Row
            {
                [ParquetAdapter(typeof(GroupAdapter))]
                public Temperature Temp { get; init; } = new();
            }
            """;

        Run(group, legacy: true)
            .Ids.ShouldBe([DiagnosticDescriptors.UnsupportedAdapterSurrogate.Id]);

        GeneratorRun modern = Run(group);
        modern.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", modern.GeneratorDiagnostics));
        modern.CompileErrors.ShouldBeEmpty(string.Join("\n", modern.CompileErrors));
    }

    [Fact]
    public void AdapterResolutionIsCachedAsValueEquatableModels()
    {
        string source = Registered + ValidAdapter + Model;

        CSharpCompilation compilation = CreateCompilation(source, []);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ParquetIncrementalGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true
            )
        );
        driver = driver.RunGenerators(compilation);

        // An unrelated edit: a new file that declares nothing the model reads.
        CSharpCompilation edited = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("public static class Unrelated { }")
        );
        driver = driver.RunGenerators(edited);

        GeneratorRunResult result = driver.GetRunResult().Results.Single();
        result
            .TrackedOutputSteps.SelectMany(s => s.Value)
            .SelectMany(s => s.Outputs)
            .ShouldAllBe(o =>
                o.Reason == IncrementalStepRunReason.Cached
                || o.Reason == IncrementalStepRunReason.Unchanged
            );
    }

    private sealed record GeneratorRun(
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        IReadOnlyList<Diagnostic> CompileErrors,
        string? ShadowSource
    )
    {
        public IReadOnlyList<string> Ids => GeneratorDiagnostics.Select(d => d.Id).ToList();

        public bool HasShadowFile => ShadowSource is not null;
    }

    private static GeneratorRun Run(
        string source,
        MetadataReference? extra = null,
        bool legacy = false
    )
    {
        CSharpCompilation compilation = CreateCompilation(source, extra is null ? [] : [extra]);

        IIncrementalGenerator generator = legacy
            ? new LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator()
            : new ParquetIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics
        );

        string? shadow = output
            .SyntaxTrees.FirstOrDefault(t =>
                t.FilePath.EndsWith(".ParquetAdapters.g.cs", StringComparison.Ordinal)
            )
            ?.ToString();

        // Only the generated trees' errors are the generator's responsibility; the legacy backend
        // targets an API this test assembly does not reference, so its serializer is not compiled.
        List<Diagnostic> errors = output
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d =>
                !legacy
                || d.Location.SourceTree?.FilePath.Contains(
                    "ParquetLegacySerializer",
                    StringComparison.Ordinal
                ) != true
            )
            .ToList();

        return new GeneratorRun(diagnostics, errors, shadow);
    }

    private static CSharpCompilation CreateCompilation(string source, MetadataReference[] extra) =>
        CSharpCompilation.Create(
            "AdapterConsumer",
            [CSharpSyntaxTree.ParseText(source)],
            [.. PlatformReferences(), .. extra],
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

    private static PortableExecutableReference CompileLibrary(string source)
    {
        CSharpCompilation library = CSharpCompilation.Create(
            "AdapterPackage" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        using var stream = new MemoryStream();
        var emit = library.Emit(stream);
        emit.Success.ShouldBeTrue(string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static List<MetadataReference> PlatformReferences()
    {
        // Framework assemblies only: the test host's own dependencies include the NodaTime adapter
        // package, whose registrations would otherwise leak into every compilation built here.
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path =>
                string.Equals(
                    Path.GetDirectoryName(path),
                    runtimeDirectory,
                    StringComparison.Ordinal
                )
            )
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(
            MetadataReference.CreateFromFile(typeof(ParquetSerializableAttribute).Assembly.Location)
        );
        references.Add(
            MetadataReference.CreateFromFile(typeof(Parquet.ParquetReader).Assembly.Location)
        );
        return references;
    }
}
