using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Backend-specific surface claims, checked against the signature-only API of every
/// <see cref="GoldenCorpus"/> model, rendered from the live emitter output.
/// </summary>
/// <remarks>
/// The declared-subset assertion from issue #246 (the classic surface is exactly a fixed core) was
/// retired by #493: #490 replaced that policy with parity, and every difference between the two
/// backends is now held by <see cref="BackendParityGateTests"/> against
/// <c>src/api/legacy-parity-allowlist.txt</c>, which fails on any unlisted legacy member as the
/// old assertion did, and also on any modern member the legacy backend lacks without an entry.
/// </remarks>
public sealed class BackendCompatibilityPolicyTests
{
    private static IEnumerable<GoldenEmission> Classic => GoldenCorpus.All.Where(IsClassic);

    private static IEnumerable<GoldenEmission> Modern => GoldenCorpus.All.Where(e => !IsClassic(e));

    private static bool IsClassic(GoldenEmission emission) =>
        emission.FileName.EndsWith("LegacyExtensions.g.cs", StringComparison.Ordinal);

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
    public void ModernBaselinesRetainTheModernOnlyCapabilities()
    {
        string[] lines = Modern.SelectMany(ReadMembers).ToArray();

        lines.ShouldContain(line => line.Contains("Where(", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("Parallel()", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("AsBatches(", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("AsAsyncEnumerable(", StringComparison.Ordinal));
    }

    /// <summary>
    /// #480: the builder is the only modern read surface. The flat <c>ReadParquet*Async</c>
    /// methods are gone from every modern model's API. The classic backend, which has no builder
    /// yet, keeps its flat reads as temporary legacy-only parity allowlist entries (#494).
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
}
