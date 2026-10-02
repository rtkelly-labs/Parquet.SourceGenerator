extern alias LegacyGenerator;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using LegacyModels = LegacyGenerator::Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// Reaches the emitted <c>DecompressionGuardStream</c> from every code path that carries it. The type
/// is a private nested class of each generated extension class, so it cannot be named directly.
/// </summary>
/// <remarks>
/// Three flavours are exercised so a fix cannot land in one backend only:
/// <list type="bullet">
/// <item><c>modern</c> is the type compiled into this test assembly by the real generator.</item>
/// <item><c>modern-csharp7</c> is the modern emitter's guard re-compiled at C# 7.3 with no target
/// framework symbols, which is what a net472 consumer sees (no Span overloads, no newer syntax).</item>
/// <item><c>legacy-csharp7</c> is the legacy emitter's guard compiled the same way.</item>
/// <item><c>legacy-csharp7-checked</c> is that guard compiled with overflow checking on, as a
/// consumer project with <c>CheckForOverflowUnderflow</c> would compile it: the guard must not
/// depend on arithmetic wrapping silently.</item>
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
        bool ownsInner = false
    )
    {
        Type type = GuardType(flavour);
        var stream = (Stream)
            Activator.CreateInstance(
                type,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                args: new object[] { inner, maxPageSize, maxExpansionRatio },
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
                    Modern => typeof(BufferModel)
                        .Assembly.GetTypes()
                        .First(t => t.Name == "DecompressionGuardStream"),
                    ModernCSharp7 => CompileGuard(EmitModern(), checkOverflow: false),
                    LegacyCSharp7 => CompileGuard(EmitLegacy(), checkOverflow: false),
                    LegacyChecked => CompileGuard(EmitLegacy(), checkOverflow: true),
                    _ => throw new ArgumentOutOfRangeException(nameof(flavour), flavour, null),
                };
                Types[flavour] = type;
            }

            return type;
        }
    }

    private static string EmitModern() =>
        CodeEmitter.EmitSource(
            new TargetClassModel(
                Namespace: "TestNamespace",
                ClassName: "TestEntity",
                Properties: new EquatableArray<PropertyModel>(Array.Empty<PropertyModel>())
            )
        );

    private static string EmitLegacy() =>
        LegacyGenerator::Parquet.SourceGenerator.Legacy.Emitter.LegacyCodeEmitter.EmitSource(
            new LegacyModels.TargetClassModel(
                "TestNamespace",
                "TestEntity",
                new LegacyModels.EquatableArray<LegacyModels.PropertyModel>(
                    Array.Empty<LegacyModels.PropertyModel>()
                )
            )
        );

    /// <summary>
    /// Cuts the guard class out of an emitted file and compiles it on its own at C# 7.3 with no
    /// preprocessor symbols. The guard depends on nothing but the base class library.
    /// </summary>
    private static Type CompileGuard(string emitted, bool checkOverflow)
    {
        string guard = ExtractClass(emitted, "private sealed class DecompressionGuardStream");
        string source =
            "namespace GuardUnderTest { internal static partial class Holder {\n" + guard + "\n} }";

        CSharpCompilation compilation = CSharpCompilation.Create(
            "GuardUnderTest_" + Guid.NewGuid().ToString("N"),
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(LanguageVersion.CSharp7_3)
                ),
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
                "The emitted guard does not compile as C# 7.3 without target framework symbols:"
                    + Environment.NewLine
                    + errors
            );
        }

        Assembly assembly = Assembly.Load(image.ToArray());
        return assembly
            .GetType("GuardUnderTest.Holder")!
            .GetNestedType("DecompressionGuardStream", BindingFlags.NonPublic)!;
    }

    private static string ExtractClass(string emitted, string declaration)
    {
        int start = emitted.IndexOf(declaration, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("Emitted source has no " + declaration);
        }

        int depth = 0;
        for (int i = emitted.IndexOf('{', start); i < emitted.Length; i++)
        {
            if (emitted[i] == '{')
            {
                depth++;
            }
            else if (emitted[i] == '}' && --depth == 0)
            {
                return emitted.Substring(start, i - start + 1);
            }
        }

        throw new InvalidOperationException("Unbalanced braces in " + declaration);
    }
}
