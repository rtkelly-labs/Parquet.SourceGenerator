using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

// Apache Parquet conformance verifier for issue #167.
//
// Modes:
//   --restore <dir>
//       Downloads the pinned toolchain described by test/data/apache-conformance.json into <dir>
//       and verifies every SHA-256 (existing files with matching hashes are kept).
//   verify (default)
//       --tools-dir <dir>        directory holding the restored toolchain (required)
//       --java <path>            java launcher; falls back to PARQUET_APACHE_CONFORMANCE_JAVA,
//                                then JAVA_HOME/bin/java, then "java" on PATH
//       --output-dir <dir>       evidence dumps (default: temp/apache-conformance)
//       --generated <path>       file emitted by the generated writer, checked against the model
//       --root <dir>             repository root for fixture paths (default: current directory)
//       --corpus-base <path>     valid fixture used to derive the negative corpus
//
// Exit code 0 means every conformance check passed. Any failure prints a FAIL line per check and
// retains tool dumps plus the corrupt corpus under --output-dir.

bool restoreMode = Array.IndexOf(args, "--restore") >= 0;
string toolManifestPath = Option(args, "--tool-manifest") ?? "test/data/apache-conformance.json";
using JsonDocument toolManifest = JsonDocument.Parse(File.ReadAllText(toolManifestPath));
string repository = toolManifest.RootElement.GetProperty("repository").GetString()!;
JsonElement[] artifacts = toolManifest
    .RootElement.GetProperty("artifacts")
    .EnumerateArray()
    .ToArray();

if (restoreMode)
{
    string toolsDir = RequiredOption(args, "--restore");
    Directory.CreateDirectory(toolsDir);
    RestoreToolchain(toolsDir);
    return 0;
}

string toolsDirVerify = RequiredOption(args, "--tools-dir");
foreach (JsonElement artifact in artifacts)
{
    VerifyArtifactSha(toolsDirVerify, artifact);
}

string java =
    Option(args, "--java")
    ?? Environment.GetEnvironmentVariable("PARQUET_APACHE_CONFORMANCE_JAVA")
    ?? Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty, "bin", "java");
if (!File.Exists(java))
{
    java = "java";
}

string root = Option(args, "--root") ?? Directory.GetCurrentDirectory();
string outputDir = Option(args, "--output-dir") ?? Path.Combine(root, "temp", "apache-conformance");
Directory.CreateDirectory(outputDir);
string classpath = string.Join(
    Path.PathSeparator,
    artifacts.Select(a => Path.Combine(toolsDirVerify, a.GetProperty("file").GetString()!))
);

int checks = 0;
var failures = new List<string>();

void Check(bool condition, string description)
{
    checks++;
    if (!condition)
    {
        failures.Add(description);
        Console.WriteLine($"FAIL: {description}");
    }
}

void WriteEvidence(string name, string content) =>
    File.WriteAllText(Path.Combine(outputDir, name), content);

void ArchiveSource(string path, string evidenceName)
{
    string sourcesDir = Path.Combine(outputDir, "sources");
    Directory.CreateDirectory(sourcesDir);
    string destination = Path.Combine(sourcesDir, evidenceName);
    if (!File.Exists(destination))
    {
        File.Copy(path, destination);
    }
}

(string output, int exit) RunTool(string file, params string[] toolArguments)
{
    ProcessResult result = Run(
        java,
        [
            "-Djava.security.manager=allow",
            "-cp",
            classpath,
            "org.apache.parquet.cli.Main",
            .. toolArguments,
            Path.GetFullPath(file, root),
        ]
    );
    return (result.Output, result.ExitCode);
}

// Version evidence: the exact launcher and the exact pinned artifacts that accepted or rejected
// every file in this run.
ProcessResult javaVersion = Run(java, ["-version"]);
WriteEvidence(
    "java-version.txt",
    javaVersion.Output.Trim()
        + Environment.NewLine
        + string.Join(Environment.NewLine, classpath.Split(Path.PathSeparator))
);

using JsonDocument fixtureManifest = JsonDocument.Parse(
    File.ReadAllText(
        Path.Combine(root, Option(args, "--fixture-manifest") ?? "test/data/fixture-manifest.json")
    )
);

string corpusBase =
    Option(args, "--corpus-base") ?? "test/data/v1/01_small_flat_primitives.parquet";

foreach (
    JsonElement fixture in fixtureManifest.RootElement.GetProperty("fixtures").EnumerateArray()
)
{
    string relativePath = fixture.GetProperty("path").GetString()!;
    string fullPath = Path.GetFullPath(relativePath, root);
    if (!File.Exists(fullPath))
    {
        Console.WriteLine($"SKIP (missing file, e.g. LFS not hydrated): {relativePath}");
        continue;
    }

    string support = fixture.GetProperty("support").GetString()!;
    string evidencePrefix = Regex.Replace(relativePath, @"[\\/]", "_");

    ArchiveSource(fullPath, $"{evidencePrefix}.source.bin");
    (string meta, int metaExit) = RunTool(fullPath, "meta");
    WriteEvidence($"{evidencePrefix}.meta.txt", meta);
    Check(
        metaExit == 0,
        $"{relativePath}: parquet-cli meta must accept the file (exit {metaExit})"
    );

    string expectedCreatedBy = fixture.GetProperty("createdBy").GetString()!;
    Check(
        meta.Contains($"Created by: {expectedCreatedBy}", StringComparison.Ordinal),
        $"{relativePath}: created-by metadata must match manifest '{expectedCreatedBy}'"
    );

    long rows = long.Parse(fixture.GetProperty("rowCount").ToString()!.Trim('"'));
    int groups = int.Parse(fixture.GetProperty("rowGroupCount").ToString()!.Trim('"'));
    int columns = int.Parse(fixture.GetProperty("columnCount").ToString()!.Trim('"'));

    int observedGroups = Regex
        .Matches(meta, @"^Row group \d+:\s+count:", RegexOptions.Multiline)
        .Count;
    long observedRows = Regex
        .Matches(meta, @"^Row group \d+:\s+count:\s+(\d+)", RegexOptions.Multiline)
        .Sum(match => long.Parse(match.Groups[1].Value));
    Check(
        observedGroups == groups,
        $"{relativePath}: row group count expected {groups}, observed {observedGroups}"
    );
    Check(
        observedRows == rows,
        $"{relativePath}: row count expected {rows}, observed {observedRows}"
    );

    List<SchemaField> fields = ParseSchemaLeafColumns(meta);
    Check(
        fields.Count == columns,
        $"{relativePath}: column chunk count expected {columns}, observed {fields.Count}"
    );

    (string head, int headExit) = RunTool(fullPath, "head", "-n", "5");
    WriteEvidence($"{evidencePrefix}.head.txt", head);
    // Row-shape assertions on head output are made only for the generated file below; external
    // producers use column names that parquet-cli renders as key/value lines rather than JSON.
    Check(
        headExit == 0 && head.Trim().Length > 0,
        $"{relativePath}: parquet-cli head must decode the first rows without error"
    );

    if (support == "conformance-only-unsupported-model")
    {
        Check(
            meta.Contains(" repeated ", StringComparison.Ordinal)
                || meta.Contains("group ", StringComparison.Ordinal),
            $"{relativePath}: nested shape must stay visible to the tooling as repeated/group"
        );
    }
}

string generated = Option(args, "--generated") ?? "";
if (generated.Length > 0)
{
    ArchiveSource(Path.GetFullPath(generated, root), "generated.source.bin");
    (string meta, int metaExit) = RunTool(generated, "meta");
    WriteEvidence("generated.meta.txt", meta);
    Check(metaExit == 0, "generated output: parquet-cli meta must accept it");
    Check(
        Regex.IsMatch(meta, @"^Created by:\s*Parquet\.Net", RegexOptions.Multiline),
        "generated output: created-by must identify the Parquet.Net backend"
    );

    var expectedFields = new (string Name, string Repetition, string Physical, string? Annotation)[]
    {
        ("id", "required", "int32", "INTEGER(32,true)"),
        ("required_name", "required", "binary", "STRING"),
        ("optional_name", "optional", "binary", "STRING"),
        ("payload", "optional", "binary", null),
        ("amount", "required", "int64", "DECIMAL(18,4)"),
        ("timestamp", "required", "int64", "TIMESTAMP(MICROS,true)"),
        ("status", "optional", "int32", "INTEGER(32,true)"),
    };
    List<SchemaField> fields = ParseSchemaLeafColumns(meta);
    foreach (
        (string name, string repetition, string physical, string? annotation) in expectedFields
    )
    {
        SchemaField? actual = fields.Find(f => f.Name == name);
        Check(
            actual is not null
                && actual.Repetition == repetition
                && actual.Physical == physical
                && actual.Annotation == annotation,
            $"generated output: field {name} expected {repetition} {physical}"
                + (annotation is null ? string.Empty : $" ({annotation})")
                + $", observed {(actual is null ? "<missing>" : $"{actual.Repetition} {actual.Physical}" + (actual.Annotation is null ? string.Empty : $" ({actual.Annotation})"))}"
        );
    }

    (string head, int headExit) = RunTool(generated, "head", "-n", "10");
    WriteEvidence("generated.head.txt", head);
    Check(headExit == 0, "generated output: parquet-cli head must decode every row");
    string[] rows = head.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Check(rows.Length == 3, $"generated output: head rows expected 3, observed {rows.Length}");
    JsonElement? rowIdTwo = null;
    JsonElement? rowIdOne = null;
    foreach (string line in rows)
    {
        using JsonDocument parsed = JsonDocument.Parse(line);
        int id = parsed.RootElement.GetProperty("id").GetInt32();
        if (id == 2)
        {
            rowIdTwo = JsonDocument.Parse(line).RootElement.Clone();
        }
        if (id == 1)
        {
            rowIdOne = JsonDocument.Parse(line).RootElement.Clone();
        }
    }
    Check(
        rowIdTwo is not null
            && rowIdTwo.Value.GetProperty("status").ValueKind == JsonValueKind.Null,
        "generated output: Apache tooling must observe the null status of row 2 unchanged"
    );
    Check(
        rowIdOne is not null && rowIdOne.Value.GetProperty("amount").GetInt64() == 1234567,
        "generated output: Apache tooling must observe the canonical DECIMAL(18,4) as unscaled 1234567"
    );
}

// Negative corpus: malformed files derived deterministically from a known-valid fixture. Each
// variant must fail with the documented error class, never silently, and never with exit 0.
string corpusSource = Path.GetFullPath(corpusBase, root);
string corpusDir = Path.Combine(outputDir, "corpus");
Directory.CreateDirectory(corpusDir);

byte[] original = File.ReadAllBytes(corpusSource);
ArchiveSource(corpusSource, "corpus-base.source.bin");

string badMagic = Path.Combine(corpusDir, "bad-footer-magic.parquet");
{
    byte[] bytes = (byte[])original.Clone();
    "XXXX"u8.CopyTo(bytes.AsSpan()[^4..]);
    File.WriteAllBytes(badMagic, bytes);
}

string badLength = Path.Combine(corpusDir, "bad-footer-length.parquet");
{
    byte[] bytes = (byte[])original.Clone();
    ((ReadOnlySpan<byte>)[0xff, 0xff, 0xff, 0x7f]).CopyTo(bytes.AsSpan()[^8..^4]);
    File.WriteAllBytes(badLength, bytes);
}

string truncated = Path.Combine(corpusDir, "truncated.parquet");
File.WriteAllBytes(truncated, original[..(original.Length / 2)]);

string noise = Path.Combine(corpusDir, "noise.parquet");
{
    byte[] bytes = new byte[4096];
    for (int i = 0; i < bytes.Length; i++)
    {
        bytes[i] = (byte)((i * 7) + 13);
    }
    File.WriteAllBytes(noise, bytes);
}

foreach (
    (string file, string signature) in new (string, string)[]
    {
        (badMagic, "is not a Parquet file"),
        (badLength, "footer"),
        (truncated, "footer"),
        (noise, "footer"),
    }
)
{
    (string output, int exit) = RunTool(file, "meta");
    string evidenceName = $"corpus-{Path.GetFileNameWithoutExtension(file)}.meta.txt";
    WriteEvidence(evidenceName, output);
    Check(exit != 0, $"{evidenceName}: malformed file must not be accepted");
    Check(
        output.Contains(signature, StringComparison.OrdinalIgnoreCase),
        $"{evidenceName}: expected predictable failure containing '{signature}'"
    );
}

Console.WriteLine(
    failures.Count == 0
        ? $"Apache conformance OK: {checks} checks passed (evidence in {outputDir})"
        : $"Apache conformance FAILED: {failures.Count} of {checks} checks failed (evidence in {outputDir})"
);
return failures.Count == 0 ? 0 : 1;

void RestoreToolchain(string directory)
{
    using var http = new HttpClient();
    foreach (JsonElement artifact in artifacts)
    {
        string file = artifact.GetProperty("file").GetString()!;
        string path = artifact.GetProperty("path").GetString()!;
        string sha256 = artifact.GetProperty("sha256").GetString()!;
        string destination = Path.Combine(directory, file);
        if (File.Exists(destination) && ComputeSha256(destination) == sha256)
        {
            Console.WriteLine($"up to date: {file}");
            continue;
        }
        Console.WriteLine($"downloading: {file}");
        byte[] bytes = http.GetByteArrayAsync($"{repository}/{path}").GetAwaiter().GetResult();
        string actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{file}: downloaded bytes do not match pinned SHA-256"
            );
        }
        File.WriteAllBytes(destination, bytes);
        Console.WriteLine($"verified: {file} sha256 {actual.ToLowerInvariant()}");
    }
}

void VerifyArtifactSha(string directory, JsonElement artifact)
{
    string file = artifact.GetProperty("file").GetString()!;
    string expected = artifact.GetProperty("sha256").GetString()!;
    string destination = Path.Combine(directory, file);
    if (!File.Exists(destination))
    {
        throw new InvalidOperationException(
            $"{file} is missing from {directory}; run --restore first."
        );
    }
    string actual = ComputeSha256(destination);
    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"{file}: SHA-256 {actual} does not match pinned {expected}"
        );
    }
}

static string ComputeSha256(string file)
{
    using SHA256 sha = SHA256.Create();
    using FileStream stream = File.OpenRead(file);
    return Convert.ToHexString(sha.ComputeHash(stream));
}

static List<SchemaField> ParseSchemaLeafColumns(string metaOutput)
{
    var fields = new List<SchemaField>();
    int depth = 0;
    foreach (string raw in metaOutput.Split('\n'))
    {
        string line = raw.TrimEnd('\r');
        string trimmed = line.Trim();
        bool closes = trimmed == "}";
        Match match = Regex.Match(
            trimmed,
            @"^(required|optional|repeated)\s+(\S+)\s+(\w+)(?:\s+\(([^()]*(?:\([^()]*\)[^()]*)*)\))?.*;$"
        );
        if (depth >= 1 && match.Success)
        {
            fields.Add(
                new SchemaField(
                    match.Groups[3].Value,
                    match.Groups[1].Value,
                    match.Groups[2].Value,
                    match.Groups[4].Success ? match.Groups[4].Value : null
                )
            );
        }
        if (trimmed.StartsWith("message ", StringComparison.Ordinal) || trimmed.EndsWith("{"))
        {
            depth++;
        }
        if (closes)
        {
            depth--;
        }
    }
    return fields;
}

static string RequiredOption(string[] arguments, string option) =>
    Option(arguments, option) ?? throw new ArgumentException($"Missing required option {option}.");

static string? Option(string[] arguments, string option)
{
    int index = Array.IndexOf(arguments, option);
    return index < 0 || index + 1 >= arguments.Length ? null : arguments[index + 1];
}

static ProcessResult Run(string executable, string[] arguments)
{
    var startInfo = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process =
        Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start {executable}.");
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    string output = stdout + Environment.NewLine + stderr;
    return new ProcessResult(output, process.ExitCode);
}

record SchemaField(string Name, string Repetition, string Physical, string? Annotation);

record ProcessResult(string Output, int ExitCode);
