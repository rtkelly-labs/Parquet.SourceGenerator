#:package CliWrap@3.7.1

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

// Apache Parquet conformance verifier for issue #167.
//
// Modes:
//   --restore <dir>
//       Downloads the pinned toolchain described by test/data/apache-conformance.json into <dir>
//       and verifies every SHA-256 (existing files with matching hashes are kept, so a cached
//       tools dir costs no network). Each artifact is fetched from the manifest's ordered
//       "mirrors" list: every round tries each mirror in turn, and failed rounds are retried with
//       exponential backoff. Bytes are written only after they match the pinned SHA-256, whichever
//       mirror served them. When every source fails, one ::error:: line per artifact names the
//       artifact, every URL tried and its outcome, and the exit code is 1.
//       --attempts <n>           rounds over the mirror list per artifact (default 3)
//       --retry-delay-ms <ms>    backoff before round 2; doubles each round (default 2000)
//       --download-timeout-seconds <s>
//                                deadline for one download, headers AND body (default 300); a
//                                mirror that stalls mid-body counts as a failed attempt
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
JsonElement[] artifacts = toolManifest
    .RootElement.GetProperty("artifacts")
    .EnumerateArray()
    .ToArray();

if (restoreMode)
{
    string toolsDir = RequiredOption(args, "--restore");
    Directory.CreateDirectory(toolsDir);
    return RestoreToolchain(toolsDir);
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

using JsonDocument fixtureManifest = JsonDocument.Parse(
    File.ReadAllText(
        Path.Combine(root, Option(args, "--fixture-manifest") ?? "test/data/fixture-manifest.json")
    )
);

// Examined-N guard (#436): the verdict below is only meaningful if the fixtures it speaks for were
// all examined. A fixture that cannot be found used to be skipped, so a moved directory or a
// checkout without the files reported "OK" having verified nothing. The v1 fixtures are real files
// in the repository (not LFS pointers), so a missing one is a path problem, never an expected skip.
// This runs before any Java process is started, so the refusal does not depend on the toolchain.
JsonElement[] manifestFixtures = fixtureManifest
    .RootElement.GetProperty("fixtures")
    .EnumerateArray()
    .ToArray();
if (manifestFixtures.Length == 0)
{
    Console.Error.WriteLine(
        "Apache conformance FAILED: the fixture manifest lists no fixtures; refusing to report OK."
    );
    return 1;
}

string[] missingFixtures = manifestFixtures
    .Select(f => f.GetProperty("path").GetString()!)
    .Where(p => !File.Exists(Path.GetFullPath(p, root)))
    .ToArray();
if (missingFixtures.Length > 0)
{
    foreach (string missing in missingFixtures)
    {
        Console.Error.WriteLine($"MISSING fixture: {missing}");
    }
    Console.Error.WriteLine(
        $"Apache conformance FAILED: {missingFixtures.Length} of {manifestFixtures.Length} manifest fixtures do not exist under {root}; refusing to report OK."
    );
    return 1;
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

string corpusBase =
    Option(args, "--corpus-base") ?? "test/data/v1/01_small_flat_primitives.parquet";

int fixturesVerified = 0;
foreach (JsonElement fixture in manifestFixtures)
{
    string relativePath = fixture.GetProperty("path").GetString()!;
    string fullPath = Path.GetFullPath(relativePath, root);
    fixturesVerified++;

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

    long rows = long.Parse(
        fixture.GetProperty("rowCount").ToString()!.Trim('"'),
        CultureInfo.InvariantCulture
    );
    int groups = int.Parse(
        fixture.GetProperty("rowGroupCount").ToString()!.Trim('"'),
        CultureInfo.InvariantCulture
    );
    int columns = int.Parse(
        fixture.GetProperty("columnCount").ToString()!.Trim('"'),
        CultureInfo.InvariantCulture
    );

    int observedGroups = Regex
        .Matches(meta, @"^Row group \d+:\s+count:", RegexOptions.Multiline)
        .Count;
    long observedRows = Regex
        .Matches(meta, @"^Row group \d+:\s+count:\s+(\d+)", RegexOptions.Multiline)
        .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
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

if (fixturesVerified != manifestFixtures.Length)
{
    failures.Add($"examined {fixturesVerified} of {manifestFixtures.Length} manifest fixtures");
}

Console.WriteLine(
    failures.Count == 0
        ? $"Apache conformance OK: {fixturesVerified}/{manifestFixtures.Length} fixtures, {checks} checks passed (evidence in {outputDir})"
        : $"Apache conformance FAILED: {failures.Count} of {checks} checks failed (evidence in {outputDir})"
);
return failures.Count == 0 ? 0 : 1;

int RestoreToolchain(string directory)
{
    string[] mirrors = ReadMirrors(toolManifest.RootElement);
    int attempts = int.Parse(Option(args, "--attempts") ?? "3", CultureInfo.InvariantCulture);
    int baseDelayMs = int.Parse(
        Option(args, "--retry-delay-ms") ?? "2000",
        CultureInfo.InvariantCulture
    );
    int timeoutSeconds = int.Parse(
        Option(args, "--download-timeout-seconds") ?? "300",
        CultureInfo.InvariantCulture
    );
    if (attempts < 1 || baseDelayMs < 0 || timeoutSeconds < 1)
    {
        throw new ArgumentException(
            "--attempts and --download-timeout-seconds must be >= 1 and --retry-delay-ms >= 0."
        );
    }
    var downloadTimeout = TimeSpan.FromSeconds(timeoutSeconds);

    // HttpClient.Timeout stops applying once ResponseHeadersRead returns, so the per-download
    // deadline in TryDownload is what bounds the body; this one is disabled to keep a single owner.
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    int failed = 0;
    foreach (JsonElement artifact in artifacts)
    {
        string file = artifact.GetProperty("file").GetString()!;
        string path = artifact.GetProperty("path").GetString()!;
        string sha256 = artifact.GetProperty("sha256").GetString()!;
        string destination = Path.Combine(directory, file);
        if (
            File.Exists(destination)
            && string.Equals(ComputeSha256(destination), sha256, StringComparison.OrdinalIgnoreCase)
        )
        {
            Console.WriteLine($"up to date: {file}");
            continue;
        }

        var tried = new List<string>();
        bool restored = false;
        for (int round = 1; round <= attempts && !restored; round++)
        {
            if (round > 1)
            {
                int delay = baseDelayMs * (1 << Math.Min(round - 2, 10));
                Console.WriteLine($"retrying {file} in {delay} ms (round {round}/{attempts})");
                Thread.Sleep(delay);
            }
            foreach (string mirror in mirrors)
            {
                string url = $"{mirror.TrimEnd('/')}/{path}";
                Console.WriteLine($"downloading: {file} from {url}");
                string outcome = TryDownload(http, url, destination, sha256, downloadTimeout);
                tried.Add($"{url} -> {outcome} (round {round})");
                if (outcome == "OK")
                {
                    Console.WriteLine($"verified: {file} sha256 {sha256.ToLowerInvariant()}");
                    restored = true;
                    break;
                }
                Console.WriteLine($"  failed: {outcome}");
            }
        }

        if (!restored)
        {
            failed++;
            // One workflow command per artifact; the message must stay on one line.
            Console.WriteLine(
                $"::error title=Apache conformance tool restore failed::{file} ({path}) could not be restored from any of {mirrors.Length} mirror(s) in {attempts} round(s). Tried: {string.Join("; ", tried)}"
            );
        }
    }

    if (failed > 0)
    {
        Console.WriteLine(
            $"Apache conformance tool restore FAILED: {failed} of {artifacts.Length} artifacts could not be restored with a matching SHA-256."
        );
        return 1;
    }
    Console.WriteLine(
        $"Apache conformance tool restore OK: {artifacts.Length} artifacts verified."
    );
    return 0;
}

// Downloads url into a temporary file beside destination and moves it into place only when its
// SHA-256 matches the pin. Returns "OK" or a one-line description of the failure. The timeout
// covers the whole download: a mirror that sends headers and then stalls must not hang the job.
static string TryDownload(
    HttpClient http,
    string url,
    string destination,
    string sha256,
    TimeSpan timeout
)
{
    string partial = destination + ".partial";
    using var deadline = new CancellationTokenSource(timeout);
    try
    {
        using HttpResponseMessage response = http.Send(
            new HttpRequestMessage(HttpMethod.Get, url),
            HttpCompletionOption.ResponseHeadersRead,
            deadline.Token
        );
        if (!response.IsSuccessStatusCode)
        {
            return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }

        string actual;
        using (Stream body = response.Content.ReadAsStream(deadline.Token))
        using (FileStream output = File.Create(partial))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[81920];
            int read;
            // Stream.Read ignores cancellation; ReadAsync on the response stream honours it.
            while (
                (read = body.ReadAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult())
                > 0
            )
            {
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            actual = Convert.ToHexString(hash.GetHashAndReset());
        }

        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return $"SHA-256 mismatch: got {actual.ToLowerInvariant()}, pinned {sha256.ToLowerInvariant()}";
        }
        File.Move(partial, destination, overwrite: true);
        return "OK";
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        return $"timed out after {timeout.TotalSeconds:0} s";
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException)
    {
        return $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
    }
    finally
    {
        if (File.Exists(partial))
        {
            File.Delete(partial);
        }
    }
}

static string[] ReadMirrors(JsonElement manifest)
{
    string[] mirrors = manifest.TryGetProperty("mirrors", out JsonElement list)
        ? list.EnumerateArray().Select(m => m.GetString()!).ToArray()
        : [];
    if (mirrors.Length == 0)
    {
        throw new InvalidOperationException(
            "The tool manifest lists no mirrors; restore needs at least one repository URL."
        );
    }
    return mirrors;
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
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var result = Cli.Wrap(executable)
        .WithArguments(arguments)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(cts.Token)
        .GetAwaiter()
        .GetResult();

    string output = result.StandardOutput + Environment.NewLine + result.StandardError;
    return new ProcessResult(output, result.ExitCode);
}

record SchemaField(string Name, string Repetition, string Physical, string? Annotation);

record ProcessResult(string Output, int ExitCode);
