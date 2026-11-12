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

// -----------------------------------------------------------------------------
// BenchmarkDiffraPayload.cs
//
// Turns the regression gate's BenchmarkDotNet results into Diffra's `diffra/benchmark@1`
// payload shape and a `collection-v1` document. It runs in shadow: the allocation gate in
// tools/BenchmarkSummaryGenerator stays the one that decides the job, and these files are
// uploaded beside it so the baseline -> budget logic can move to Diffra's standard shape once
// its CLI ships. docs/reference/benchmarks.md has the design; diffra.project.yml declares the
// metric and holds the budgets; the schemas are vendored under diffra/schemas/.
//
//   benchmarks.json   { scope, methodology, stratum, plan, metrics.benchmarks.<field>.<case> }
//   collection.json   every field x stratum as collected
//
// One payload holds five fields, each a map from benchmark case to { value, unit }:
//
//   allocatedBytes                    B per operation
//   gen0PerKOps gen1PerKOps gen2PerKOps   count per 1000 operations
//   meanTime                          ns per operation
//
// A case is Type.Method(Parameters), the identity benchmarks/baseline.json and
// RegressionCheck already use, so a key never changes unless the benchmark does.
//
// GC counts are normalised per 1000 operations (BenchmarkDotNet's own Gen0Collects metric)
// because the raw counts scale with TotalOperations, which BenchmarkDotNet picks from the
// runner's speed: the same code ran 1024 operations on one runner and 2048 on a faster one.
//
// Everything that changes what a number means is in the stratum: the runtime major.minor,
// the OS, the architecture and the processor. The runtime sizes its gen0 budget from the CPU
// cache, so the same code ran 2 to 5 times fewer gen0 collections on a Xeon than on an EPYC;
// the project file therefore compares GC counts and time per processor, and allocations
// across processors. The methodology names only the converter and the BenchmarkDotNet
// major.minor.
//
// Usage: dotnet run scripts/BenchmarkDiffraPayload.cs -- <results-dir> <output-dir> --commit <sha>
//            [--project <diffra.project.yml>] [--repository <owner/name>]
// Exit codes: 0 written, 1 nothing to emit or unreadable results or project file, 2 usage.
// -----------------------------------------------------------------------------

const string StandardType = "diffra/benchmark@1";
const string Converter = "parquet-sourcegenerator.prototype.benchmarkdotnet@0.1.0";
const string CollectionSchema = "https://diffra.dev/schemas/collection-v1.schema.json";
const string ProducerId = "parquet-sourcegenerator.benchmark-diffra-payload";
const string ProducerVersion = "0.1.0";

string? resultsDirectory = null;
string? outputDirectory = null;
string? commit = null;
string projectPath = "diffra.project.yml";
string repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")
    is { Length: > 0 } fromEnvironment
    ? fromEnvironment
    : "rtkelly-labs/Parquet.SourceGenerator";

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--commit" or "--project" or "--repository")
    {
        if (i + 1 >= args.Length)
        {
            return Usage($"{args[i]} needs a value.");
        }

        string option = args[i];
        string value = args[++i];
        if (option == "--commit")
        {
            commit = value;
        }
        else if (option == "--project")
        {
            projectPath = value;
        }
        else
        {
            repository = value;
        }
    }
    else if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        return Usage($"Unknown option '{args[i]}'.");
    }
    else if (resultsDirectory is null)
    {
        resultsDirectory = args[i];
    }
    else if (outputDirectory is null)
    {
        outputDirectory = args[i];
    }
    else
    {
        return Usage($"Unexpected argument '{args[i]}'.");
    }
}

if (resultsDirectory is null || outputDirectory is null)
{
    return Usage("A results directory and an output directory are required.");
}

if (commit is null || !Regex.IsMatch(commit, "^[0-9a-f]{40}$"))
{
    return Usage("--commit takes the full 40-character commit SHA.");
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
string projectRelativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), projectPath)
    .Replace('\\', '/');

string[] files = Directory.Exists(resultsDirectory)
    ? Directory
        .GetFiles(resultsDirectory, "*-report-full-compressed.json")
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray()
    : [];

if (files.Length == 0)
{
    Console.Error.WriteLine(
        $"No BenchmarkDotNet '*-report-full-compressed.json' export in '{resultsDirectory}'. Nothing to emit; an empty payload would compare as 'everything removed'."
    );
    return 1;
}

var cases = new SortedDictionary<string, Case>(StringComparer.Ordinal);
Host? environment = null;

foreach (string file in files)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
    JsonElement root = document.RootElement;

    Host fileEnvironment = ReadHost(root.GetProperty("HostEnvironmentInfo"));

    // One payload describes one run on one machine. Results stitched from two would carry a
    // stratum that is true of neither.
    if (environment is not null && environment != fileEnvironment)
    {
        Console.Error.WriteLine(
            $"'{Path.GetFileName(file)}' was produced by '{fileEnvironment}', but earlier files by '{environment}'."
        );
        return 1;
    }

    environment = fileEnvironment;

    foreach (JsonElement benchmark in root.GetProperty("Benchmarks").EnumerateArray())
    {
        string type = Text(benchmark, "Type");
        string method = Text(benchmark, "Method");
        string parameters = Text(benchmark, "Parameters");
        if (type.Length == 0 || method.Length == 0)
        {
            Console.Error.WriteLine(
                $"A benchmark in '{Path.GetFileName(file)}' has no type or method."
            );
            return 1;
        }

        string key = parameters.Length == 0 ? $"{type}.{method}" : $"{type}.{method}({parameters})";

        // A case that failed, or ran without the memory diagnoser, is an error rather than a
        // missing row: in a payload, a missing row reads as "benchmark removed".
        if (
            !benchmark.TryGetProperty("Statistics", out JsonElement statistics)
            || statistics.ValueKind != JsonValueKind.Object
            || !statistics.TryGetProperty("Mean", out JsonElement mean)
            || mean.ValueKind != JsonValueKind.Number
            || !benchmark.TryGetProperty("Memory", out JsonElement memory)
            || memory.ValueKind != JsonValueKind.Object
        )
        {
            Console.Error.WriteLine(
                $"'{key}' produced no measurement (failed, or run without the memory diagnoser)."
            );
            return 1;
        }

        long operations = memory.GetProperty("TotalOperations").GetInt64();
        long allocated = memory.GetProperty("BytesAllocatedPerOperation").GetInt64();
        if (operations <= 0 || allocated < 0)
        {
            Console.Error.WriteLine(
                $"'{key}' reports {operations} operations and {allocated} bytes per operation."
            );
            return 1;
        }

        var measured = new Case(
            allocated,
            PerThousand(memory.GetProperty("Gen0Collections").GetInt64(), operations),
            PerThousand(memory.GetProperty("Gen1Collections").GetInt64(), operations),
            PerThousand(memory.GetProperty("Gen2Collections").GetInt64(), operations),
            Math.Round((decimal)mean.GetDouble(), 1, MidpointRounding.AwayFromZero)
        );

        if (!cases.TryAdd(key, measured))
        {
            Console.Error.WriteLine($"Two benchmarks share the identity '{key}'.");
            return 1;
        }
    }
}

if (cases.Count == 0)
{
    Console.Error.WriteLine(
        $"The {files.Length} result file(s) in '{resultsDirectory}' hold no benchmark. Nothing to emit."
    );
    return 1;
}

Host host = environment!;
if (host.Processor.Length == 0 || host.Runtime.Length == 0 || host.Architecture.Length == 0)
{
    Console.Error.WriteLine(
        $"The results do not name their runtime, architecture and processor: '{host}'."
    );
    return 1;
}

string methodology = $"{StandardType};{Converter};benchmarkdotnet-json-full@{host.BenchmarkDotNet}";

// The fields, in the order the project file declares them. Allocations compare across
// processors; GC counts and time only within one.
(string Name, string Unit, bool PerProcessor, Func<Case, string> Value)[] fields =
[
    ("allocatedBytes", "B", false, c => c.AllocatedBytes.ToString(CultureInfo.InvariantCulture)),
    ("gen0PerKOps", "count/1000-ops", true, c => Canonical(c.Gen0PerThousand)),
    ("gen1PerKOps", "count/1000-ops", true, c => Canonical(c.Gen1PerThousand)),
    ("gen2PerKOps", "count/1000-ops", true, c => Canonical(c.Gen2PerThousand)),
    ("meanTime", "ns", true, c => Canonical(c.MeanNanoseconds)),
];

byte[] payload = WritePayload();
string evidenceId = Digest(payload);
string collectionId = Digest(WriteCollection(id: null));
byte[] collection = WriteCollection(collectionId);

Directory.CreateDirectory(outputDirectory);
File.WriteAllBytes(Path.Combine(outputDirectory, "benchmarks.json"), payload);
File.WriteAllBytes(Path.Combine(outputDirectory, "collection.json"), collection);

Console.WriteLine(
    $"Examined {cases.Count} benchmark case(s) from {files.Length} file(s); wrote benchmarks.json and collection.json to '{outputDirectory}'."
);
Console.WriteLine($"Methodology: {methodology}");
Console.WriteLine($"Stratum: {host}");
return 0;

byte[] WritePayload()
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteString("scope", scope);
        writer.WriteString("methodology", methodology);
        writer.WritePropertyName("stratum");
        WriteStratum(writer, withProcessor: true);

        writer.WriteStartObject("plan");
        writer.WriteStartObject("benchmarks");
        writer.WriteStartArray("keys");
        foreach (string key in cases.Keys)
        {
            writer.WriteStringValue(key);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject("metrics");
        writer.WriteStartObject("benchmarks");
        foreach ((string name, string unit, _, Func<Case, string> value) in fields)
        {
            writer.WriteStartObject(name);
            foreach ((string key, Case measured) in cases)
            {
                // Every value is a decimal string: Diffra's canonical JSON has no fractions.
                writer.WriteStartObject(key);
                writer.WriteString("value", value(measured));
                writer.WriteString("unit", unit);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    stream.WriteByte((byte)'\n');
    return stream.ToArray();
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

        // A run with a failed case writes nothing (see above), so what is written is complete.
        writer.WriteString("status", "complete");

        writer.WriteStartObject("subject");
        writer.WriteString("kind", "git-repository");
        writer.WriteString("id", $"repo:{repository}");
        writer.WriteString("commit", commit);
        writer.WriteString("repository", $"https://github.com/{repository}");
        writer.WriteEndObject();

        writer.WriteStartObject("project");
        writer.WriteString("scope", scope);
        writer.WriteString("digest", Digest(projectBytes));
        writer.WriteStartObject("loadedFrom");
        writer.WriteString("role", "subject");
        writer.WriteString("commit", commit);
        writer.WriteString("path", projectRelativePath);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartArray("metrics");
        foreach ((string name, _, bool perProcessor, _) in fields)
        {
            writer.WriteStartObject();
            writer.WriteString("metric", "benchmarks");
            writer.WriteString("field", name);
            writer.WriteString("type", StandardType);
            writer.WriteBoolean("required", true);
            writer.WriteString("methodology", methodology);
            writer.WriteStartArray("strata");
            writer.WriteStartObject();
            writer.WritePropertyName("stratum");
            WriteStratum(writer, perProcessor);
            writer.WriteString("status", "collected");
            writer.WriteString("evidenceId", evidenceId);
            writer.WriteStartObject("keys");
            foreach (string key in cases.Keys)
            {
                writer.WriteString(key, "collected");
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

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

void WriteStratum(Utf8JsonWriter writer, bool withProcessor)
{
    writer.WriteStartObject();
    writer.WriteString("arch", host.Architecture);
    if (withProcessor)
    {
        writer.WriteString("cpu", host.Processor);
    }

    writer.WriteString("os", host.Os);
    writer.WriteString("runtime", host.Runtime);
    writer.WriteEndObject();
}

// Diffra's canonical decimal: no exponent, no trailing zero, no trailing point.
static string Canonical(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

static string Digest(byte[] bytes) =>
    "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static decimal PerThousand(long collections, long operations) =>
    Math.Round(collections * 1000m / operations, 6, MidpointRounding.AwayFromZero);

static Host ReadHost(JsonElement info)
{
    // "0.14.0" -> "0.14": the export format follows the minor version.
    string[] benchmarkDotNet = Text(info, "BenchmarkDotNetVersion").Split('.');
    string runtime = Text(info, "RuntimeVersion"); // ".NET 8.0.33 (8.0.3326.52219)"

    // The runtime patch is left out on purpose: the gate's runner moved from 8.0.31 to 8.0.33
    // over five weeks, and a stratum that changed with every patch would never compare.
    string[] version = runtime.Replace(".NET ", "", StringComparison.Ordinal).Split('.', ' ');

    // BenchmarkDotNet records the OS as free text ("Ubuntu 24.04.5 LTS (Noble Numbat)"); the
    // family is what changes allocation sizes, and this script runs on the same machine.
    string os =
        OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsWindows() ? "windows"
        : RuntimeInformation.OSDescription;

    return new Host(
        benchmarkDotNet.Length >= 2 ? $"{benchmarkDotNet[0]}.{benchmarkDotNet[1]}" : "unknown",
        version.Length >= 2 ? $"net{version[0]}.{version[1]}" : runtime,
        os,
        Text(info, "Architecture").ToLowerInvariant(),
        Text(info, "ProcessorName").Trim()
    );
}

static string Text(JsonElement element, string name) =>
    element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;

static int Usage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(
        "Usage: dotnet run scripts/BenchmarkDiffraPayload.cs -- <results-dir> <output-dir> --commit <sha> [--project <diffra.project.yml>] [--repository <owner/name>]"
    );
    return 2;
}

internal sealed record Case(
    long AllocatedBytes,
    decimal Gen0PerThousand,
    decimal Gen1PerThousand,
    decimal Gen2PerThousand,
    decimal MeanNanoseconds
);

internal sealed record Host(
    string BenchmarkDotNet,
    string Runtime,
    string Os,
    string Architecture,
    string Processor
);
