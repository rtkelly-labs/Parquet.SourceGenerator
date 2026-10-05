using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Keeps the diagnostics reference and the release-tracking files in step with the descriptors
/// (#428, #593). PARQ ids are consumer contract: users put them in <c>NoWarn</c> and
/// <c>.editorconfig</c>. The build already fails (RS2000 series) when a descriptor is missing from
/// the release files; nothing failed when compiler-diagnostics.md lacked a section, which is how PARQ012 to PARQ014
/// went undocumented.
/// </summary>
public sealed class DiagnosticDocsAndReleaseTrackingTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void EveryDescriptorHasASectionAndACatalogRowInTheDiagnosticsReference()
    {
        string root = FindRepositoryRoot();
        string[] ids = DescriptorIds(root);
        string doc = Read(root, "docs", "reference", "compiler-diagnostics.md");

        ids.Length.ShouldBeGreaterThanOrEqualTo(16, "the descriptor scan examined too few ids");
        foreach (string id in ids)
        {
            doc.ShouldContain(
                $"### {id}:",
                customMessage: $"compiler-diagnostics.md has no section for {id}"
            );
            doc.ShouldContain(
                $"| **[`{id}`](#",
                customMessage: $"compiler-diagnostics.md catalog has no row for {id}"
            );
        }

        // And nothing is documented that no longer exists.
        IEnumerable<string> documented = Regex
            .Matches(doc, @"^### (PARQ\d{3}):", RegexOptions.Multiline, RegexTimeout)
            .Select(m => m.Groups[1].Value);
        documented.Except(ids).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Parquet.SourceGenerator")]
    [InlineData("Parquet.SourceGenerator.Legacy")]
    public void EveryDescriptorIsInExactlyOneReleaseTrackingFileOfEachGenerator(string project)
    {
        string root = FindRepositoryRoot();
        string[] ids = DescriptorIds(root);
        string directory = Path.Combine(root, "src", project);

        var listed = new List<string>();
        foreach (
            string file in new[] { "AnalyzerReleases.Shipped.md", "AnalyzerReleases.Unshipped.md" }
        )
        {
            listed.AddRange(
                Regex
                    .Matches(
                        IOFile.ReadAllText(Path.Combine(directory, file)),
                        @"^(PARQ\d{3}) \|",
                        RegexOptions.Multiline,
                        RegexTimeout
                    )
                    .Select(m => m.Groups[1].Value)
            );
        }

        listed
            .OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(ids.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void TheDocumentsThatCitedARangeNoLongerCiteOneUpToPARQ099()
    {
        // docs 01 and 03 said `PARQ001` - `PARQ099`; the code defines far fewer ids. Only these
        // files are scanned: other documents may legitimately quote the phrase when describing it.
        string root = FindRepositoryRoot();
        string[] files =
        [
            Path.Combine(root, "docs", "vision.md"),
            Path.Combine(root, "docs", "architecture", "overview.md"),
            Path.Combine(root, "docs", "architecture", "roslyn-pipeline.md"),
            Path.Combine(root, "docs", "reference", "compiler-diagnostics.md"),
            Path.Combine(root, "README.md"),
            Path.Combine(root, "PACKAGE_README.md"),
        ];

        string[] offenders = files
            .Where(f => IOFile.ReadAllText(f).Contains("PARQ099", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToArray();
        offenders.ShouldBeEmpty();
    }

    private static string[] DescriptorIds(string root) =>
        Regex
            .Matches(
                Read(
                    root,
                    "src",
                    "Parquet.SourceGenerator",
                    "Diagnostics",
                    "DiagnosticDescriptors.cs"
                ),
                @"id:\s*""(PARQ\d{3})""",
                RegexOptions.CultureInvariant,
                RegexTimeout
            )
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    private static string Read(string root, params string[] segments) =>
        IOFile
            .ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()))
            .Replace("\r\n", "\n");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (
                IOFile.Exists(
                    Path.Combine(directory.FullName, "docs", "reference", "compiler-diagnostics.md")
                ) || IOFile.Exists(Path.Combine(directory.FullName, "Parquet.SourceGenerator.slnx"))
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
