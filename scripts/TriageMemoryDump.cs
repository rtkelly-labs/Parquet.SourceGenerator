#pragma warning disable CA1305, MA0009, MA0011, MA0023, MA0047, CA1852

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// -----------------------------------------------------------------------------
// TriageMemoryDump.cs
// Captures and analyzes managed memory dumps using dotnet-dump for Parquet.SourceGenerator.
// Evaluates heap allocations, GC generational footprint, Large Object Heap (LOH),
// Pinned Object Heap (POH), and Parquet-specific memory retention graphs.
// -----------------------------------------------------------------------------

string? dumpFile = null;
int? targetPid = null;
string? launchCommand = null;
int delaySeconds = 2;
string dumpType = "Full";
string outputDir = "temp/dumps";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--analyze" when i + 1 < args.Length:
            dumpFile = args[++i];
            break;
        case "--pid" when i + 1 < args.Length:
            targetPid = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--command" when i + 1 < args.Length:
            launchCommand = args[++i];
            break;
        case "--delay" when i + 1 < args.Length:
            delaySeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--type" when i + 1 < args.Length:
            dumpType = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outputDir = args[++i];
            break;
        case "--help":
        case "-h":
            PrintUsage();
            return 0;
    }
}

Console.WriteLine("===============================================================");
Console.WriteLine("  Parquet.SourceGenerator - Memory Dump & GC Triage Tool");
Console.WriteLine("===============================================================\n");

EnsureDotnetTools();
Directory.CreateDirectory(outputDir);

// 1. Capture dump if required
if (string.IsNullOrEmpty(dumpFile))
{
    if (targetPid.HasValue)
    {
        dumpFile = Path.Combine(
            outputDir,
            $"dump_pid{targetPid.Value}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dmp"
        );
        CaptureDump(targetPid.Value, dumpFile, dumpType);
    }
    else if (!string.IsNullOrEmpty(launchCommand))
    {
        dumpFile = Path.Combine(outputDir, $"dump_cmd_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dmp");
        LaunchAndCapture(launchCommand, delaySeconds, dumpFile, dumpType);
    }
    else
    {
        Console.WriteLine(
            "No existing dump specified via --analyze and no --pid/--command provided."
        );
        Console.WriteLine("Launching sample CLI profile run to capture memory dump...\n");
        dumpFile = Path.Combine(outputDir, $"dump_cli_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dmp");
        LaunchCliAndCapture(dumpFile, dumpType);
    }
}

if (!File.Exists(dumpFile))
{
    Console.Error.WriteLine($"❌ Dump file not found: {dumpFile}");
    return 1;
}

Console.WriteLine($"📦 Analyzing memory dump: {dumpFile}");
Console.WriteLine($"📂 Output directory for triage reports: {outputDir}\n");

// 2. Execute SOS Analysis via dotnet-dump analyze
var triageResult = AnalyzeDump(dumpFile);

// 3. Write Markdown report
string reportPath = Path.Combine(outputDir, "MEMORY_TRIAGE_REPORT.md");
GenerateMarkdownReport(triageResult, dumpFile, reportPath);
Console.WriteLine($"\n📄 Memory triage report written to: {reportPath}\n");

// 4. Print Summary to Console
PrintSummary(triageResult);

Console.WriteLine("\n🎉 Memory triage completed successfully!");
return 0;

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run scripts/TriageMemoryDump.cs [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --analyze <dump_path>  Directly analyze an existing memory dump file");
    Console.WriteLine(
        "  --pid <process_id>     Capture a dump from a running process and analyze it"
    );
    Console.WriteLine(
        "  --command <cmd>        Launch a process command, wait, capture a dump, and analyze"
    );
    Console.WriteLine(
        "  --delay <seconds>      Seconds to wait before capturing dump for --command (default: 2)"
    );
    Console.WriteLine("  --type <Full|Heap>     Dump type to capture (default: Full)");
    Console.WriteLine(
        "  --out <directory>      Output directory for dump files and reports (default: temp/dumps)"
    );
    Console.WriteLine("  -h, --help             Show help information");
}

static void EnsureDotnetTools()
{
    var (exitCode, _, stderr) = RunDotnet("tool restore --disable-parallel");
    if (exitCode != 0)
    {
        Console.WriteLine($"Note: dotnet tool restore reported: {stderr}");
    }
}

static void CaptureDump(int pid, string destinationPath, string dumpType)
{
    Console.WriteLine($"📸 Capturing {dumpType} dump for process {pid} -> {destinationPath}...");
    var (exitCode, stdout, stderr) = RunDotnet(
        $"tool run dotnet-dump collect -p {pid} -o \"{destinationPath}\" --type {dumpType}"
    );

    if (exitCode != 0)
    {
        Console.Error.WriteLine($"❌ Failed to capture dump:\n{stdout}\n{stderr}");
        throw new InvalidOperationException("Failed to capture memory dump.");
    }
    Console.WriteLine("✅ Dump captured successfully.\n");
}

static void LaunchAndCapture(string command, int delaySec, string destinationPath, string dumpType)
{
    Console.WriteLine($"🚀 Launching process: {command}...");
    var parts = command.Split(' ', 2);
    var psi = new ProcessStartInfo
    {
        FileName = parts[0],
        Arguments = parts.Length > 1 ? parts[1] : "",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    using var proc = Process.Start(psi)!;
    int pid = proc.Id;
    Console.WriteLine(
        $"Process launched with PID {pid}. Waiting {delaySec}s before capturing dump..."
    );
    Thread.Sleep(delaySec * 1000);

    if (!proc.HasExited)
    {
        CaptureDump(pid, destinationPath, dumpType);
    }
    else
    {
        Console.WriteLine("⚠️ Process already finished before dump could be taken.");
    }
}

static void LaunchCliAndCapture(string destinationPath, string dumpType)
{
    // Build and run the test CLI project
    string cliProj = "test/Parquet.SourceGenerator.CLI/Parquet.SourceGenerator.CLI.csproj";
    Console.WriteLine($"🔨 Ensuring {cliProj} is built...");
    RunDotnet($"build {cliProj} -c Release");

    string homeDotnetDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dotnet"
    );
    string dotnetHost = Path.Combine(homeDotnetDir, "dotnet");
    if (!File.Exists(dotnetHost))
        dotnetHost = "dotnet";

    var psi = new ProcessStartInfo
    {
        FileName = dotnetHost,
        Arguments = $"run --project {cliProj} -c Release --no-build",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    using var proc = Process.Start(psi)!;
    int pid = proc.Id;
    Console.WriteLine($"Test workload running under PID {pid}. Capturing dump...");
    Thread.Sleep(1000);

    CaptureDump(pid, destinationPath, dumpType);

    if (!proc.HasExited)
    {
        try
        {
            proc.Kill();
        }
        catch
        { /* ignore */
        }
    }
}

static TriageResult AnalyzeDump(string dumpFilePath)
{
    Console.WriteLine("🔬 Running SOS diagnostic inspection suite...");

    // Commands to run in order:
    // 1. eeheap -gc (Generation sizes, LOH, POH)
    // 2. dumpheap -stat (Object counts and total sizes by type)
    // 3. exit
    string args =
        $"tool run dotnet-dump analyze \"{dumpFilePath}\" -c \"eeheap -gc\" -c \"dumpheap -stat\" -c \"exit\"";
    var (exitCode, stdout, stderr) = RunDotnet(args);

    if (exitCode != 0)
    {
        Console.Error.WriteLine(
            $"Warning: dotnet-dump analyze returned exit code {exitCode}:\n{stderr}"
        );
    }

    return ParseSosOutput(stdout);
}

static TriageResult ParseSosOutput(string output)
{
    var typeStats = new List<HeapTypeStat>();
    var parquetStats = new List<HeapTypeStat>();
    var gcGenerations = new List<string>();

    var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    bool parsingStats = false;
    bool parsingEeheap = false;

    // Regex for dumpheap -stat:
    // MethodTable Count TotalSize Class Name
    // 000121a289f0 46,752 5,137,002 System.String
    var statRegex = new Regex(
        @"^([0-9a-fA-F]+)\s+([0-9,]+)\s+([0-9,]+)\s+(.+)$",
        RegexOptions.Compiled
    );

    foreach (var rawLine in lines)
    {
        var line = rawLine.Trim();

        if (
            line.Contains("GC Heap History")
            || line.Contains("Number of GC Heaps")
            || line.StartsWith("Generation ", StringComparison.OrdinalIgnoreCase)
        )
        {
            parsingEeheap = true;
        }

        if (
            parsingEeheap
            && (
                line.StartsWith("Generation", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Large object heap", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Pinned object heap", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("GC Heap Size", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            gcGenerations.Add(line);
        }

        if (
            line.StartsWith("Statistics:", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("MT") && line.Contains("Count") && line.Contains("TotalSize"))
        )
        {
            parsingStats = true;
            parsingEeheap = false;
            continue;
        }

        if (parsingStats)
        {
            if (line.StartsWith("Total ", StringComparison.OrdinalIgnoreCase))
            {
                parsingStats = false;
                continue;
            }

            var match = statRegex.Match(line);
            if (match.Success)
            {
                string mt = match.Groups[1].Value;
                long count = long.Parse(
                    match.Groups[2].Value.Replace(",", ""),
                    CultureInfo.InvariantCulture
                );
                long size = long.Parse(
                    match.Groups[3].Value.Replace(",", ""),
                    CultureInfo.InvariantCulture
                );
                string name = match.Groups[4].Value.Trim();

                var stat = new HeapTypeStat(mt, count, size, name);
                typeStats.Add(stat);

                if (name.Contains("Parquet", StringComparison.OrdinalIgnoreCase))
                {
                    parquetStats.Add(stat);
                }
            }
        }
    }

    // Sort descending by total size
    typeStats = typeStats.OrderByDescending(s => s.TotalSizeBytes).ToList();
    parquetStats = parquetStats.OrderByDescending(s => s.TotalSizeBytes).ToList();

    return new TriageResult(typeStats, parquetStats, gcGenerations, output);
}

static void PrintSummary(TriageResult result)
{
    Console.WriteLine("-----------------------------------------------------------------------");
    Console.WriteLine("  Top Managed Allocations by Footprint");
    Console.WriteLine("-----------------------------------------------------------------------");
    Console.WriteLine(
        string.Format(
            CultureInfo.InvariantCulture,
            "{0,-48} | {1,8} | {2,12}",
            "Type Name",
            "Count",
            "Total Bytes"
        )
    );
    Console.WriteLine("-----------------------------------------------------------------------");

    foreach (var s in result.TopTypes.Take(12))
    {
        string shortName = s.TypeName.Length > 48 ? "..." + s.TypeName[^45..] : s.TypeName;
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "{0,-48} | {1,8:N0} | {2,12:N0}",
                shortName,
                s.Count,
                s.TotalSizeBytes
            )
        );
    }
    Console.WriteLine("-----------------------------------------------------------------------");

    if (result.ParquetTypes.Count > 0)
    {
        Console.WriteLine(
            "\n-----------------------------------------------------------------------"
        );
        Console.WriteLine("  Parquet.SourceGenerator Specific Allocations");
        Console.WriteLine(
            "-----------------------------------------------------------------------"
        );
        foreach (var s in result.ParquetTypes.Take(8))
        {
            string shortName = s.TypeName.Length > 48 ? "..." + s.TypeName[^45..] : s.TypeName;
            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-48} | {1,8:N0} | {2,12:N0}",
                    shortName,
                    s.Count,
                    s.TotalSizeBytes
                )
            );
        }
        Console.WriteLine(
            "-----------------------------------------------------------------------"
        );
    }
}

static void GenerateMarkdownReport(TriageResult result, string dumpPath, string reportPath)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Managed Memory & GC Triage Report\n");
    sb.AppendLine($"- **Dump File**: `{Path.GetFileName(dumpPath)}`");
    sb.AppendLine($"- **File Size**: `{new FileInfo(dumpPath).Length:N0} bytes`");
    sb.AppendLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"- **Analysis Timestamp**: `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC`"
        )
    );
    sb.AppendLine($"- **Unique Managed Types**: {result.TopTypes.Count:N0}\n");

    if (result.GcGenerations.Count > 0)
    {
        sb.AppendLine("## GC Heap Architecture & Generation Sizes\n");
        sb.AppendLine("```text");
        foreach (var g in result.GcGenerations)
        {
            sb.AppendLine(g);
        }
        sb.AppendLine("```\n");
    }

    sb.AppendLine("## Top Managed Types by Allocated Memory\n");
    sb.AppendLine("| Type Name | Object Count | Total Size (Bytes) |");
    sb.AppendLine("| :--- | :---: | :---: |");
    foreach (var s in result.TopTypes.Take(25))
    {
        sb.AppendLine($"| `{s.TypeName}` | {s.Count:N0} | {s.TotalSizeBytes:N0} |");
    }

    if (result.ParquetTypes.Count > 0)
    {
        sb.AppendLine("\n## Parquet Specific Allocations\n");
        sb.AppendLine("| Type Name | Object Count | Total Size (Bytes) |");
        sb.AppendLine("| :--- | :---: | :---: |");
        foreach (var s in result.ParquetTypes)
        {
            sb.AppendLine($"| `{s.TypeName}` | {s.Count:N0} | {s.TotalSizeBytes:N0} |");
        }
    }

    File.WriteAllText(reportPath, sb.ToString());
}

static (int exitCode, string stdout, string stderr) RunDotnet(string arguments)
{
    string homeDotnetDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dotnet"
    );
    string dotnetHost = Path.Combine(homeDotnetDir, "dotnet");
    if (!File.Exists(dotnetHost))
        dotnetHost = "dotnet";

    var psi = new ProcessStartInfo
    {
        FileName = dotnetHost,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    if (Directory.Exists(homeDotnetDir))
    {
        psi.Environment["DOTNET_ROOT"] = homeDotnetDir;
        psi.Environment["PATH"] =
            Path.Combine(homeDotnetDir, "tools")
            + Path.PathSeparator
            + homeDotnetDir
            + Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH");
    }

    using var process = Process.Start(psi)!;
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    return (process.ExitCode, stdout, stderr);
}

sealed record HeapTypeStat(string MethodTable, long Count, long TotalSizeBytes, string TypeName);

sealed record TriageResult(
    List<HeapTypeStat> TopTypes,
    List<HeapTypeStat> ParquetTypes,
    List<string> GcGenerations,
    string RawOutput
);
