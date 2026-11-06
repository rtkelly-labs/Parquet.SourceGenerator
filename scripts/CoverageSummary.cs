#pragma warning disable CA1305, MA0009, MA0011, MA0023, MA0047, CA1852

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

// -----------------------------------------------------------------------------
// CoverageSummary.cs
//
// Parses Cobertura coverage reports (coverage.cobertura.xml), generates a
// deterministic Markdown summary table for PR sticky comments and CI step
// summaries, and evaluates configured coverage gate thresholds.
// -----------------------------------------------------------------------------

string? inputPath = null;
string? outputMarkdownPath = null;
double minLineRate = 85.0;
double minBranchRate = 70.0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input":
        case "-i":
            if (i + 1 < args.Length)
                inputPath = args[++i];
            break;
        case "--output-markdown":
        case "-o":
            if (i + 1 < args.Length)
                outputMarkdownPath = args[++i];
            break;
        case "--min-line":
            if (
                i + 1 < args.Length
                && double.TryParse(
                    args[++i],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double ml
                )
            )
                minLineRate = ml;
            break;
        case "--min-branch":
            if (
                i + 1 < args.Length
                && double.TryParse(
                    args[++i],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double mb
                )
            )
                minBranchRate = mb;
            break;
        case "--help":
        case "-h":
            Console.WriteLine("Usage: dotnet run scripts/CoverageSummary.cs -- [options]");
            Console.WriteLine("Options:");
            Console.WriteLine(
                "  --input, -i <path>             Path to coverage.cobertura.xml (defaults to search)"
            );
            Console.WriteLine(
                "  --output-markdown, -o <path>   Output markdown file path (for sticky PR comment)"
            );
            Console.WriteLine(
                "  --min-line <percent>           Minimum required line coverage percentage (default: 85.0)"
            );
            Console.WriteLine(
                "  --min-branch <percent>         Minimum required branch coverage percentage (default: 70.0)"
            );
            return 0;
    }
}

if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
{
    inputPath = FindCoberturaXml();
    if (inputPath is null || !File.Exists(inputPath))
    {
        Console.Error.WriteLine("❌ Could not locate coverage.cobertura.xml report.");
        return 1;
    }
}

Console.WriteLine($"📄 Processing coverage report: {inputPath}");
Console.WriteLine(
    $"🎯 Target gate thresholds: Line >= {minLineRate:F2}%, Branch >= {minBranchRate:F2}%"
);

XDocument doc;
try
{
    doc = XDocument.Load(inputPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ Failed to parse coverage report XML: {ex.Message}");
    return 1;
}

var root = doc.Root;
if (root is null || root.Name != "coverage")
{
    Console.Error.WriteLine("❌ Invalid Cobertura XML: root element <coverage> not found.");
    return 1;
}

var packages = new List<PackageCoverage>();

foreach (var pkgElem in root.Descendants("package"))
{
    string name = pkgElem.Attribute("name")?.Value ?? "Unknown";

    // Skip test assemblies and tools if they leaked through
    if (
        name.StartsWith("Parquet.SourceGenerator.Tests", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("BenchmarkSummaryGenerator", StringComparison.OrdinalIgnoreCase)
    )
    {
        continue;
    }

    int linesCovered = 0;
    int linesValid = 0;
    int branchesCovered = 0;
    int branchesValid = 0;

    foreach (var lineElem in pkgElem.Descendants("line"))
    {
        linesValid++;
        if (
            int.TryParse(
                lineElem.Attribute("hits")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int hits
            )
            && hits > 0
        )
        {
            linesCovered++;
        }

        if (bool.TryParse(lineElem.Attribute("branch")?.Value, out bool isBranch) && isBranch)
        {
            // condition-coverage attribute format: "50% (1/2)"
            string? cond = lineElem.Attribute("condition-coverage")?.Value;
            if (cond != null && cond.Contains('(') && cond.Contains(')'))
            {
                int open = cond.IndexOf('(');
                int slash = cond.IndexOf('/', open);
                int close = cond.IndexOf(')', slash);
                if (slash > open && close > slash)
                {
                    ReadOnlySpan<char> span = cond.AsSpan();
                    if (
                        int.TryParse(
                            span.Slice(open + 1, slash - open - 1),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int cov
                        )
                        && int.TryParse(
                            span.Slice(slash + 1, close - slash - 1),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int tot
                        )
                    )
                    {
                        branchesCovered += cov;
                        branchesValid += tot;
                    }
                }
            }
        }
    }

    double lineRate = linesValid > 0 ? (double)linesCovered / linesValid * 100.0 : 100.0;
    double branchRate = branchesValid > 0 ? (double)branchesCovered / branchesValid * 100.0 : 100.0;

    packages.Add(
        new PackageCoverage(
            Name: name,
            LinesCovered: linesCovered,
            LinesValid: linesValid,
            LineRate: lineRate,
            BranchesCovered: branchesCovered,
            BranchesValid: branchesValid,
            BranchRate: branchRate
        )
    );
}

// Deterministic ordering: Alphabetical by package name
var sortedPackages = packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

int totalLinesCovered = sortedPackages.Sum(p => p.LinesCovered);
int totalLinesValid = sortedPackages.Sum(p => p.LinesValid);
int totalBranchesCovered = sortedPackages.Sum(p => p.BranchesCovered);
int totalBranchesValid = sortedPackages.Sum(p => p.BranchesValid);

double totalLineRate =
    totalLinesValid > 0 ? (double)totalLinesCovered / totalLinesValid * 100.0 : 100.0;
double totalBranchRate =
    totalBranchesValid > 0 ? (double)totalBranchesCovered / totalBranchesValid * 100.0 : 100.0;

bool totalPassed = totalLineRate >= minLineRate && totalBranchRate >= minBranchRate;

// Generate Markdown table
var md = new StringBuilder();
md.AppendLine("<!-- parquet-code-coverage-report -->");
md.AppendLine("## 📊 Code Coverage Summary");
md.AppendLine();
md.AppendLine(
    "| Package / Assembly | Lines Covered | Total Lines | Line Rate | Branches Covered | Total Branches | Branch Rate | Status |"
);
md.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

foreach (var pkg in sortedPackages)
{
    bool passed = pkg.LineRate >= minLineRate && pkg.BranchRate >= minBranchRate;
    string status = passed ? "✅ Pass" : "⚠️ Sub-target";
    md.AppendLine(
        CultureInfo.InvariantCulture,
        $"| `{pkg.Name}` | {pkg.LinesCovered:N0} | {pkg.LinesValid:N0} | {pkg.LineRate:F2}% | {pkg.BranchesCovered:N0} | {pkg.BranchesValid:N0} | {pkg.BranchRate:F2}% | {status} |"
    );
}

string totalStatus = totalPassed ? "✅ **Pass**" : "❌ **Fail**";
md.AppendLine(
    CultureInfo.InvariantCulture,
    $"| **Total (Shipping)** | **{totalLinesCovered:N0}** | **{totalLinesValid:N0}** | **{totalLineRate:F2}%** | **{totalBranchesCovered:N0}** | **{totalBranchesValid:N0}** | **{totalBranchRate:F2}%** | {totalStatus} |"
);
md.AppendLine();
md.AppendLine("> [!NOTE]");
md.AppendLine(
    CultureInfo.InvariantCulture,
    $"> **Gate Policy**: Minimum Line Rate: **{minLineRate:F2}%** | Minimum Branch Rate: **{minBranchRate:F2}%**"
);
md.AppendLine(
    CultureInfo.InvariantCulture,
    $"> Report generated on `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC`"
);

string markdownResult = md.ToString();
Console.WriteLine();
Console.WriteLine(markdownResult);

if (!string.IsNullOrEmpty(outputMarkdownPath))
{
    string? dir = Path.GetDirectoryName(outputMarkdownPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    File.WriteAllText(outputMarkdownPath, markdownResult);
    Console.WriteLine($"💾 Saved coverage summary markdown to: {outputMarkdownPath}");
}

if (!totalPassed)
{
    Console.Error.WriteLine(
        $"❌ Coverage gate failed! Line: {totalLineRate:F2}% (req >= {minLineRate:F2}%), Branch: {totalBranchRate:F2}% (req >= {minBranchRate:F2}%)"
    );
    return 1;
}

Console.WriteLine("✅ Code coverage gate passed successfully.");
return 0;

static string? FindCoberturaXml()
{
    string[] candidateDirs =
    [
        Path.Combine(AppContext.BaseDirectory, "TestResults"),
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "test",
            "Parquet.SourceGenerator.Tests",
            "TestResults"
        ),
        Path.Combine(
            Directory.GetCurrentDirectory(),
            "test",
            "Parquet.SourceGenerator.Tests",
            "TestResults"
        ),
        Path.Combine(Directory.GetCurrentDirectory(), "TestResults"),
    ];

    foreach (var dir in candidateDirs)
    {
        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(
                dir,
                "coverage.cobertura.xml",
                SearchOption.AllDirectories
            );
            if (files.Length > 0)
            {
                return files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
        }
    }

    var cwdFiles = Directory.GetFiles(
        Directory.GetCurrentDirectory(),
        "coverage.cobertura.xml",
        SearchOption.AllDirectories
    );
    return cwdFiles.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
}

sealed record PackageCoverage(
    string Name,
    int LinesCovered,
    int LinesValid,
    double LineRate,
    int BranchesCovered,
    int BranchesValid,
    double BranchRate
);
