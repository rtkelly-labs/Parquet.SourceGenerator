using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Enforces the declared-subset policy from issue #246: the classic backend remains a stable core
/// compatibility surface while modern-only features are allowed to grow in the v6 backend.
/// </summary>
public sealed class BackendCompatibilityPolicyTests
{
    private static readonly string GoldenFilesDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "..",
        "..",
        "..",
        "GoldenFiles"
    );

    private static readonly HashSet<string> ClassicCoreSignatures = new(StringComparer.Ordinal)
    {
        "static ClassicExtensions.ReadParquetArrayAsync(System.IO.Stream stream, Parquet.SourceGenerator.ParquetSerializerOptions? options = null, System.Threading.CancellationToken cancellationToken = default) -> System.Threading.Tasks.Task<T[]>",
        "static ClassicExtensions.ReadParquetAsync(System.IO.Stream stream, Parquet.SourceGenerator.ParquetSerializerOptions? options = null, System.Threading.CancellationToken cancellationToken = default) -> System.Threading.Tasks.Task<System.Collections.Generic.List<T>>",
        "static ClassicExtensions.WriteParquetAsync(this System.Collections.Generic.IReadOnlyList<T> items, System.IO.Stream stream, Parquet.SourceGenerator.ParquetSerializerOptions? options = null, System.Threading.CancellationToken cancellationToken = default) -> System.Threading.Tasks.Task",
        "static ClassicExtensions.WriteParquetBatchedAsync(this System.Collections.Generic.IEnumerable<T> items, System.IO.Stream stream, Parquet.SourceGenerator.ParquetSerializerOptions? options = null, System.Threading.CancellationToken cancellationToken = default) -> System.Threading.Tasks.Task",
        "static readonly ClassicExtensions.Schema -> Parquet.Schema.ParquetSchema",
    };

    [Fact]
    public void EveryClassicBaselineContainsOnlyTheDeclaredCoreSurface()
    {
        string[] paths = Directory.GetFiles(GoldenFilesDir, "*LegacyExtensions.api.txt");
        paths.Length.ShouldBeGreaterThan(0, "At least one classic API baseline must be checked.");

        foreach (string path in paths)
        {
            string[] signatures = ReadMembers(path).Select(NormalizeClassicSignature).ToArray();

            signatures
                .Except(ClassicCoreSignatures, StringComparer.Ordinal)
                .ShouldBeEmpty(
                    $"The classic backend baseline {Path.GetFileName(path)} contains a signature "
                        + "outside the declared core surface. A new member or overload requires an "
                        + "explicit compatibility-policy decision in docs/reference/compatibility.md."
                );

            foreach (string required in ClassicCoreSignatures)
            {
                signatures.ShouldContain(required, $"Missing from {Path.GetFileName(path)}");
            }
        }
    }

    [Fact]
    public void ClassicRowGroupWriterIsNotPublicSurface()
    {
        // #481: the per-row-group writer is the strategy the flat and batched writes are built
        // from, not a caller intent. It stays emitted (internal) and leaves the core surface.
        foreach (string path in Directory.GetFiles(GoldenFilesDir, "*LegacyExtensions.api.txt"))
        {
            ReadMembers(path)
                .ShouldNotContain(line =>
                    line.Contains(".WriteRowGroupAsync(", StringComparison.Ordinal)
                );
        }
    }

    [Fact]
    public void ModernOnlyOverloadWithAClassicMemberNameIsRejected()
    {
        const string modernStreamingOverload =
            "static SampleDomain.Models.LegacyRecordParquetLegacyExtensions.ReadParquetAsync(System.Collections.Generic.IAsyncEnumerable<LegacyRecord> items) -> System.Threading.Tasks.Task";

        ClassicCoreSignatures.ShouldNotContain(NormalizeClassicSignature(modernStreamingOverload));
    }

    [Fact]
    public void ModernBaselinesRetainTheModernOnlyCapabilities()
    {
        string[] lines = Directory
            .GetFiles(GoldenFilesDir, "*.api.txt")
            .Where(path => !path.EndsWith("LegacyExtensions.api.txt", StringComparison.Ordinal))
            .SelectMany(ReadMembers)
            .ToArray();

        lines.ShouldContain(line => line.Contains("Where(", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("Parallel()", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("ColumnBatch", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("AsAsyncEnumerable(", StringComparison.Ordinal));
    }

    /// <summary>
    /// #480: the builder is the only modern read surface. The flat <c>ReadParquet*Async</c>
    /// methods are gone from every modern baseline, while the classic backend — which has no
    /// builder — keeps its flat reads as its declared subset (checked above).
    /// </summary>
    [Fact]
    public void ModernBaselinesExposeNoFlatReadMethods()
    {
        string[] flatReads =
        [
            "ReadParquetAsync(",
            "ReadParquetArrayAsync(",
            "ReadParquetBatchesAsync(",
            "ReadParquetParallelAsync(",
            "ReadParquetParallelArrayAsync(",
            "ReadParquetStreamAsync(",
        ];

        string[] paths = Directory
            .GetFiles(GoldenFilesDir, "*.api.txt")
            .Where(path => !path.EndsWith("LegacyExtensions.api.txt", StringComparison.Ordinal))
            .ToArray();
        paths.Length.ShouldBeGreaterThan(0);

        foreach (string path in paths)
        {
            foreach (string line in ReadMembers(path))
            {
                flatReads
                    .Where(name => line.Contains("Extensions." + name, StringComparison.Ordinal))
                    .ShouldBeEmpty($"{Path.GetFileName(path)} still exposes a flat read: {line}");
            }
        }
    }

    private static string[] ReadMembers(string path) =>
        global::System
            .IO.File.ReadAllLines(path)
            .Where(line =>
                line.Length > 0
                && !line.StartsWith('#')
                && !line.StartsWith("//", StringComparison.Ordinal)
                && line.Contains(" -> ", StringComparison.Ordinal)
            )
            .ToArray();

    private static string NormalizeClassicSignature(string signature)
    {
        const string NamespacePrefix = "SampleDomain.Models.";
        const string ExtensionSuffix = "ParquetLegacyExtensions";
        const string ExtensionMemberSeparator = ExtensionSuffix + ".";

        int ownerStart = signature.IndexOf(NamespacePrefix, StringComparison.Ordinal);
        if (ownerStart < 0)
        {
            return signature;
        }

        int ownerEnd = signature.IndexOf(
            ExtensionMemberSeparator,
            ownerStart,
            StringComparison.Ordinal
        );
        if (ownerEnd < 0)
        {
            return signature;
        }

        int modelStart = ownerStart + NamespacePrefix.Length;
        string modelName = signature.Substring(modelStart, ownerEnd - modelStart);
        string ownerName = signature.Substring(
            ownerStart,
            ownerEnd + ExtensionSuffix.Length - ownerStart
        );

        return signature
            .Replace(ownerName, "ClassicExtensions", StringComparison.Ordinal)
            .Replace(modelName, "T", StringComparison.Ordinal);
    }
}
