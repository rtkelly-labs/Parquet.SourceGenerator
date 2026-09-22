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
using Xunit.Abstractions;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The evidence behind docs/46-SERIALIZATION-SHAPES.md: every row of that document's support
/// tables is one case here, run through the real generator and — when accepted — through the
/// C# compiler. A change that widens or narrows what the generator accepts fails this test until
/// the document (and the expectation here) is updated with it.
/// </summary>
public sealed class SerializationShapeMatrixTests(ITestOutputHelper output)
{
    /// <summary>Types every case can use: an adapted scalar, adapted groups at three depths.</summary>
    private const string Prelude = """
        using System;
        using System.Collections.Generic;
        using Parquet.SourceGenerator;

        [assembly: ParquetTypeAdapter(typeof(CelsiusAdapter))]
        [assembly: ParquetTypeAdapter(typeof(SpanAdapter))]
        [assembly: ParquetTypeAdapter(typeof(WindowAdapter))]
        [assembly: ParquetTypeAdapter(typeof(DeepAdapter))]

        // Scalar surrogate: one column.
        public readonly record struct Celsius(double Value);
        [ParquetTypeAdapter(typeof(Celsius), typeof(double))]
        public static class CelsiusAdapter
        {
            public static double ToStorage(Celsius v) => v.Value;
            public static Celsius FromStorage(double v) => new(v);
        }

        // Group surrogate of leaves (like NodaTime Instant).
        public readonly record struct Span(long Days, long Nanos);
        public readonly record struct SpanStorage(long Days, long Nanos);
        [ParquetTypeAdapter(typeof(Span), typeof(SpanStorage))]
        public static class SpanAdapter
        {
            public static SpanStorage ToStorage(Span v) => new(v.Days, v.Nanos);
            public static Span FromStorage(SpanStorage v) => new(v.Days, v.Nanos);
        }

        // Group surrogate containing a group (like NodaTime ZonedDateTime).
        public readonly record struct Window(Span Start, string Zone);
        public readonly record struct WindowStorage(SpanStorage Start, string Zone);
        [ParquetTypeAdapter(typeof(Window), typeof(WindowStorage))]
        public static class WindowAdapter
        {
            public static WindowStorage ToStorage(Window v) => new(new(v.Start.Days, v.Start.Nanos), v.Zone);
            public static Window FromStorage(WindowStorage v) => new(new(v.Start.Days, v.Start.Nanos), v.Zone);
        }

        // Group surrogate nesting two groups deep.
        public readonly record struct Deep(Window W);
        public readonly record struct DeepStorage(WindowStorage W);
        [ParquetTypeAdapter(typeof(Deep), typeof(DeepStorage))]
        public static class DeepAdapter
        {
            public static DeepStorage ToStorage(Deep v) => new(WindowAdapter.ToStorage(v.W));
            public static Deep FromStorage(DeepStorage v) => new(WindowAdapter.FromStorage(v.W));
        }

        [ParquetSerializable] public partial record Leafy { public int A { get; init; } public string? B { get; init; } }
        [ParquetSerializable] public partial struct Point { public int X { get; init; } public int Y { get; init; } }
        [ParquetSerializable] public partial record WithGroup { public int A { get; init; } public Leafy? Inner { get; init; } }
        [ParquetSerializable] public partial record WithGroupOfGroup { public WithGroup? Inner { get; init; } }
        [ParquetSerializable] public partial record WithAdaptedScalar { public Celsius T { get; init; } public Celsius? U { get; init; } }
        [ParquetSerializable] public partial record WithAdaptedGroup { public Span S { get; init; } public Span? MaybeS { get; init; } }
        [ParquetSerializable] public partial record WithAdaptedDeep { public Window W { get; init; } }
        [ParquetSerializable] public partial record WithList { public List<int> Items { get; init; } = new(); }

        // A chain of nested types for the depth limit.
        [ParquetSerializable] public partial record L6 { public int V { get; init; } }
        [ParquetSerializable] public partial record L5 { public L6? N { get; init; } }
        [ParquetSerializable] public partial record L4 { public L5? N { get; init; } }
        [ParquetSerializable] public partial record L3 { public L4? N { get; init; } }
        [ParquetSerializable] public partial record L2 { public L3? N { get; init; } }
        [ParquetSerializable] public partial record L1 { public L2? N { get; init; } }
        [ParquetSerializable] public partial record L0 { public L1? N { get; init; } }

        // The same limit reached through adapter groups: A4 holds a Window (2 group levels).
        [ParquetSerializable] public partial record A4 { public Window W { get; init; } }
        [ParquetSerializable] public partial record A3 { public A4? N { get; init; } }
        [ParquetSerializable] public partial record A2 { public A3? N { get; init; } }
        [ParquetSerializable] public partial record A1 { public A2? N { get; init; } }
        [ParquetSerializable] public partial record A0 { public A1? N { get; init; } }

        """;

    /// <summary>(case id, member declarations of the model under test, expected diagnostic or null).</summary>
    public static IEnumerable<object?[]> Cases() =>
        new (string Id, string Members, string? Expected)[]
        {
            // --- Flat leaves -------------------------------------------------------------
            (
                "flat/primitives",
                "public int A { get; init; } public string? B { get; init; } public double? C { get; init; }",
                null
            ),
            (
                "flat/temporal",
                "public DateTime A { get; init; } public DateOnly B { get; init; } public TimeOnly? C { get; init; } public TimeSpan D { get; init; }",
                null
            ),
            (
                "flat/other",
                "public decimal A { get; init; } public Guid B { get; init; } public byte[]? C { get; init; } public DayOfWeek D { get; init; }",
                null
            ),
            ("flat/unsupported-char", "public char A { get; init; }", "PARQ006"),
            (
                "flat/unsupported-datetimeoffset",
                "public DateTimeOffset A { get; init; }",
                "PARQ006"
            ),
            // --- Nested types (groups) -----------------------------------------------------
            ("group/reference", "public Leafy Inner { get; init; } = new();", null),
            ("group/nullable-reference", "public Leafy? Inner { get; init; }", null),
            ("group/value", "public Point P { get; init; }", null),
            ("group/nullable-value", "public Point? P { get; init; }", null),
            ("group/group-in-group", "public WithGroupOfGroup Inner { get; init; } = new();", null),
            ("group/depth-6", "public L1? N { get; init; }", null),
            ("group/depth-7", "public L0? N { get; init; }", "PARQ013"),
            ("group/unattributed-class", "public Uri? Link { get; init; }", "PARQ006"),
            ("group/list-inside-group", "public WithList Inner { get; init; } = new();", "PARQ006"),
            // --- Lists ---------------------------------------------------------------------
            (
                "list/leaves",
                "public List<int> A { get; init; } = new(); public string?[]? B { get; init; } public IReadOnlyList<DateTime> C { get; init; } = new List<DateTime>(); public IEnumerable<double?> D { get; init; } = new double?[0];",
                null
            ),
            (
                "list/reference-elements",
                "public List<Leafy> A { get; init; } = new(); public Leafy?[]? B { get; init; }",
                null
            ),
            (
                "list/value-elements",
                "public List<Point> A { get; init; } = new(); public Point?[] B { get; init; } = new Point?[0];",
                null
            ),
            ("list/element-with-group", "public List<WithGroup> A { get; init; } = new();", null),
            (
                "list/element-with-group-of-group",
                "public List<WithGroupOfGroup> A { get; init; } = new();",
                "PARQ006"
            ),
            ("list/list-of-lists", "public List<List<int>> A { get; init; } = new();", "PARQ006"),
            (
                "list/dictionary",
                "public Dictionary<string, int> A { get; init; } = new();",
                "PARQ006"
            ),
            ("list/hashset", "public HashSet<int> A { get; init; } = new();", "PARQ006"),
            // --- Adapted members -------------------------------------------------------------
            (
                "adapter/root-scalar",
                "public Celsius T { get; init; } public Celsius? U { get; init; }",
                null
            ),
            (
                "adapter/root-group",
                "public Span S { get; init; } public Span? MaybeS { get; init; }",
                null
            ),
            ("adapter/root-group-of-group", "public Window W { get; init; }", null),
            (
                "adapter/in-nested-type",
                "public WithAdaptedScalar A { get; init; } = new(); public WithAdaptedGroup? B { get; init; } public WithAdaptedDeep C { get; init; } = new();",
                null
            ),
            (
                "adapter/list-scalar",
                "public List<Celsius> A { get; init; } = new(); public Celsius?[]? B { get; init; }",
                null
            ),
            (
                "adapter/list-group",
                "public List<Span> A { get; init; } = new(); public Span?[] B { get; init; } = new Span?[0];",
                null
            ),
            ("adapter/list-group-of-group", "public List<Window> A { get; init; } = new();", null),
            ("adapter/list-deeper", "public List<Deep> A { get; init; } = new();", "PARQ018"),
            (
                "adapter/list-element-with-adapted-scalar",
                "public List<WithAdaptedScalar> A { get; init; } = new();",
                null
            ),
            (
                "adapter/list-element-with-adapted-group",
                "public List<WithAdaptedGroup> A { get; init; } = new();",
                null
            ),
            (
                "adapter/list-element-with-adapted-group-of-group",
                "public List<WithAdaptedDeep> A { get; init; } = new();",
                "PARQ006"
            ),
            ("adapter/root-deeper", "public Deep D { get; init; }", null),
            ("adapter/depth-6", "public A1? N { get; init; }", null),
            ("adapter/depth-7", "public A0? N { get; init; }", "PARQ013"),
            (
                "adapter/dictionary-value",
                "public Dictionary<string, Celsius> A { get; init; } = new();",
                "PARQ006"
            ),
        }.Select(c => new object?[] { c.Id, c.Members, c.Expected });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShapeIsAcceptedOrRejectedAsDocumented(string id, string members, string? expected)
    {
        Result result = Run(
            $"[ParquetSerializable] public partial record Subject {{ {members} }}\n"
        );
        IReadOnlyList<string> subject = result.SubjectDiagnostics;
        output.WriteLine(
            $"{id}: diagnostics [{string.Join(", ", subject)}], compile errors {result.CompileErrors.Count}"
        );

        if (expected is null)
        {
            subject.ShouldBeEmpty(id);
            result.CompileErrors.ShouldBeEmpty(string.Join("\n", result.CompileErrors));
            result.SubjectEmitted.ShouldBeTrue(id);
        }
        else
        {
            subject.ShouldContain(expected, id);
            result.SubjectEmitted.ShouldBeFalse(id);
            // A rejection is a diagnostic, never generated code that fails to compile.
            result.CompileErrors.ShouldBeEmpty(string.Join("\n", result.CompileErrors));
        }
    }

    [Fact]
    public void FeatureLevelOneIsFlatOnly()
    {
        string level1 = Prelude.Replace(
            "[assembly: ParquetTypeAdapter(typeof(CelsiusAdapter))]",
            "[assembly: ParquetGeneratorOptions(FeatureLevel = ParquetGeneratorFeatureLevel.Level1Flat)]\n[assembly: ParquetTypeAdapter(typeof(CelsiusAdapter))]",
            StringComparison.Ordinal
        );
        Run(Subject("public Leafy Inner { get; init; } = new();"), prelude: level1)
            .SubjectDiagnostics.ShouldContain("PARQ006");
        Run(Subject("public List<int> A { get; init; } = new();"), prelude: level1)
            .SubjectDiagnostics.ShouldContain("PARQ006");
        Run(Subject("public Span S { get; init; }"), prelude: level1)
            .SubjectDiagnostics.ShouldContain("PARQ018");
        Result scalar = Run(Subject("public Celsius T { get; init; }"), prelude: level1);
        scalar.SubjectDiagnostics.ShouldBeEmpty();
        scalar.SubjectEmitted.ShouldBeTrue();
    }

    private static string Subject(string members) =>
        $"[ParquetSerializable] public partial record Subject {{ {members} }}\n";

    [Fact]
    public void TheLegacyBackendIsFlatWithScalarAdapters()
    {
        Run(Subject("public Leafy Inner { get; init; } = new();"), legacy: true)
            .SubjectDiagnostics.ShouldContain("PARQ006");
        Run(Subject("public List<int> A { get; init; } = new();"), legacy: true)
            .SubjectDiagnostics.ShouldContain("PARQ006");
        Run(Subject("public Span S { get; init; }"), legacy: true)
            .SubjectDiagnostics.ShouldContain("PARQ018");
        Run(Subject("public Celsius T { get; init; }"), legacy: true)
            .SubjectDiagnostics.ShouldBeEmpty();
    }

    private static readonly Dictionary<(string, bool), HashSet<string>> Baselines = new();

    private static HashSet<string> Baseline(string prelude, bool legacy)
    {
        lock (Baselines)
        {
            if (!Baselines.TryGetValue((prelude, legacy), out HashSet<string>? found))
            {
                found =
                [
                    .. RunDiagnostics(prelude, legacy)
                        .Select(d => $"{d.Id}@{d.Location.SourceSpan}"),
                ];
                Baselines[(prelude, legacy)] = found;
            }

            return found;
        }
    }

    private sealed record Result(
        IReadOnlyList<string> SubjectDiagnostics,
        IReadOnlyList<Diagnostic> CompileErrors,
        bool SubjectEmitted
    );

    private static Result Run(string subjectSource, bool legacy = false, string? prelude = null)
    {
        prelude ??= Prelude;
        ImmutableArray<Diagnostic> diagnostics = RunDiagnostics(
            prelude + subjectSource,
            legacy,
            out Compilation outputCompilation
        );

        // What the subject adds: diagnostics beyond those the prelude's own types produce on
        // their own. A rejection deep in a nested type is reported at that type's member, which
        // lives in the prelude, so filtering by position would miss it.
        HashSet<string> baseline = Baseline(prelude, legacy);
        List<string> subject = diagnostics
            .Select(d => $"{d.Id}@{d.Location.SourceSpan}")
            .Where(key => !baseline.Contains(key))
            .Select(key => key.Substring(0, key.IndexOf('@', StringComparison.Ordinal)))
            .Distinct()
            .ToList();

        bool emitted = outputCompilation.SyntaxTrees.Any(t =>
            t.FilePath.EndsWith(
                legacy ? "Subject.ParquetLegacySerializer.g.cs" : "Subject.ParquetSerializer.g.cs",
                StringComparison.Ordinal
            )
        );

        // The legacy backend targets the Parquet.Net 4.x/5.x API this assembly does not
        // reference, so only the modern backend's output is compiled.
        List<Diagnostic> errors = legacy
            ? []
            : outputCompilation
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

        return new Result(subject, errors, emitted);
    }

    private static ImmutableArray<Diagnostic> RunDiagnostics(string source, bool legacy) =>
        RunDiagnostics(source, legacy, out _);

    private static ImmutableArray<Diagnostic> RunDiagnostics(
        string source,
        bool legacy,
        out Compilation outputCompilation
    )
    {
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p =>
                string.Equals(Path.GetDirectoryName(p), runtimeDirectory, StringComparison.Ordinal)
            )
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        references.Add(
            MetadataReference.CreateFromFile(typeof(ParquetSerializableAttribute).Assembly.Location)
        );
        references.Add(
            MetadataReference.CreateFromFile(typeof(Parquet.ParquetReader).Assembly.Location)
        );

        CSharpCompilation compilation = CSharpCompilation.Create(
            "ShapeMatrix",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        IIncrementalGenerator generator = legacy
            ? new LegacyGenerator::Parquet.SourceGenerator.Legacy.ParquetLegacyIncrementalGenerator()
            : new ParquetIncrementalGenerator();
        CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(
                compilation,
                out outputCompilation,
                out ImmutableArray<Diagnostic> diagnostics
            );
        return diagnostics;
    }
}
