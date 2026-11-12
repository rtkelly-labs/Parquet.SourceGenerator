#pragma warning disable CA1305, CA1852, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

// -----------------------------------------------------------------------------
// TestResultsDiffraPayload.cs
//
// Turns the test job's TRX reports into Diffra's `diffra/test-results@1` payload shape and a
// `collection-v1` document that says which of the expected reports were collected. It runs in
// shadow: `scripts/VerifyRequiredTestExecution.cs` stays the check that decides the job, and
// these files are uploaded beside it. The point is to try Diffra's schemas on real test data
// and to record where they do not fit; docs/internals/diffra-shadow-payloads.md has the design
// and the list of misfits. The schemas are vendored under diffra/schemas/.
//
// One payload per report, because a payload holds one stratum and the three reports are three
// separate test runs:
//
//   test-results.<suite>.json   { scope, methodology, stratum, plan, metrics.tests.results }
//   collection.json             every expected suite as collected, missing or failed
//   summary.md                  counts that show how the data fits the schema
//
// A result is keyed by its TRX test name, prefixed with the class name when the name does not
// already start with it. Two results with one key keep the worst outcome (error, then success,
// then skipped). Durations are whole nanoseconds as decimal strings.
//
// Usage:
//   dotnet run scripts/TestResultsDiffraPayload.cs -- <output-dir> --commit <sha>
//       --input <suite>=<report.trx> [--input <suite>=<report.trx> ...]
//       [--project <diffra.project.yml>] [--repository <owner/name>]
//
// Exit codes:
//   0  the payloads and the collection document were written (the collection may be incomplete)
//   1  no report could be converted, or the project file is unreadable; nothing is written
//   2  usage error
// -----------------------------------------------------------------------------

const string StandardType = "diffra/test-results@1";
const string Methodology = StandardType + ";parquet-sourcegenerator.prototype.trx@0.1.0;trx@2010";
const string CollectionSchema = "https://diffra.dev/schemas/collection-v1.schema.json";
const string ProducerId = "parquet-sourcegenerator.test-results-diffra-payload";
const string ProducerVersion = "0.1.0";

string? outputDir = null;
string? commit = null;
string projectPath = "diffra.project.yml";
string repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")
    is { Length: > 0 } fromEnvironment
    ? fromEnvironment
    : "rtkelly-labs/Parquet.SourceGenerator";
var inputs = new List<(string Suite, string Path)>();

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    if (arg is "--commit" or "--input" or "--project" or "--repository")
    {
        if (i + 1 >= args.Length)
        {
            return Usage($"{arg} needs a value.");
        }

        string value = args[++i];
        switch (arg)
        {
            case "--commit":
                commit = value;
                break;
            case "--project":
                projectPath = value;
                break;
            case "--repository":
                repository = value;
                break;
            default:
                int separator = value.IndexOf('=');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    return Usage($"--input takes <suite>=<report.trx>, not '{value}'.");
                }

                inputs.Add((value[..separator], value[(separator + 1)..]));
                break;
        }
    }
    else if (arg.StartsWith("--", StringComparison.Ordinal))
    {
        return Usage($"Unknown option '{arg}'.");
    }
    else if (outputDir is null)
    {
        outputDir = arg;
    }
    else
    {
        return Usage($"Unexpected argument '{arg}'.");
    }
}

if (outputDir is null)
{
    return Usage("The output directory is missing.");
}

if (commit is null || !Regex.IsMatch(commit, "^[0-9a-f]{40}$"))
{
    return Usage("--commit takes the full 40-character commit SHA.");
}

if (inputs.Count == 0)
{
    return Usage("At least one --input is needed.");
}

foreach ((string suite, _) in inputs)
{
    if (!Regex.IsMatch(suite, "^[a-z][a-z0-9-]*$"))
    {
        return Usage($"Suite '{suite}' must be lower-case letters, digits and hyphens.");
    }
}

if (inputs.Select(input => input.Suite).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
{
    return Usage("Each suite may be given once.");
}

if (!File.Exists(projectPath))
{
    Console.Error.WriteLine($"The project file '{projectPath}' does not exist.");
    return 1;
}

byte[] projectBytes = File.ReadAllBytes(projectPath);
Match scopeLine = Regex.Match(
    Encoding.UTF8.GetString(projectBytes),
    @"^scope:\s*(?<scope>[a-z0-9][a-z0-9._/-]*)\s*$",
    RegexOptions.Multiline
);
if (!scopeLine.Success)
{
    Console.Error.WriteLine($"The project file '{projectPath}' declares no scope.");
    return 1;
}

string scope = scopeLine.Groups["scope"].Value;
string projectDigest = Digest(projectBytes);
string projectRelativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), projectPath)
    .Replace('\\', '/');

// A TRX report records the machine name and nothing else about where it ran, so the operating
// system and architecture are those of the machine running this script.
string os =
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
    : "linux";
string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

var suites = new List<SuiteOutcome>();
foreach ((string suite, string path) in inputs)
{
    suites.Add(ConvertReport(suite, path));
}

int collected = suites.Count(s => s.Status == "collected");
if (collected == 0)
{
    // collection-v1 needs at least one collected stratum, so "nothing was collected" cannot be
    // written down. Say so and write nothing.
    foreach (SuiteOutcome suite in suites)
    {
        Console.Error.WriteLine($"{suite.Suite}: {suite.Status}: {suite.Reason}");
    }

    Console.Error.WriteLine("No TRX report could be converted; nothing was written.");
    return 1;
}

string status = collected == suites.Count ? "complete" : "incomplete";
string collectionId = Digest(WriteCollection(id: null));
byte[] collection = WriteCollection(collectionId);

Directory.CreateDirectory(outputDir);
foreach (SuiteOutcome suite in suites.Where(s => s.Payload is not null))
{
    File.WriteAllBytes(Path.Combine(outputDir, $"test-results.{suite.Suite}.json"), suite.Payload!);
}

File.WriteAllBytes(Path.Combine(outputDir, "collection.json"), collection);
File.WriteAllText(Path.Combine(outputDir, "summary.md"), Summary());

foreach (SuiteOutcome suite in suites)
{
    Console.WriteLine(
        suite.Stats is { } stats
            ? $"{suite.Suite}: collected, {stats.Keys} key(s) from {stats.Results} result(s)"
            : $"{suite.Suite}: {suite.Status}: {suite.Reason}"
    );
}

int examined = suites.Sum(s => s.Stats?.Results ?? 0);
Console.WriteLine(
    $"Examined {examined} test result(s) from {collected} of {suites.Count} TRX report(s); "
        + $"wrote {collected} payload(s) and a collection document ({status}) to {outputDir}"
);
return 0;

SuiteOutcome ConvertReport(string suite, string path)
{
    if (!File.Exists(path))
    {
        return SuiteOutcome.NotCollected(suite, path, "missing", "The TRX report was not found.");
    }

    XDocument document;
    try
    {
        document = XDocument.Load(path);
    }
    catch (Exception exception) when (exception is XmlException or IOException)
    {
        return SuiteOutcome.NotCollected(
            suite,
            path,
            "failed",
            $"The TRX report could not be read: {OneLine(exception.Message)}"
        );
    }

    var definitions = new Dictionary<string, (string ClassName, string Name)>(
        StringComparer.Ordinal
    );
    foreach (XElement unitTest in document.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
    {
        string? id = (string?)unitTest.Attribute("id");
        if (string.IsNullOrEmpty(id))
        {
            continue;
        }

        XElement? method = unitTest
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "TestMethod");
        definitions[id] = (
            (string?)method?.Attribute("className") ?? "",
            (string?)unitTest.Attribute("name") ?? (string?)method?.Attribute("name") ?? ""
        );
    }

    var stats = new SuiteStats();
    stats.Definitions = definitions.Count;
    var results = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
    var resultsPerDefinition = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (
        XElement result in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult")
    )
    {
        stats.Results++;
        string testId = (string?)result.Attribute("testId") ?? "";
        string testName = (string?)result.Attribute("testName") ?? "";
        string outcome = (string?)result.Attribute("outcome") ?? "";
        stats.Outcomes[outcome] = stats.Outcomes.GetValueOrDefault(outcome) + 1;

        string className = "";
        if (definitions.TryGetValue(testId, out (string ClassName, string Name) definition))
        {
            className = definition.ClassName;
            resultsPerDefinition[testId] = resultsPerDefinition.GetValueOrDefault(testId) + 1;
            if (testName.Length == 0)
            {
                testName = definition.Name;
            }
        }
        else
        {
            stats.ResultsWithoutDefinition++;
        }

        if (testName.Length == 0)
        {
            stats.ResultsWithoutName++;
            continue;
        }

        string key = Key(className, testName);
        string escapedKey = EscapeControlCharacters(key);
        if (!string.Equals(key, escapedKey, StringComparison.Ordinal))
        {
            stats.KeysWithControlCharacters++;
        }

        string tag = outcome switch
        {
            "Passed" => "success",
            "Failed" or "Error" or "Aborted" or "Timeout" => "error",
            _ => "skipped",
        };

        string duration = "0";
        if (tag != "skipped")
        {
            if (Nanoseconds((string?)result.Attribute("duration")) is { } nanoseconds)
            {
                duration = nanoseconds;
            }
            else
            {
                stats.MissingDurations++;
            }
        }

        string message =
            (string?)
                result
                    .Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Message" && IsErrorInfo(e.Parent))
            ?? "";
        if (message.Length == 0 && tag != "success")
        {
            // Both tags need text, and a TRX result may carry none: fall back to the outcome.
            message = outcome.Length == 0 ? "No outcome recorded." : outcome;
        }

        stats.LongestMessage = Math.Max(stats.LongestMessage, message.Length);
        if (message.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            stats.MessagesWithControlCharacters++;
        }

        if (message.Length > SuiteStats.MaxText)
        {
            stats.TruncatedMessages++;
            int length = SuiteStats.MaxText;
            if (char.IsHighSurrogate(message[length - 1]))
            {
                length--;
            }

            message = message[..length];
        }

        var entry = new Entry(
            tag,
            duration,
            tag == "error" ? (outcome == "Timeout" ? "timeout" : "exception") : null,
            message,
            Occurrences: 1
        );
        if (results.TryGetValue(escapedKey, out Entry? existing))
        {
            Entry worst = Rank(entry.Tag) > Rank(existing.Tag) ? entry : existing;
            results[escapedKey] = worst with { Occurrences = existing.Occurrences + 1 };
        }
        else
        {
            results[escapedKey] = entry;
        }
    }

    if (results.Count == 0)
    {
        return SuiteOutcome.NotCollected(
            suite,
            path,
            "failed",
            "The TRX report holds no test result."
        );
    }

    // A TRX report has no plan of its own: the nearest thing is a definition with no result.
    var notRun = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach ((string id, (string className, string name)) in definitions)
    {
        if (resultsPerDefinition.ContainsKey(id) || name.Length == 0)
        {
            continue;
        }

        string key = EscapeControlCharacters(Key(className, name));
        if (!results.ContainsKey(key))
        {
            notRun[key] = "The TRX report defines this test and records no result for it.";
        }
    }

    stats.Keys = results.Count;
    stats.DefinitionsWithoutResult = notRun.Count;
    stats.DuplicateKeys = results.Values.Count(entry => entry.Occurrences > 1);
    stats.DataDrivenKeys = results.Keys.Count(key => key.Contains('('));
    stats.LongestKey = results.Keys.Max(key => key.Length);
    stats.DefinitionsWithSeveralResults = resultsPerDefinition.Values.Count(count => count > 1);
    stats.MostResultsForOneDefinition =
        resultsPerDefinition.Count == 0 ? 0 : resultsPerDefinition.Values.Max();

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteString("scope", scope);
        writer.WriteString("methodology", Methodology);
        writer.WritePropertyName("stratum");
        WriteStratum(writer, suite);

        writer.WriteStartObject("plan");
        writer.WriteStartObject("tests");
        writer.WriteStartArray("keys");
        foreach (string key in results.Keys.Concat(notRun.Keys).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(key);
        }

        writer.WriteEndArray();
        if (notRun.Count > 0)
        {
            writer.WriteStartObject("failed");
            foreach ((string key, string reason) in notRun)
            {
                writer.WriteString(key, reason);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject("metrics");
        writer.WriteStartObject("tests");
        writer.WriteStartObject("results");
        foreach ((string key, Entry entry) in results)
        {
            writer.WriteStartObject(key);
            writer.WriteStartObject("value");
            writer.WriteString("tag", entry.Tag);
            switch (entry.Tag)
            {
                case "success":
                    writer.WriteString("duration", entry.Duration);
                    break;
                case "error":
                    writer.WriteString("duration", entry.Duration);
                    writer.WriteString("kind", entry.Kind);
                    writer.WriteString("message", entry.Text);
                    break;
                default:
                    writer.WriteString("reason", entry.Text);
                    break;
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    stream.WriteByte((byte)'\n');
    byte[] payload = stream.ToArray();
    return new SuiteOutcome(suite, path, "collected", null, Digest(payload), payload, stats);
}

byte[] WriteCollection(string? id)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteString("schema", CollectionSchema);
        if (id is not null)
        {
            // The id is the SHA-256 of this document written without the id.
            writer.WriteString("id", id);
        }

        writer.WriteString("status", status);

        writer.WriteStartObject("subject");
        writer.WriteString("kind", "git-repository");
        writer.WriteString("id", $"repo:{repository}");
        writer.WriteString("commit", commit);
        writer.WriteString("repository", $"https://github.com/{repository}");
        writer.WriteEndObject();

        writer.WriteStartObject("project");
        writer.WriteString("scope", scope);
        writer.WriteString("digest", projectDigest);
        writer.WriteStartObject("loadedFrom");
        writer.WriteString("role", "subject");
        writer.WriteString("commit", commit);
        writer.WriteString("path", projectRelativePath);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartArray("metrics");
        writer.WriteStartObject();
        writer.WriteString("metric", "tests");
        writer.WriteString("field", "results");
        writer.WriteString("type", StandardType);
        writer.WriteBoolean("required", true);
        writer.WriteString("methodology", Methodology);
        writer.WriteStartArray("strata");
        foreach (SuiteOutcome suite in suites)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("stratum");
            WriteStratum(writer, suite.Suite);
            writer.WriteString("status", suite.Status);
            if (suite.EvidenceId is not null)
            {
                writer.WriteString("evidenceId", suite.EvidenceId);
            }

            if (suite.Reason is not null)
            {
                writer.WriteString("reason", suite.Reason);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteStartObject("provenance");
        writer.WriteString("producerId", ProducerId);
        writer.WriteString("producerVersion", ProducerVersion);
        writer.WriteString("source", $"git:{commit}");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    stream.WriteByte((byte)'\n');
    return stream.ToArray();
}

void WriteStratum(Utf8JsonWriter writer, string suite)
{
    writer.WriteStartObject();
    writer.WriteString("arch", arch);
    writer.WriteString("os", os);
    writer.WriteString("suite", suite);
    writer.WriteEndObject();
}

string Summary()
{
    var text = new StringBuilder();
    text.AppendLine("### Diffra test-results payloads (shadow, gates nothing)");
    text.AppendLine();
    text.AppendLine($"Collection status: **{status}** ({collected} of {suites.Count} suites).");
    text.AppendLine();
    text.AppendLine(
        "| Suite | Status | Results | Keys | Data-driven keys | Duplicate keys | Longest key | "
            + "Definitions | Definitions with several results | Most results for one definition | "
            + "Definitions without result | Results without definition | Missing durations | "
            + "Longest message | Truncated messages | Control characters (keys / messages) | "
            + "Outcomes |"
    );
    text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (SuiteOutcome suite in suites)
    {
        if (suite.Stats is not { } s)
        {
            text.AppendLine(
                $"| {suite.Suite} | {suite.Status}: {suite.Reason} | | | | | | | | | | | | | | | |"
            );
            continue;
        }

        string outcomes = string.Join(
            ", ",
            s.Outcomes.Select(pair =>
                $"{(pair.Key.Length == 0 ? "(none)" : pair.Key)} {pair.Value}"
            )
        );
        text.AppendLine(
            $"| {suite.Suite} | collected | {s.Results} | {s.Keys} | {s.DataDrivenKeys} | "
                + $"{s.DuplicateKeys} | {s.LongestKey} | {s.Definitions} | "
                + $"{s.DefinitionsWithSeveralResults} | {s.MostResultsForOneDefinition} | "
                + $"{s.DefinitionsWithoutResult} | {s.ResultsWithoutDefinition} | "
                + $"{s.MissingDurations} | {s.LongestMessage} | {s.TruncatedMessages} | "
                + $"{s.KeysWithControlCharacters} / {s.MessagesWithControlCharacters} | "
                + $"{outcomes} |"
        );
    }

    return text.ToString();
}

static string Key(string className, string testName) =>
    className.Length == 0 || testName.StartsWith(className + ".", StringComparison.Ordinal)
        ? testName
        : className + "." + testName;

static bool IsErrorInfo(XElement? element) => element?.Name.LocalName == "ErrorInfo";

static int Rank(string tag) =>
    tag switch
    {
        "error" => 2,
        "success" => 1,
        _ => 0,
    };

// TRX durations are hh:mm:ss.fffffff, so 100 ns is the finest step there is.
static string? Nanoseconds(string? duration) =>
    TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out TimeSpan parsed)
    && parsed >= TimeSpan.Zero
        ? (parsed.Ticks * 100).ToString(CultureInfo.InvariantCulture)
        : null;

static string EscapeControlCharacters(string value)
{
    if (!value.Any(char.IsControl))
    {
        return value;
    }

    var text = new StringBuilder(value.Length + 8);
    foreach (char c in value)
    {
        if (char.IsControl(c))
        {
            text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        else
        {
            text.Append(c);
        }
    }

    return text.ToString();
}

static string OneLine(string value) =>
    value.ReplaceLineEndings(" ").Trim() is { Length: > 0 } text ? text : "no detail";

static string Digest(byte[] bytes) =>
    "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static int Usage(string error)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(
        "Usage: dotnet run scripts/TestResultsDiffraPayload.cs -- <output-dir> --commit <sha> "
            + "--input <suite>=<report.trx> [--input ...] [--project <diffra.project.yml>] "
            + "[--repository <owner/name>]"
    );
    return 2;
}

sealed record Entry(string Tag, string Duration, string? Kind, string Text, int Occurrences);

sealed record SuiteOutcome(
    string Suite,
    string Path,
    string Status,
    string? Reason,
    string? EvidenceId,
    byte[]? Payload,
    SuiteStats? Stats
)
{
    public static SuiteOutcome NotCollected(
        string suite,
        string path,
        string status,
        string reason
    ) => new(suite, path, status, reason, null, null, null);
}

sealed class SuiteStats
{
    public const int MaxText = 4096;

    public int Definitions { get; set; }
    public int Results { get; set; }
    public int Keys { get; set; }
    public int DataDrivenKeys { get; set; }
    public int DuplicateKeys { get; set; }
    public int LongestKey { get; set; }
    public int DefinitionsWithSeveralResults { get; set; }
    public int MostResultsForOneDefinition { get; set; }
    public int DefinitionsWithoutResult { get; set; }
    public int ResultsWithoutDefinition { get; set; }
    public int ResultsWithoutName { get; set; }
    public int MissingDurations { get; set; }
    public int LongestMessage { get; set; }
    public int TruncatedMessages { get; set; }
    public int KeysWithControlCharacters { get; set; }
    public int MessagesWithControlCharacters { get; set; }
    public SortedDictionary<string, int> Outcomes { get; } = new(StringComparer.Ordinal);
}
