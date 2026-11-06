#pragma warning disable CA1305, MA0009, MA0011, MA0023, MA0047, CA1852

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// -----------------------------------------------------------------------------
// InterrogateIL.cs
// Interrogates Roslyn-generated IL and decompiled C# for Parquet.SourceGenerator
// using ilspycmd and dotnet-inspect. Analyzes generated methods for performance
// anti-patterns: boxing ('box'), virtual calls ('callvirt'), and heap allocations.
// -----------------------------------------------------------------------------

string projectPath = "test/Parquet.SourceGenerator.CLI/Parquet.SourceGenerator.CLI.csproj";
string? assemblyPath = null;
string typeFilter = "*ParquetExtensions*";
string outputDir = "temp/il";
bool failOnBoxing = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length:
            projectPath = args[++i];
            break;
        case "--assembly" when i + 1 < args.Length:
            assemblyPath = args[++i];
            break;
        case "--type" when i + 1 < args.Length:
            typeFilter = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outputDir = args[++i];
            break;
        case "--check":
            failOnBoxing = true;
            break;
        case "--help":
        case "-h":
            PrintUsage();
            return 0;
    }
}

Console.WriteLine("===============================================================");
Console.WriteLine("  Parquet.SourceGenerator - IL Interrogation & Codegen Analyzer");
Console.WriteLine("===============================================================\n");

// Ensure dotnet tools are restored
EnsureDotnetTools();

// If assembly path is not provided, build the target project in Release configuration
if (string.IsNullOrEmpty(assemblyPath))
{
    Console.WriteLine($"🔨 Building project in Release configuration: {projectPath}...");
    assemblyPath = BuildProject(projectPath);
}

if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"❌ Target assembly not found: {assemblyPath}");
    return 1;
}

Console.WriteLine($"📦 Target assembly: {assemblyPath}");
Directory.CreateDirectory(outputDir);
Console.WriteLine($"📂 Output directory for IL & decompiled artifacts: {outputDir}\n");

// Discover classes in target assembly
Console.WriteLine("🔍 Discovering types in target assembly...");
var allClasses = ListClasses(assemblyPath);
var matchingClasses = FilterTypes(allClasses, typeFilter);

if (matchingClasses.Count == 0)
{
    Console.WriteLine($"⚠️ No types matched filter '{typeFilter}'. Available types:");
    foreach (var c in allClasses.Take(10))
    {
        Console.WriteLine($"   - {c}");
    }
    return 1;
}

Console.WriteLine($"✅ Found {matchingClasses.Count} matching type(s) for pattern '{typeFilter}':");
foreach (var c in matchingClasses)
{
    Console.WriteLine($"   • {c}");
}
Console.WriteLine();

// Disassemble IL for target assembly once
Console.WriteLine("⚡ Disassembling assembly IL...");
string assemblyIl = DisassembleAssembly(assemblyPath);

var reports = new List<TypeIlReport>();
int totalBoxes = 0;

foreach (var typeName in matchingClasses)
{
    Console.WriteLine($"🔎 Interrogating IL for: {typeName}...");
    var report = InterrogateType(
        typeName,
        assemblyPath,
        assemblyIl,
        outputDir,
        decompileCs: !failOnBoxing
    );
    reports.Add(report);
    totalBoxes += report.BoxCount;
}

// Generate Markdown Summary Report
string reportPath = Path.Combine(outputDir, "IL_INTERROGATION_REPORT.md");
GenerateMarkdownReport(reports, assemblyPath, reportPath);
Console.WriteLine($"\n📄 Detailed report written to: {reportPath}\n");

// Print Summary Table to Console
PrintSummaryTable(reports);

if (failOnBoxing && totalBoxes > 0)
{
    Console.Error.WriteLine(
        $"\n❌ IL verification failed: {totalBoxes} boxing operation(s) ('box') detected in generated code!"
    );
    return 1;
}

Console.WriteLine("\n🎉 IL interrogation completed successfully!");
return 0;

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run scripts/InterrogateIL.cs [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine(
        "  --project <path>    Project to compile and inspect (default: test/Parquet.SourceGenerator.CLI/...)"
    );
    Console.WriteLine("  --assembly <path>   Prebuilt assembly DLL to inspect directly");
    Console.WriteLine(
        "  --type <filter>     Type name pattern to match (default: *ParquetExtensions*)"
    );
    Console.WriteLine(
        "  --out <directory>   Output directory for dumped IL and reports (default: temp/il)"
    );
    Console.WriteLine(
        "  --check             Exit with code 1 if any 'box' opcodes are detected in target types"
    );
    Console.WriteLine("  -h, --help          Show help information");
}

static void EnsureDotnetTools()
{
    var (exitCode, _, stderr) = RunDotnet("tool restore --disable-parallel");
    if (exitCode != 0)
    {
        Console.WriteLine($"Note: dotnet tool restore reported: {stderr}");
    }
}

static string BuildProject(string projectFile)
{
    var (exitCode, stdout, stderr) = RunDotnet(
        $"build {projectFile} --configuration Release -nodeReuse:false -p:UseSharedCompilation=false"
    );
    if (exitCode != 0)
    {
        Console.Error.WriteLine($"Failed to build {projectFile}:\n{stdout}\n{stderr}");
        throw new InvalidOperationException("Project build failed.");
    }

    // Try to deduce output assembly path
    string projDir = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
    string projName = Path.GetFileNameWithoutExtension(projectFile);

    string[] searchPaths =
    [
        Path.Combine(projDir, "bin", "Release", "net8.0", $"{projName}.dll"),
        Path.Combine(projDir, "bin", "Release", "net9.0", $"{projName}.dll"),
        Path.Combine(projDir, "bin", "Release", "net10.0", $"{projName}.dll"),
    ];

    foreach (var path in searchPaths)
    {
        if (File.Exists(path))
            return path;
    }

    // Fallback: search recursively in bin/Release
    string binRelease = Path.Combine(projDir, "bin", "Release");
    if (Directory.Exists(binRelease))
    {
        var found = Directory
            .GetFiles(binRelease, $"{projName}.dll", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (found != null)
            return found;
    }

    throw new FileNotFoundException($"Could not locate built assembly for {projectFile}");
}

static List<string> ListClasses(string assemblyFile)
{
    var (exitCode, stdout, stderr) = RunDotnet($"tool run ilspycmd -l c \"{assemblyFile}\"");
    if (exitCode != 0)
    {
        Console.Error.WriteLine($"Warning: Failed to list classes via ilspycmd:\n{stderr}");
        return [];
    }

    var list = new List<string>();
    foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        var line = rawLine.Trim();
        if (line.StartsWith("Class ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(line["Class ".Length..].Trim());
        }
    }
    return list;
}

static List<string> FilterTypes(List<string> types, string filterPattern)
{
    string regexPattern =
        "^" + Regex.Escape(filterPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    var regex = new Regex(
        regexPattern,
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2)
    );
    return types
        .Where(t => regex.IsMatch(t) && !t.Contains("<>c", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

static string DisassembleAssembly(string assemblyFile)
{
    var (exitCode, stdout, stderr) = RunDotnet($"tool run ilspycmd -il \"{assemblyFile}\"");
    if (exitCode != 0)
    {
        Console.Error.WriteLine($"Warning: Failed to disassemble assembly via ilspycmd:\n{stderr}");
        return string.Empty;
    }
    return stdout;
}

static TypeIlReport InterrogateType(
    string typeName,
    string assemblyFile,
    string assemblyIl,
    string outDir,
    bool decompileCs = true
)
{
    // Sanitize filename
    string safeName = Regex.Replace(
        typeName,
        @"[^\w\.\-]",
        "_",
        RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2)
    );

    // 1. Decompile C# (optional for fast checks)
    string csPath = string.Empty;
    if (decompileCs)
    {
        csPath = Path.Combine(outDir, $"{safeName}.decompiled.cs");
        var (_, csOut, _) = RunDotnet($"tool run ilspycmd -t \"{typeName}\" \"{assemblyFile}\"");
        File.WriteAllText(csPath, csOut);
    }

    // 2. Disassemble IL
    string ilPath = Path.Combine(outDir, $"{safeName}.il");

    // Filter IL content for this specific class block
    string classIl = ExtractClassIl(assemblyIl, typeName);
    if (string.IsNullOrWhiteSpace(classIl))
    {
        classIl = assemblyIl;
    }
    File.WriteAllText(ilPath, classIl);

    // Analyze IL instructions
    var boxingMatches = Regex.Matches(
        classIl,
        @"^\s*IL_[0-9a-fA-F]+:\s+box\s+(.+)$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2)
    );
    var callvirtMatches = Regex.Matches(
        classIl,
        @"^\s*IL_[0-9a-fA-F]+:\s+callvirt\s+(.+)$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2)
    );
    var newobjMatches = Regex.Matches(
        classIl,
        @"^\s*IL_[0-9a-fA-F]+:\s+(?:newobj|newarr)\s+(.+)$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2)
    );

    var boxingDetails = new List<string>();
    foreach (Match m in boxingMatches)
    {
        boxingDetails.Add(m.Value.Trim());
    }

    return new TypeIlReport(
        typeName,
        csPath,
        ilPath,
        boxingMatches.Count,
        callvirtMatches.Count,
        newobjMatches.Count,
        boxingDetails
    );
}

static string ExtractClassIl(string fullIl, string typeName)
{
    var lines = fullIl.Split(['\r', '\n']);
    var sb = new StringBuilder();
    bool insideClass = false;
    int braceDepth = 0;

    foreach (var line in lines)
    {
        if (!insideClass)
        {
            if (
                line.StartsWith(".class ", StringComparison.Ordinal)
                && line.Contains(typeName, StringComparison.Ordinal)
            )
            {
                insideClass = true;
                sb.AppendLine(line);
                if (line.Contains('{'))
                    braceDepth++;
            }
        }
        else
        {
            sb.AppendLine(line);
            braceDepth += line.Count(c => c == '{');
            braceDepth -= line.Count(c => c == '}');

            if (braceDepth <= 0 && line.Contains('}'))
            {
                break;
            }
        }
    }

    return sb.Length > 0 ? sb.ToString() : fullIl;
}

static void PrintSummaryTable(List<TypeIlReport> reports)
{
    Console.WriteLine("-----------------------------------------------------------------------");
    Console.WriteLine(
        string.Format(
            CultureInfo.InvariantCulture,
            "{0,-45} | {1,5} | {2,8} | {3,8}",
            "Type Name",
            "Box",
            "Callvirt",
            "HeapAlloc"
        )
    );
    Console.WriteLine("-----------------------------------------------------------------------");

    foreach (var r in reports)
    {
        string shortName = r.TypeName.Length > 45 ? "..." + r.TypeName[^42..] : r.TypeName;
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "{0,-45} | {1,5} | {2,8} | {3,8}",
                shortName,
                r.BoxCount,
                r.CallvirtCount,
                r.HeapAllocCount
            )
        );
    }
    Console.WriteLine("-----------------------------------------------------------------------");
}

static void GenerateMarkdownReport(
    List<TypeIlReport> reports,
    string assemblyPath,
    string reportPath
)
{
    var sb = new StringBuilder();
    sb.AppendLine("# IL Interrogation & Codegen Performance Report\n");
    sb.AppendLine($"- **Assembly**: `{Path.GetFileName(assemblyPath)}`");
    sb.AppendLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"- **Generated At**: `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC`"
        )
    );
    sb.AppendLine($"- **Total Types Interrogated**: {reports.Count}\n");

    sb.AppendLine("## Summary Table\n");
    sb.AppendLine(
        "| Type Name | `box` Opcodes | `callvirt` Calls | `newobj`/`newarr` Allocations |"
    );
    sb.AppendLine("| :--- | :---: | :---: | :---: |");

    foreach (var r in reports)
    {
        sb.AppendLine(
            $"| `{r.TypeName}` | {r.BoxCount} | {r.CallvirtCount} | {r.HeapAllocCount} |"
        );
    }

    sb.AppendLine("\n## Findings & Anti-Pattern Analysis\n");
    foreach (var r in reports)
    {
        sb.AppendLine($"### `{r.TypeName}`\n");
        if (!string.IsNullOrEmpty(r.DecompiledCsPath))
        {
            sb.AppendLine(
                $"- **Decompiled C#**: [{Path.GetFileName(r.DecompiledCsPath)}]({r.DecompiledCsPath})"
            );
        }
        sb.AppendLine($"- **Disassembled IL**: [{Path.GetFileName(r.IlPath)}]({r.IlPath})");

        if (r.BoxCount > 0)
        {
            sb.AppendLine(
                $"\n> [!WARNING]\n> **{r.BoxCount} Boxing Operation(s) Detected:**\n> Value types boxed to heap on hot paths:"
            );
            sb.AppendLine("```cil");
            foreach (var b in r.BoxingDetails)
            {
                sb.AppendLine(b);
            }
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine(
                "\n> [!NOTE]\n> Zero boxing operations (`box`) detected. Value type fast-paths preserved."
            );
        }
        sb.AppendLine();
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
    {
        dotnetHost = "dotnet";
    }

    var psi = new ProcessStartInfo
    {
        FileName = dotnetHost,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
    psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
    psi.Environment["DOTNET_BUILD_SERVER_DISABLE"] = "1";

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
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    string stdout = stdoutTask.GetAwaiter().GetResult();
    string stderr = stderrTask.GetAwaiter().GetResult();

    return (process.ExitCode, stdout, stderr);
}

sealed record TypeIlReport(
    string TypeName,
    string DecompiledCsPath,
    string IlPath,
    int BoxCount,
    int CallvirtCount,
    int HeapAllocCount,
    List<string> BoxingDetails
);
