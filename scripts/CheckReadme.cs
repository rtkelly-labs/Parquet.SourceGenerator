using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// -----------------------------------------------------------------------------
// CheckReadme.cs
//
// Compiles the C# examples in README.md and PACKAGE_README.md against the generator, exactly as a
// consumer references it (#596, doc 47 section 8: "README examples use only the stable surface").
// A README that names a removed or renamed member fails the build with the README line number.
//
// What is compiled, by marker:
//   ```csharp compile        a block of statements, wrapped in `static async Task Snippet_N()`
//   ```csharp compile-file   a compilation unit fragment (usings, models), placed at file scope
//   <!-- readme-compile-members ... -->   helper fields and methods the statements assume
//                                          (events, stream, GetEvents(), ...), invisible when rendered
//   <!-- readme-compile-file ... -->      models the prose refers to without showing
// Every other fenced block is not compiled. Each README gets its own namespace, so both may declare
// the same model. Compiler errors name the README line because each block is emitted behind a
// `#line` directive.
//
// It fails when it would examine nothing: a README without a single compiled block, a marker block
// that never closes, or a build that reports no error yet compiled no snippet.
//
// Usage:
//   dotnet run scripts/CheckReadme.cs                              # README.md and PACKAGE_README.md
//   dotnet run scripts/CheckReadme.cs -- --readme path/to/file.md  # explicit files (repeatable)
// -----------------------------------------------------------------------------

string root = FindRepoRoot();
string project = Path.Combine(
    root,
    "test",
    "Parquet.SourceGenerator.ReadmeSnippets",
    "Parquet.SourceGenerator.ReadmeSnippets.csproj"
);
string outputDir = Path.Combine(Path.GetDirectoryName(project)!, "obj", "readme");

var readmes = new List<string>();
for (int i = 0; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--readme", StringComparison.Ordinal))
    {
        readmes.Add(Path.GetFullPath(args[i + 1]));
    }
}

if (readmes.Count == 0)
{
    readmes.Add(Path.Combine(root, "README.md"));
    readmes.Add(Path.Combine(root, "PACKAGE_README.md"));
}

Directory.CreateDirectory(outputDir);
foreach (string stale in Directory.GetFiles(outputDir, "*.g.cs"))
{
    File.Delete(stale);
}

int totalBlocks = 0;
int failures = 0;
int readmeIndex = 0;
foreach (string readme in readmes)
{
    if (!File.Exists(readme))
    {
        Console.Error.WriteLine($"::error::{readme} does not exist.");
        return 1;
    }

    List<Block> blocks;
    try
    {
        blocks = Extract(File.ReadAllLines(readme), Path.GetFileName(readme));
    }
    catch (FormatException exception)
    {
        Console.Error.WriteLine($"::error file={readme}::{exception.Message}");
        return 1;
    }

    int compiled = blocks.Count(b => b.Kind is BlockKind.Statements or BlockKind.File);
    if (compiled == 0)
    {
        Console.Error.WriteLine(
            $"::error file={readme}::{Path.GetFileName(readme)} has no ```csharp compile block: the README check examined nothing."
        );
        failures++;
        continue;
    }

    totalBlocks += compiled;
    // The index keeps two inputs with the same file name from overwriting each other's output.
    string name =
        $"R{++readmeIndex}_"
        + Regex.Replace(Path.GetFileNameWithoutExtension(readme), "[^A-Za-z0-9]", string.Empty);
    File.WriteAllText(
        Path.Combine(outputDir, name + ".g.cs"),
        Render(name, Path.GetFileName(readme), blocks)
    );
    Console.WriteLine($"{Path.GetFileName(readme)}: {compiled} compiled block(s).");
}

if (failures > 0 || totalBlocks == 0)
{
    return 1;
}

// A plain build: the project turns the extracted files into one compilation with the generator and
// the attributes referenced the way a consumer references them. Errors cite README lines.
(int exit, string output) = Run(
    "dotnet",
    root,
    "build",
    project,
    "--configuration",
    "Release",
    "--nologo",
    "-v:q",
    "-clp:NoSummary;ErrorsOnly",
    "-p:TreatWarningsAsErrors=false"
);

if (exit != 0)
{
    Console.Error.WriteLine("README examples do not compile:");
    foreach (
        string line in output
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains(" error ", StringComparison.Ordinal))
            .Distinct()
    )
    {
        Console.Error.WriteLine("  " + line);
    }

    Console.Error.WriteLine(
        "A README example uses a member the generated or shipped API no longer has. Update the example."
    );
    return 1;
}

Console.WriteLine(
    $"README examples compile: {totalBlocks} block(s) across {readmes.Count} file(s)."
);
return 0;

static bool IsFenceCloser(string line, char fenceChar, int fenceLength)
{
    string trimmed = line.TrimEnd();
    int indent = trimmed.Length - trimmed.TrimStart(' ').Length;
    string body = trimmed.TrimStart(' ');
    return indent <= 3 && body.Length >= fenceLength && body.All(c => c == fenceChar);
}

static List<Block> Extract(string[] lines, string file)
{
    var blocks = new List<Block>();
    for (int i = 0; i < lines.Length; i++)
    {
        string line = lines[i].TrimEnd();
        Match opener = Regex.Match(
            line,
            "^ {0,3}(?<fence>`{3,}|~{3,})(?<info>[^`]*)$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2)
        );
        if (opener.Success)
        {
            // CommonMark: a fence closes on a line of the same character, at least as long as the
            // opener, with nothing but whitespace after it. A longer opener (four backticks) is how
            // a block shows a fence inside itself.
            char fenceChar = opener.Groups["fence"].Value[0];
            int fenceLength = opener.Groups["fence"].Length;
            string info = opener.Groups["info"].Value.Trim();
            int close = Array.FindIndex(
                lines,
                i + 1,
                l => IsFenceCloser(l, fenceChar, fenceLength)
            );
            if (close < 0)
            {
                throw new FormatException($"{file}:{i + 1}: a code fence never closes.");
            }

            BlockKind kind = info switch
            {
                "csharp compile" => BlockKind.Statements,
                "csharp compile-file" => BlockKind.File,
                _ => BlockKind.None,
            };
            if (kind != BlockKind.None)
            {
                blocks.Add(new Block(kind, i + 2, lines[(i + 1)..close]));
            }

            i = close;
        }
        else if (line.StartsWith("<!-- readme-compile-", StringComparison.Ordinal))
        {
            BlockKind kind =
                line.StartsWith("<!-- readme-compile-members", StringComparison.Ordinal)
                    ? BlockKind.Members
                : line.StartsWith("<!-- readme-compile-file", StringComparison.Ordinal)
                    ? BlockKind.HiddenFile
                : throw new FormatException($"{file}:{i + 1}: unknown readme-compile marker.");
            int close = Array.FindIndex(lines, i + 1, l => l.TrimEnd() == "-->");
            if (close < 0)
            {
                throw new FormatException(
                    $"{file}:{i + 1}: a readme-compile comment never closes."
                );
            }

            blocks.Add(new Block(kind, i + 2, lines[(i + 1)..close]));
            i = close;
        }
    }

    return blocks;
}

static string Render(string name, string file, List<Block> blocks)
{
    var text = new StringBuilder();
    text.AppendLine(
        "// <auto-generated> Extracted from "
            + file
            + " by scripts/CheckReadme.cs; never committed. </auto-generated>"
    );
    text.AppendLine("#nullable enable");
    text.AppendLine("#pragma warning disable");
    text.AppendLine("using System;");
    text.AppendLine("using System.Collections.Generic;");
    text.AppendLine("using System.IO;");
    text.AppendLine("using System.Linq;");
    text.AppendLine("using System.Threading;");
    text.AppendLine("using System.Threading.Tasks;");
    text.AppendLine("using Parquet;");
    text.AppendLine("using Parquet.SourceGenerator;");
    text.AppendLine();
    text.AppendLine($"namespace ReadmeSnippets.{name}");
    text.AppendLine("{");

    // File-scope code first (models and usings), in README order.
    foreach (Block block in blocks.Where(b => b.Kind is BlockKind.File or BlockKind.HiddenFile))
    {
        AppendWithLines(text, file, block);
    }

    text.AppendLine("internal static partial class Snippets");
    text.AppendLine("{");
    foreach (Block block in blocks.Where(b => b.Kind == BlockKind.Members))
    {
        AppendWithLines(text, file, block);
    }

    int n = 0;
    foreach (Block block in blocks.Where(b => b.Kind == BlockKind.Statements))
    {
        text.AppendLine($"    internal static async Task Snippet_{++n}()");
        text.AppendLine("    {");
        AppendWithLines(text, file, block);
        text.AppendLine("    }");
    }

    text.AppendLine("}");
    text.AppendLine("}");
    return text.ToString();
}

static void AppendWithLines(StringBuilder text, string file, Block block)
{
    text.AppendLine($"#line {block.FirstLine} \"{file}\"");
    foreach (string line in block.Lines)
    {
        text.AppendLine(line);
    }

    text.AppendLine("#line default");
}

static (int ExitCode, string Output) Run(
    string fileName,
    string workingDirectory,
    params string[] arguments
)
{
    var start = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start)!;
    // Both pipes are drained at once: a build that fills the unread one would otherwise block
    // forever while this waits on the other.
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (
        process.ExitCode,
        stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()
    );
}

static string FindRepoRoot()
{
    // `git rev-parse --show-toplevel`: in a worktree `.git` is a file, so walking up would escape.
    (int exit, string output) = Run(
        "git",
        Directory.GetCurrentDirectory(),
        "rev-parse",
        "--show-toplevel"
    );
    if (exit != 0 || output.Trim().Length == 0)
    {
        Console.Error.WriteLine("::error::Could not locate the repository root.");
        Environment.Exit(1);
    }

    return output.Split('\n')[0].Trim();
}

enum BlockKind
{
    None,
    Statements,
    File,
    HiddenFile,
    Members,
}

record Block(BlockKind Kind, int FirstLine, string[] Lines);
