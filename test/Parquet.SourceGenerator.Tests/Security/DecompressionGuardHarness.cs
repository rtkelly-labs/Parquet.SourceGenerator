using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Reaches the <see cref="ParquetDecompressionGuardStream"/> from every execution flavour.
/// </summary>
/// <remarks>
/// Flavours exercised:
/// <list type="bullet">
/// <item><c>modern</c> is the compiled type in the Runtime assembly.</item>
/// <item><c>modern-csharp7</c> is the guard compiled at C# 7.3 with no target framework symbols (net472 consumer).</item>
/// <item><c>legacy-csharp7</c> is the legacy equivalent.</item>
/// <item><c>legacy-csharp7-checked</c> is compiled with arithmetic overflow checking enabled.</item>
/// </list>
/// </remarks>
internal static class DecompressionGuardHarness
{
    public const string Modern = "modern";
    public const string ModernCSharp7 = "modern-csharp7";
    public const string LegacyCSharp7 = "legacy-csharp7";
    public const string LegacyChecked = "legacy-csharp7-checked";

    private static readonly Dictionary<string, Type> Types = new();
    private static readonly object Gate = new();

    public static IEnumerable<object[]> Flavours() =>
        new[] { Modern, ModernCSharp7, LegacyCSharp7, LegacyChecked }.Select(f =>
            new object[] { f }
        );

    public sealed class Guard : IDisposable
    {
        private readonly Stream _stream;
        private readonly MethodInfo _activate;
        private readonly Stream? _ownedInner;

        public Guard(Stream stream, MethodInfo activate, Stream? ownedInner)
        {
            _stream = stream;
            _activate = activate;
            _ownedInner = ownedInner;
        }

        public Stream Stream => _stream;

        public void Activate() => _activate.Invoke(_stream, null);

        public void Dispose()
        {
            _stream.Dispose();
            _ownedInner?.Dispose();
        }
    }

    public static Guard Create(
        string flavour,
        Stream inner,
        int maxPageSize = 67_108_864,
        int maxExpansionRatio = 1000,
        int maxRowGroupCount = int.MaxValue,
        bool ownsInner = false
    )
    {
        Type type = GuardType(flavour);
        var stream = (Stream)
            Activator.CreateInstance(
                type,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                args: new object[] { inner, maxPageSize, maxExpansionRatio, maxRowGroupCount },
                culture: null
            )!;
        MethodInfo activate = type.GetMethod(
            "Activate",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        )!;
        return new Guard(stream, activate, ownsInner ? inner : null);
    }

    private static Type GuardType(string flavour)
    {
        lock (Gate)
        {
            if (!Types.TryGetValue(flavour, out Type? type))
            {
                type = flavour switch
                {
                    Modern => typeof(ParquetDecompressionGuardStream),
                    ModernCSharp7 => CompileGuard(checkOverflow: false),
                    LegacyCSharp7 => CompileGuard(checkOverflow: false),
                    LegacyChecked => CompileGuard(checkOverflow: true),
                    _ => throw new ArgumentOutOfRangeException(nameof(flavour), flavour, null),
                };
                Types[flavour] = type;
            }

            return type;
        }
    }

    private static Type CompileGuard(bool checkOverflow)
    {
        string guardSourcePath = Path.Combine(
            FindRepoRoot(),
            "src",
            "Parquet.SourceGenerator.Runtime",
            "ParquetDecompressionGuardStream.cs"
        );
        string source = IOFile.ReadAllText(guardSourcePath);

        CSharpCompilation compilation = CSharpCompilation.Create(
            "GuardUnderTest_" + Guid.NewGuid().ToString("N"),
            new[]
            {
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)),
            },
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                checkOverflow: checkOverflow
            )
        );

        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        if (!result.Success)
        {
            string errors = string.Join(
                Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            );
            throw new InvalidOperationException(
                "The guard does not compile as C# 7.3 without target framework symbols:"
                    + Environment.NewLine
                    + errors
            );
        }

        image.Position = 0;
        Assembly assembly = Assembly.Load(image.ToArray());
        return assembly.GetType("Parquet.SourceGenerator.ParquetDecompressionGuardStream")!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (IOFile.Exists(Path.Combine(dir.FullName, "Parquet.SourceGenerator.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find repository root containing Parquet.SourceGenerator.slnx"
        );
    }
}
