using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Enforces the declared-subset policy from issue #246: the classic backend remains a stable core
/// compatibility surface while modern-only features are allowed to grow in the v6 backend.
/// Checked against the signature-only API of every <see cref="GoldenCorpus"/> model, rendered
/// from the live emitter output.
/// </summary>
public sealed class BackendCompatibilityPolicyTests
{
    private static IEnumerable<GoldenEmission> Classic => GoldenCorpus.All.Where(IsClassic);

    private static IEnumerable<GoldenEmission> Modern => GoldenCorpus.All.Where(e => !IsClassic(e));

    private static bool IsClassic(GoldenEmission emission) =>
        emission.FileName.EndsWith("LegacyExtensions.g.cs", StringComparison.Ordinal);

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
        GoldenEmission[] classic = Classic.ToArray();
        classic.Length.ShouldBeGreaterThan(0, "At least one classic model must be checked.");

        foreach (GoldenEmission emission in classic)
        {
            string[] signatures = ReadMembers(emission).Select(NormalizeClassicSignature).ToArray();

            signatures
                .Except(ClassicCoreSignatures, StringComparer.Ordinal)
                .ShouldBeEmpty(
                    $"The classic backend API of {emission.FileName} contains a signature "
                        + "outside the declared core surface. A new member or overload requires an "
                        + "explicit compatibility-policy decision in docs/14-COMPATIBILITY-MATRIX.md."
                );

            foreach (string required in ClassicCoreSignatures)
            {
                signatures.ShouldContain(required, $"Missing from {emission.FileName}");
            }
        }
    }

    [Fact]
    public void ClassicRowGroupWriterIsNotPublicSurface()
    {
        // #481: the per-row-group writer is the strategy the flat and batched writes are built
        // from, not a caller intent. It stays emitted (internal) and leaves the core surface.
        foreach (GoldenEmission emission in Classic)
        {
            ReadMembers(emission)
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
        string[] lines = Modern.SelectMany(ReadMembers).ToArray();

        lines.ShouldContain(line => line.Contains("Where(", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("Parallel()", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("ColumnBatch", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("AsAsyncEnumerable(", StringComparison.Ordinal));
    }

    /// <summary>
    /// #480: the builder is the only modern read surface. The flat <c>ReadParquet*Async</c>
    /// methods are gone from every modern model's API, while the classic backend — which has no
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

        GoldenEmission[] modern = Modern.ToArray();
        modern.Length.ShouldBeGreaterThan(0);

        foreach (GoldenEmission emission in modern)
        {
            foreach (string line in ReadMembers(emission))
            {
                flatReads
                    .Where(name => line.Contains("Extensions." + name, StringComparison.Ordinal))
                    .ShouldBeEmpty($"{emission.FileName} still exposes a flat read: {line}");
            }
        }
    }

    private static string[] ReadMembers(GoldenEmission emission) =>
        GoldenCorpus
            .ApiOf(emission)
            .Split('\n')
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
