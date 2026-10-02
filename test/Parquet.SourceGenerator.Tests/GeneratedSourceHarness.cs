using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Runs the real generator over a source string, compiles the result against the real runtime
/// reference set (plus Parquet.Net and Apache.Arrow, so the Arrow bridge is emitted), and can load
/// the emitted assembly. Used where a hostile or unusual model must not be declared in the test
/// project itself, because the whole project would stop building if the emitter mishandled it.
/// </summary>
internal static class GeneratedSourceHarness
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpaJoined)
        {
            throw new InvalidOperationException("TPA unavailable");
        }

        return tpaJoined
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase))
            .Where(p =>
            {
                string file = Path.GetFileName(p);
                return file.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                    || file.StartsWith("Microsoft.Win32", StringComparison.OrdinalIgnoreCase)
                    || file
                        is "netstandard.dll"
                            or "mscorlib.dll"
                            or "Parquet.dll"
                            or "Apache.Arrow.dll"
                            or "Parquet.SourceGenerator.Attributes.dll";
            })
            .Distinct(StringComparer.Ordinal)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    });

    /// <summary>
    /// The compilation after the generator ran, with XML doc comments parsed and diagnosed so a
    /// malformed doc comment in emitted code (CS1570) is reported rather than ignored.
    /// </summary>
    public static (Compilation Output, ImmutableArray<Diagnostic> GeneratorDiagnostics) Generate(
        string source
    )
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Latest,
            DocumentationMode.Diagnose
        );
        CSharpCompilation compilation = CSharpCompilation.Create(
            "GeneratedSourceHarnessAssembly",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ParquetIncrementalGenerator().AsSourceGenerator() },
            parseOptions: parseOptions
        );
        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics
        );
        return (output, diagnostics);
    }

    /// <summary>Errors, and any XML doc warning, in the compiled output.</summary>
    public static IReadOnlyList<string> CompileProblems(Compilation output) =>
        output
            .GetDiagnostics()
            .Where(d =>
                d.Severity == DiagnosticSeverity.Error
                || d.Id is "CS1570" or "CS1571" or "CS1572" or "CS1573" or "CS1587"
            )
            .Select(d => d.ToString())
            .ToList();

    /// <summary>Emits and loads the compiled output into a collectible context.</summary>
    public static Assembly Load(Compilation output)
    {
        using var stream = new MemoryStream();
        var result = output.Emit(stream);
        result.Success.ShouldBeTrue(string.Join("\n", result.Diagnostics));
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }
}
