using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Parquet.SourceGenerator.Tools;

public static class Program
{
    private const string StartMarker = "<!-- BENCHMARK_TABLE_START -->";
    private const string EndMarker = "<!-- BENCHMARK_TABLE_END -->";

    // Encoding.UTF8 emits a preamble; the README files on main are BOM-less and the headline
    // PR must touch only the marker-delimited table (#195).
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static int Main(string[] args)
    {
        string resultsDir =
            args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
                ? args[0]
                : "BenchmarkDotNet.Artifacts/results";
        bool updateReadme = args.Contains("--update-readme", StringComparer.Ordinal);
        string? outputPath = args.FirstOrDefault(a =>
            a != resultsDir && !a.StartsWith("--", StringComparison.Ordinal)
        );

        // The regression check owns the missing-directory case (#408): a gate that finds no
        // results has examined nothing, which is a failure, not the "nothing to summarise" exit
        // below.
        string? baselinePath = OptionValue(args, "--baseline");
        if (baselinePath is not null)
        {
            return RunRegressionCheck(resultsDir, baselinePath, args);
        }

        if (!Directory.Exists(resultsDir))
        {
            Console.WriteLine($"Results directory '{resultsDir}' does not exist.");
            return 0;
        }

        string headlineTable = BuildHeadlineTable(resultsDir);

        if (updateReadme && !string.IsNullOrEmpty(headlineTable))
        {
            string combinedReadmeTable = headlineTable;
            string realWorldTable = BuildRealWorldDatasetTable(resultsDir);
            if (!string.IsNullOrEmpty(realWorldTable))
            {
                combinedReadmeTable = $"{headlineTable}\n\n{realWorldTable}";
            }

            if (ReadmeChangeIsSignificant(combinedReadmeTable, args))
            {
                UpdateReadmeFile("README.md", combinedReadmeTable);
                UpdateReadmeFile("PACKAGE_README.md", combinedReadmeTable);
            }
        }

        if (!string.IsNullOrEmpty(outputPath))
        {
            string fullReport = BuildFullReport(resultsDir, headlineTable);
            File.WriteAllText(outputPath, fullReport, Utf8NoBom);
            Console.WriteLine($"Benchmark summary written to {outputPath}");
        }
        else if (!updateReadme)
        {
            Console.WriteLine(headlineTable);
        }

        return 0;
    }

    /// <summary>
    /// Whether the README table should be rewritten. Without <c>--alloc-threshold</c> or
    /// <c>--time-threshold</c> it always is, which is what a local run wants. The scheduled
    /// workflow passes both so that run-to-run noise on a shared runner does not rewrite the table
    /// and open a pull request (#568).
    /// </summary>
    private static bool ReadmeChangeIsSignificant(string freshTable, string[] args)
    {
        if (
            OptionValue(args, "--alloc-threshold") is null
            && OptionValue(args, "--time-threshold") is null
        )
        {
            return true;
        }

        string committed = ReadmeTable("README.md");
        bool significant = HeadlineNoiseFilter.IsSignificant(
            committed,
            freshTable,
            ParseTolerance(
                args,
                "--alloc-threshold",
                HeadlineNoiseFilter.DefaultAllocationThreshold
            ),
            ParseTolerance(args, "--time-threshold", HeadlineNoiseFilter.DefaultTimeThreshold),
            out string reason
        );

        Console.WriteLine(
            significant
                ? $"Headline table will be rewritten: {reason}"
                : $"Headline table left as committed: {reason}"
        );
        return significant;
    }

    private static string ReadmeTable(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        string content = File.ReadAllText(path, Encoding.UTF8);
        int start = content.IndexOf(StartMarker, StringComparison.Ordinal);
        int end = content.IndexOf(EndMarker, StringComparison.Ordinal);
        return start >= 0 && end > start
            ? content.Substring(start + StartMarker.Length, end - start - StartMarker.Length)
            : string.Empty;
    }

    /// <summary>
    /// Compares a benchmark run against the committed baseline, or refreshes that baseline.
    /// </summary>
    /// <returns>0 when the run is acceptable, 1 when it regressed or examined nothing.</returns>
    private static int RunRegressionCheck(string resultsDir, string baselinePath, string[] args)
    {
        if (!TryReadResults(resultsDir, out IReadOnlyList<BenchmarkMeasurement> current))
        {
            return 1;
        }

        if (current.Count == 0)
        {
            // Not a pass. A filter that matched nothing, a run that crashed before exporting, or
            // a results directory that was never created would otherwise be indistinguishable
            // from a clean result (#408).
            Console.Error.WriteLine(
                $"No benchmark results found in '{resultsDir}'. Nothing to compare."
            );
            return 1;
        }

        string environment = RegressionCheck.ReadEnvironment(resultsDir);

        if (args.Contains("--update-baseline", StringComparer.Ordinal))
        {
            WriteBaselineFile(
                baselinePath,
                current,
                environment,
                OptionValue(args, "--recorded-on") ?? string.Empty
            );
            Console.WriteLine(
                $"Baseline updated with {current.Count} measurement(s): {baselinePath}"
            );
            return 0;
        }

        if (!TryReadBaseline(baselinePath, out IReadOnlyList<BenchmarkMeasurement> baseline))
        {
            return 1;
        }

        if (baseline.Count == 0)
        {
            // A check never writes its own reference (#412). A missing or empty baseline is
            // indistinguishable from "somebody deleted it", and recording the current numbers as
            // the new truth on the same invocation would hide whatever regressed. Recording is
            // either --update-baseline or, for a deliberate first run, --bootstrap.
            if (!args.Contains("--bootstrap", StringComparer.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Baseline '{baselinePath}' is missing or empty. Record one with --update-baseline (or --bootstrap for a first run); a check never writes its own baseline."
                );
                return 1;
            }

            WriteBaselineFile(
                baselinePath,
                current,
                environment,
                OptionValue(args, "--recorded-on") ?? string.Empty
            );
            Console.WriteLine(
                $"Bootstrapped baseline '{baselinePath}' with {current.Count} measurement(s). Commit it, and subsequent runs will be compared against it."
            );
            return 0;
        }

        double allocationTolerance = ParseTolerance(
            args,
            "--alloc-tolerance",
            RegressionCheck.DefaultAllocationTolerance
        );

        // --no-time: the baseline was recorded on other hardware than this run, so a wall-clock
        // comparison is noise by construction. An infinite tolerance never reports one.
        double timeTolerance = args.Contains("--no-time", StringComparer.Ordinal)
            ? double.PositiveInfinity
            : ParseTolerance(args, "--time-tolerance", RegressionCheck.DefaultTimeTolerance);
        bool failOnTime = args.Contains("--fail-on-time", StringComparer.Ordinal);

        IReadOnlyList<BenchmarkComparison> comparisons = RegressionCheck.Compare(
            baseline,
            current,
            allocationTolerance,
            timeTolerance
        );

        string report = RegressionCheck.BuildReport(comparisons);
        Console.WriteLine(report);

        string baselineEnvironment = RegressionCheck.ReadBaselineEnvironment(baselinePath);
        Console.WriteLine($"Baseline recorded on: {OrUnknown(baselineEnvironment)}");
        Console.WriteLine($"This run on: {OrUnknown(environment)}");

        string? reportPath = OptionValue(args, "--report");
        if (reportPath is not null)
        {
            File.WriteAllText(reportPath, report, Encoding.UTF8);
        }

        // Examined-N: a run that shares no benchmark with the baseline (every method renamed, or
        // a filter that skipped them all) compared nothing, and every row is New or NotRun,
        // neither of which fails. Say how many were compared and refuse zero.
        int compared = RegressionCheck.CountCompared(comparisons);
        Console.WriteLine(
            $"{compared} benchmark(s) compared against {baseline.Count} baseline measurement(s)."
        );

        if (compared == 0)
        {
            Console.Error.WriteLine(
                "No benchmark in this run matches the baseline. Nothing was compared."
            );
            return 1;
        }

        if (
            args.Contains("--fail-on-not-run", StringComparer.Ordinal)
            && comparisons.Any(c => c.Kind == RegressionKind.NotRun)
        )
        {
            Console.Error.WriteLine(
                "The baseline holds benchmarks this run did not execute (--fail-on-not-run)."
            );
            return 1;
        }

        if (
            args.Contains("--fail-on-new", StringComparer.Ordinal)
            && comparisons.Any(c => c.Kind == RegressionKind.New)
        )
        {
            Console.Error.WriteLine(
                "This run executed benchmarks the baseline does not hold, so they are not gated (--fail-on-new). Record them with --update-baseline."
            );
            return 1;
        }

        return RegressionCheck.HasFailures(comparisons, failOnTime) ? 1 : 0;
    }

    private static bool TryReadResults(
        string resultsDir,
        out IReadOnlyList<BenchmarkMeasurement> results
    )
    {
        try
        {
            results = RegressionCheck.ReadResults(resultsDir);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(
                $"The benchmark results cannot be read unambiguously: {ex.Message}"
            );
            results = Array.Empty<BenchmarkMeasurement>();
            return false;
        }
    }

    private static bool TryReadBaseline(
        string baselinePath,
        out IReadOnlyList<BenchmarkMeasurement> baseline
    )
    {
        try
        {
            baseline = RegressionCheck.ReadBaseline(baselinePath);
            return true;
        }
        catch (BaselineFormatException ex)
        {
            Console.Error.WriteLine($"Baseline '{baselinePath}' is unusable: {ex.Message}");
            baseline = Array.Empty<BenchmarkMeasurement>();
            return false;
        }
    }

    private static string OrUnknown(string value) =>
        string.IsNullOrEmpty(value) ? "unknown" : value;

    private static void WriteBaselineFile(
        string path,
        IReadOnlyList<BenchmarkMeasurement> measurements,
        string environment,
        string recordedOn
    )
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            RegressionCheck.WriteBaseline(measurements, environment, recordedOn),
            Utf8NoBom
        );
    }

    /// <summary>
    /// Reads a <c>--name value</c> option.
    /// </summary>
    private static string? OptionValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return
            index >= 0
            && index + 1 < args.Length
            && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    private static double ParseTolerance(string[] args, string name, double fallback)
    {
        string? raw = OptionValue(args, name);
        return
            raw is not null
            && double.TryParse(
                raw,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double value
            )
            && value >= 0
            ? value
            : fallback;
    }

    private static string BuildHeadlineTable(string resultsDir)
    {
        var entries = new Dictionary<(string Method, int Count), BenchmarkEntry>();
        string[] csvFiles = Directory.GetFiles(resultsDir, "*-report.csv");

        foreach (string csvFile in csvFiles)
        {
            string[] lines = File.ReadAllLines(csvFile);
            if (lines.Length <= 1)
                continue;

            string[] headers = ParseCsvLine(lines[0]);
            int methodIdx = Array.IndexOf(headers, "Method");
            int countIdx = Array.IndexOf(headers, "Count");
            int meanIdx = Array.IndexOf(headers, "Mean");
            int allocIdx = Array.IndexOf(headers, "Allocated");
            int ratioIdx = Array.IndexOf(headers, "Ratio");
            int allocRatioIdx = Array.IndexOf(headers, "Alloc Ratio");

            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = ParseCsvLine(lines[i]);
                if (parts.Length <= Math.Max(methodIdx, meanIdx))
                    continue;

                string method = methodIdx >= 0 && methodIdx < parts.Length ? parts[methodIdx] : "";
                string countStr = countIdx >= 0 && countIdx < parts.Length ? parts[countIdx] : "0";
                string meanStr = meanIdx >= 0 && meanIdx < parts.Length ? parts[meanIdx] : "";
                string allocStr = allocIdx >= 0 && allocIdx < parts.Length ? parts[allocIdx] : "";
                string ratioStr = ratioIdx >= 0 && ratioIdx < parts.Length ? parts[ratioIdx] : "";
                string allocRatioStr =
                    allocRatioIdx >= 0 && allocRatioIdx < parts.Length ? parts[allocRatioIdx] : "";

                if (string.IsNullOrWhiteSpace(meanStr) || meanStr == "NA")
                    continue;

                _ = int.TryParse(
                    countStr,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int count
                );
                var entry = new BenchmarkEntry(
                    Method: method,
                    Count: count,
                    MeanFormatted: FormatTime(meanStr),
                    MeanNumeric: ParseNumber(meanStr),
                    AllocatedFormatted: FormatMemory(allocStr),
                    RatioNumeric: ParseNumber(ratioStr),
                    AllocRatioNumeric: ParseNumber(allocRatioStr)
                );

                entries[(method, count)] = entry;
            }
        }

        var scenarios = new[]
        {
            new Scenario(
                "File Serialization (Write)",
                "ReflectionParquetSerializerV6Write",
                "SourceGeneratorWriteAsync",
                100_000
            ),
            new Scenario(
                "Streaming Batched Write",
                "ReflectionParquetSerializerV6Write",
                "SourceGeneratorWriteBatchedAsync",
                100_000
            ),
            new Scenario(
                "File Deserialization (Read)",
                "ReflectionParquetSerializerV6Read",
                "SourceGeneratorReadAsync",
                100_000
            ),
            new Scenario(
                "Parallel Deserialization (Read)",
                "ReflectionParquetSerializerV6Read",
                "SourceGeneratorReadParallelBufferAsync",
                100_000
            ),
            new Scenario(
                "Streaming Read (IAsyncEnumerable)",
                "ReflectionParquetSerializerV6Read",
                "SourceGeneratorReadStreamAsync",
                100_000
            ),
            new Scenario(
                "Guid Serialization",
                "ReflectionParquetSerializerGuidWrite",
                "SourceGeneratorGuidWriteAsync",
                100_000
            ),
        };

        var sb = new StringBuilder();
        sb.AppendLine("## ⚡ Performance & Benchmarks");
        sb.AppendLine();
        sb.AppendLine(
            "Zero-reflection C# source generation vs **`ParquetSerializer` v6** reflection baseline:"
        );
        sb.AppendLine();
        sb.AppendLine(
            "| Operation | Scale | Reflection Baseline | Source Generator | Speedup | Memory Reduction |"
        );
        sb.AppendLine("|:--- |:---:|:---:|:---:|:---:|:---:|");

        bool hasRows = false;
        foreach (var s in scenarios)
        {
            int count = s.TargetCount;
            entries.TryGetValue((s.BaselineMethod, count), out var bEntry);
            entries.TryGetValue((s.SgMethod, count), out var sgEntry);

            if (bEntry == null || sgEntry == null)
            {
                foreach (int altCount in new[] { 100_000, 10_000, 1_000 })
                {
                    if (
                        entries.TryGetValue((s.BaselineMethod, altCount), out var bAlt)
                        && entries.TryGetValue((s.SgMethod, altCount), out var sgAlt)
                    )
                    {
                        bEntry = bAlt;
                        sgEntry = sgAlt;
                        count = altCount;
                        break;
                    }
                }
            }

            if (bEntry == null || sgEntry == null)
                continue;

            string bTime = bEntry.MeanFormatted;
            string sgTime = sgEntry.MeanFormatted;
            string bAlloc = bEntry.AllocatedFormatted;
            string sgAlloc = sgEntry.AllocatedFormatted;

            string speedupStr = FormatSpeedup(bEntry, sgEntry);
            string memStr = FormatMemoryReduction(sgEntry);

            string countStr = count.ToString("N0", CultureInfo.InvariantCulture);
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"| **{s.Title}** | {countStr} items | {bTime} ({bAlloc}) | **{sgTime}** (**{sgAlloc}**) | {speedupStr} | {memStr} |"
            );
            hasRows = true;
        }

        if (!hasRows)
            return string.Empty;

        sb.AppendLine();
        sb.AppendLine(
            "> 📌 **Note**: BenchmarkDotNet results captured on GitHub Actions. Detailed multi-scale reports (1K, 10K, 100K, 1M rows) are in [docs/BENCHMARKS.md](https://github.com/rtkelly13/Parquet.SourceGenerator/blob/main/docs/BENCHMARKS.md)."
        );

        return sb.ToString();
    }

    private static string BuildRealWorldDatasetTable(string resultsDir)
    {
        var entries = new Dictionary<string, BenchmarkEntry>(StringComparer.OrdinalIgnoreCase);
        string[] csvFiles = Directory.GetFiles(resultsDir, "*-report.csv");

        foreach (string csvFile in csvFiles)
        {
            string[] lines = File.ReadAllLines(csvFile);
            if (lines.Length <= 1)
                continue;

            string[] headers = ParseCsvLine(lines[0]);
            int methodIdx = Array.IndexOf(headers, "Method");
            int meanIdx = Array.IndexOf(headers, "Mean");
            int allocIdx = Array.IndexOf(headers, "Allocated");
            int ratioIdx = Array.IndexOf(headers, "Ratio");
            int allocRatioIdx = Array.IndexOf(headers, "Alloc Ratio");

            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = ParseCsvLine(lines[i]);
                if (parts.Length <= Math.Max(methodIdx, meanIdx))
                    continue;

                string method = methodIdx >= 0 && methodIdx < parts.Length ? parts[methodIdx] : "";
                string meanStr = meanIdx >= 0 && meanIdx < parts.Length ? parts[meanIdx] : "";
                string allocStr = allocIdx >= 0 && allocIdx < parts.Length ? parts[allocIdx] : "";
                string ratioStr = ratioIdx >= 0 && ratioIdx < parts.Length ? parts[ratioIdx] : "";
                string allocRatioStr =
                    allocRatioIdx >= 0 && allocRatioIdx < parts.Length ? parts[allocRatioIdx] : "";

                if (string.IsNullOrWhiteSpace(meanStr) || meanStr == "NA")
                    continue;

                entries[method] = new BenchmarkEntry(
                    Method: method,
                    Count: 0,
                    MeanFormatted: FormatTime(meanStr),
                    MeanNumeric: ParseNumber(meanStr),
                    AllocatedFormatted: FormatMemory(allocStr),
                    RatioNumeric: ParseNumber(ratioStr),
                    AllocRatioNumeric: ParseNumber(allocRatioStr)
                );
            }
        }

        var readScenarios = new[]
        {
            new RealWorldScenario(
                "TPC-H LineItem Deserialization",
                "60,175 rows",
                "ReflectionParquetSerializerTpchRead",
                "SourceGeneratorTpchReadAsync"
            ),
            new RealWorldScenario(
                "TPC-H LineItem Parallel Deserialization",
                "60,175 rows",
                "ReflectionParquetSerializerTpchRead",
                "SourceGeneratorTpchReadParallelBufferAsync"
            ),
            new RealWorldScenario(
                "TPC-H LineItem Streaming Deserialization",
                "60,175 rows",
                "ReflectionParquetSerializerTpchRead",
                "SourceGeneratorTpchReadStreamAsync"
            ),
            new RealWorldScenario(
                "Adult Census Deserialization (Dictionaries)",
                "32,561 rows",
                "ReflectionParquetSerializerCensusRead",
                "SourceGeneratorCensusReadAsync"
            ),
            new RealWorldScenario(
                "Adult Census Parallel Deserialization",
                "32,561 rows",
                "ReflectionParquetSerializerCensusRead",
                "SourceGeneratorCensusReadParallelBufferAsync"
            ),
            new RealWorldScenario(
                "Adult Census Streaming Deserialization",
                "32,561 rows",
                "ReflectionParquetSerializerCensusRead",
                "SourceGeneratorCensusReadStreamAsync"
            ),
            new RealWorldScenario(
                "Diamonds Deserialization",
                "53,940 rows",
                "ReflectionParquetSerializerDiamondsRead",
                "SourceGeneratorDiamondsReadAsync"
            ),
            new RealWorldScenario(
                "Diamonds Parallel Deserialization",
                "53,940 rows",
                "ReflectionParquetSerializerDiamondsRead",
                "SourceGeneratorDiamondsReadParallelBufferAsync"
            ),
            new RealWorldScenario(
                "Diamonds Streaming Deserialization",
                "53,940 rows",
                "ReflectionParquetSerializerDiamondsRead",
                "SourceGeneratorDiamondsReadStreamAsync"
            ),
        };

        var sb = new StringBuilder();
        bool hasReadRows = false;

        foreach (var s in readScenarios)
        {
            if (
                entries.TryGetValue(s.BaselineMethod, out var bEntry)
                && entries.TryGetValue(s.SgMethod, out var sgEntry)
            )
            {
                if (!hasReadRows)
                {
                    sb.AppendLine("## 🌐 Real-World Provenanced Dataset Benchmarks");
                    sb.AppendLine();
                    sb.AppendLine(
                        "Fixed public datasets tracked under Git LFS with full cryptographic SHA-256 data provenance:"
                    );
                    sb.AppendLine(
                        "- **TPC-H SF 0.01 LineItem**: 60,175 rows, 16 columns (decimals, dates, strings, dictionary encoding)"
                    );
                    sb.AppendLine(
                        "- **Adult Census Income**: 32,561 rows, 15 columns (9 categorical dictionary columns)"
                    );
                    sb.AppendLine(
                        "- **Diamonds**: 53,940 rows, 10 columns (continuous float metrics & ordinal cuts)"
                    );
                    sb.AppendLine();
                    sb.AppendLine(
                        "| Operation | Scale | Reflection Baseline | Source Generator | Speedup | Memory Reduction |"
                    );
                    sb.AppendLine("|:--- |:---:|:---:|:---:|:---:|:---:|");
                }

                string bTime = bEntry.MeanFormatted;
                string sgTime = sgEntry.MeanFormatted;
                string bAlloc = bEntry.AllocatedFormatted;
                string sgAlloc = sgEntry.AllocatedFormatted;

                string speedupStr = FormatSpeedup(bEntry, sgEntry);
                string memStr = FormatMemoryReduction(sgEntry);

                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| **{s.Title}** | {s.Scale} | {bTime} ({bAlloc}) | **{sgTime}** (**{sgAlloc}**) | {speedupStr} | {memStr} |"
                );
                hasReadRows = true;
            }
        }

        var writeMethods = new (string Codec, string Method)[]
        {
            ("Snappy", "WriteSnappyAsync"),
            ("Zstandard (Fastest)", "WriteZstdFastestAsync"),
            ("Zstandard (Optimal)", "WriteZstdOptimalAsync"),
            ("Uncompressed", "WriteUncompressedAsync"),
        };

        bool hasWrites = writeMethods.Any(w => entries.ContainsKey(w.Method));
        if (hasWrites)
        {
            if (hasReadRows)
            {
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("## 🌐 Real-World Provenanced Dataset Benchmarks");
                sb.AppendLine();
            }

            sb.AppendLine(
                "### 🗜️ TPC-H LineItem Multi-Codec Serialization Throughput (60,175 rows)"
            );
            sb.AppendLine();
            sb.AppendLine(
                "| Codec | Compression Profile | Serialization Time | Allocated Memory |"
            );
            sb.AppendLine("|:--- |:---:|:---:|:---:|");

            foreach (var (codec, method) in writeMethods)
            {
                if (entries.TryGetValue(method, out var wEntry))
                {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"| **{codec}** | Generator Built-in | **{wEntry.MeanFormatted}** | **{wEntry.AllocatedFormatted}** |"
                    );
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSpeedup(BenchmarkEntry? bEntry, BenchmarkEntry sgEntry)
    {
        double? speedup = null;
        if (sgEntry.RatioNumeric.HasValue && sgEntry.RatioNumeric.Value > 0)
        {
            speedup = 1.0 / sgEntry.RatioNumeric.Value;
        }
        else if (
            bEntry?.MeanNumeric.HasValue == true
            && sgEntry.MeanNumeric.HasValue
            && sgEntry.MeanNumeric.Value > 0
        )
        {
            speedup = bEntry.MeanNumeric.Value / sgEntry.MeanNumeric.Value;
        }

        if (speedup.HasValue)
        {
            if (speedup.Value >= 1.095)
            {
                return $"⚡ **{speedup.Value:F1}x faster**";
            }
            if (speedup.Value >= 0.995)
            {
                return "~1.0x (parity)";
            }
            double baselineRatio = 1.0 / speedup.Value;
            return $"{baselineRatio:F2}x baseline";
        }

        return "—";
    }

    private static string FormatMemoryReduction(BenchmarkEntry sgEntry)
    {
        if (sgEntry.AllocRatioNumeric.HasValue && sgEntry.AllocRatioNumeric.Value > 0)
        {
            double ar = sgEntry.AllocRatioNumeric.Value;
            if (ar < 1.0)
            {
                int savedPct = (int)Math.Round((1.0 - ar) * 100);
                return $"📉 **{savedPct}% less memory**";
            }
            return $"{ar:F2}x alloc";
        }

        return "—";
    }

    private static string BuildFullReport(string resultsDir, string headlineTable)
    {
        var sb = new StringBuilder();
        sb.AppendLine(headlineTable);
        sb.AppendLine();

        string realWorldTable = BuildRealWorldDatasetTable(resultsDir);
        if (!string.IsNullOrEmpty(realWorldTable))
        {
            sb.AppendLine(realWorldTable);
            sb.AppendLine();
        }

        sb.AppendLine("## 📊 Detailed BenchmarkDotNet Reports");
        sb.AppendLine();

        string[] mdFiles = Directory.GetFiles(resultsDir, "*-report-github.md");
        Array.Sort(mdFiles, StringComparer.Ordinal);

        foreach (string mdFile in mdFiles)
        {
            string suiteName = Path.GetFileNameWithoutExtension(mdFile)
                .Replace("-report-github", "")
                .Split('.')
                .Last();
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {suiteName}");
            sb.AppendLine();
            sb.AppendLine(File.ReadAllText(mdFile).Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Replaces the content between the table markers in a README file, leaving everything
    /// outside the markers — and the file's existing absence of a BOM — untouched.
    /// </summary>
    public static void UpdateReadmeFile(string filepath, string tableMd)
    {
        if (!File.Exists(filepath))
            return;

        string content = File.ReadAllText(filepath, Encoding.UTF8);
        string pattern = $"{Regex.Escape(StartMarker)}.*?{Regex.Escape(EndMarker)}";
        string replacement = $"{StartMarker}\n{tableMd}\n{EndMarker}";

        var regex = new Regex(
            pattern,
            RegexOptions.Singleline | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2)
        );
        if (regex.IsMatch(content))
        {
            string updated = regex.Replace(content, replacement);
            File.WriteAllText(filepath, updated, Utf8NoBom);
            Console.WriteLine($"Updated headline benchmark table in {filepath}");
        }
    }

    private static string FormatTime(string meanStr)
    {
        if (string.IsNullOrWhiteSpace(meanStr) || meanStr == "NA" || meanStr == "?")
            return "N/A";
        string clean = meanStr.Replace(",", "").Trim();
        if (clean.EndsWith("μs", StringComparison.Ordinal))
        {
            if (
                double.TryParse(
                    clean.Replace("μs", "").Trim(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double val
                )
            )
                return val >= 1000 ? $"{val / 1000:F2} ms" : $"{val:F1} μs";
        }
        else if (
            clean.EndsWith("ms", StringComparison.Ordinal)
            && double.TryParse(
                clean.Replace("ms", "").Trim(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double val
            )
        )
        {
            return $"{val:F2} ms";
        }
        return meanStr;
    }

    private static string FormatMemory(string allocStr)
    {
        if (
            string.IsNullOrWhiteSpace(allocStr)
            || allocStr == "NA"
            || allocStr == "?"
            || allocStr == "-"
        )
            return "N/A";
        string clean = allocStr.Replace(",", "").Trim();
        if (clean.EndsWith("KB", StringComparison.OrdinalIgnoreCase))
        {
            if (
                double.TryParse(
                    clean.Replace("KB", "", StringComparison.OrdinalIgnoreCase).Trim(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double val
                )
            )
                return val >= 1024 ? $"{val / 1024:F2} MB" : $"{val:F1} KB";
        }
        else if (
            clean.EndsWith("MB", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(
                clean.Replace("MB", "", StringComparison.OrdinalIgnoreCase).Trim(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double val
            )
        )
        {
            return $"{val:F2} MB";
        }
        return allocStr;
    }

    private static double? ParseNumber(string str)
    {
        if (string.IsNullOrWhiteSpace(str) || str == "NA" || str == "?")
            return null;
        var match = Regex.Match(
            str.Replace(",", ""),
            @"[0-9.]+",
            RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2)
        );
        return
            match.Success
            && double.TryParse(
                match.Value,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double val
            )
            ? val
            : null;
    }

    /// <summary>
    /// Splits one CSV line, honouring quoted fields. Shared with <see cref="RegressionCheck"/>.
    /// </summary>
    internal static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    private sealed record BenchmarkEntry(
        string Method,
        int Count,
        string MeanFormatted,
        double? MeanNumeric,
        string AllocatedFormatted,
        double? RatioNumeric,
        double? AllocRatioNumeric
    );

    private sealed record Scenario(
        string Title,
        string BaselineMethod,
        string SgMethod,
        int TargetCount
    );

    private sealed record RealWorldScenario(
        string Title,
        string Scale,
        string BaselineMethod,
        string SgMethod
    );
}
