#pragma warning disable CA1305, CA1852, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// -----------------------------------------------------------------------------
// BenchmarkDiffraPayload.cs
//
// Turns the regression gate's BenchmarkDotNet results into Diffra metrics payloads
// ({ scope, methodology, metrics }), the input to `diffra collect`. It runs in shadow:
// the allocation gate in tools/BenchmarkSummaryGenerator stays the one that decides the
// job, and these files are uploaded beside it so the baseline -> budget logic can move to
// Diffra's standard shape once its CLI ships. docs/reference/benchmarks.md has the design.
//
// Three payloads, not one, because a Diffra rules file is one flat set of thresholds
// applied to every measurement in the Delta it evaluates. Allocations, GC counts and time
// need different budgets (benchmarks/diffra/*.rules.json), so each gets its own file:
//
//   allocations.json  /<benchmark>/allocatedBytes                bytes per operation
//   gc.json           /<benchmark>/gen{0,1,2}CollectionsPer1000Ops count per 1000 operations
//   time.json         /<benchmark>/meanNanoseconds               ns per operation
//
// <benchmark> is Type.Method(Parameters), the identity benchmarks/baseline.json and
// RegressionCheck already use, so a key never changes unless the benchmark does.
//
// GC counts are normalised per 1000 operations (BenchmarkDotNet's own Gen0Collects metric)
// because the raw counts scale with TotalOperations, which BenchmarkDotNet picks from the
// runner's speed: the same code ran 1024 operations on one runner and 2048 on a faster one.
//
// The methodology string carries everything that changes what a number means: the
// BenchmarkDotNet version, the runtime major.minor, the OS and the architecture, and for
// time the processor. Diffra compares two payloads only when scope and methodology match
// exactly, so an Arm64 baseline against an X64 run is `incompatible`, not a 6% "change".
//
// Usage: dotnet run scripts/BenchmarkDiffraPayload.cs -- <results-dir> <output-dir> [--scope <scope>]
// Exit codes: 0 written, 1 nothing to emit or unreadable results, 2 usage.
// -----------------------------------------------------------------------------

const string DefaultScope = "parquet-sourcegenerator/benchmarks/regression-gate";
const string MethodologyVersion = "v1";

string? resultsDirectory = null;
string? outputDirectory = null;
string scope = DefaultScope;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--scope" && i + 1 < args.Length)
    {
        scope = args[++i];
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

if (resultsDirectory is null || outputDirectory is null || scope.Length == 0)
{
    return Usage("A results directory and an output directory are required.");
}

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
string? environment = null;
string? processor = null;

foreach (string file in files)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
    JsonElement root = document.RootElement;

    JsonElement host = root.GetProperty("HostEnvironmentInfo");
    string fileEnvironment = Methodology(host);
    string fileProcessor = Text(host, "ProcessorName");

    // One payload describes one run on one machine. Results stitched from two would carry a
    // methodology that is true of neither.
    if (environment is not null && (environment != fileEnvironment || processor != fileProcessor))
    {
        Console.Error.WriteLine(
            $"'{Path.GetFileName(file)}' was produced by '{fileEnvironment}' on '{fileProcessor}', but earlier files by '{environment}' on '{processor}'."
        );
        return 1;
    }

    environment = fileEnvironment;
    processor = fileProcessor;

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

string outputPath = outputDirectory;
Directory.CreateDirectory(outputPath);

Write(
    "allocations.json",
    $"{environment};allocated-bytes-per-op-{MethodologyVersion}",
    c => [("allocatedBytes", Value.Integer(c.AllocatedBytes, "bytes"))]
);
Write(
    "gc.json",
    $"{environment};gc-collections-per-1000-ops-{MethodologyVersion}",
    c =>
        [
            ("gen0CollectionsPer1000Ops", Value.Fraction(c.Gen0PerThousand, "count/1000-ops")),
            ("gen1CollectionsPer1000Ops", Value.Fraction(c.Gen1PerThousand, "count/1000-ops")),
            ("gen2CollectionsPer1000Ops", Value.Fraction(c.Gen2PerThousand, "count/1000-ops")),
        ]
);

// Time is only comparable on the same processor model: the gate's runners span at least
// three EPYC generations and the same code ran up to 2.1x apart across them. With the
// processor in the methodology a cross-model comparison is `incompatible`, never a trend.
Write(
    "time.json",
    $"{environment};{processor};mean-ns-per-op-{MethodologyVersion}",
    c => [("meanNanoseconds", Value.Fraction(c.MeanNanoseconds, "ns"))]
);

Console.WriteLine(
    $"Examined {cases.Count} benchmark case(s) from {files.Length} file(s); wrote allocations.json, gc.json and time.json to '{outputPath}'."
);
Console.WriteLine($"Methodology: {environment}");
return 0;

void Write(string fileName, string methodology, Func<Case, (string Name, Value Value)[]> measures)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteString("scope", scope);
        writer.WriteString("methodology", methodology);
        writer.WriteStartObject("metrics");
        foreach ((string key, Case measured) in cases)
        {
            writer.WriteStartObject(key);
            foreach ((string name, Value value) in measures(measured))
            {
                writer.WriteStartObject(name);
                if (value.Number is { } number)
                {
                    writer.WriteNumber("value", number);
                }
                else
                {
                    // Diffra's canonical JSON accepts only integer number tokens, so anything
                    // that can carry a fraction is a decimal string.
                    writer.WriteString("value", value.Text);
                }

                writer.WriteString("unit", value.Unit);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    File.WriteAllText(
        Path.Combine(outputPath, fileName),
        Encoding.UTF8.GetString(stream.ToArray()) + "\n"
    );
}

static decimal PerThousand(long collections, long operations) =>
    Math.Round(collections * 1000m / operations, 6, MidpointRounding.AwayFromZero);

static string Methodology(JsonElement host)
{
    string benchmarkDotNet = Text(host, "BenchmarkDotNetVersion");
    string runtime = Text(host, "RuntimeVersion"); // ".NET 8.0.33 (8.0.3326.52219)"
    string architecture = Text(host, "Architecture").ToLowerInvariant();

    // The runtime patch is left out on purpose: the gate's runner moved from 8.0.31 to 8.0.33
    // over five weeks, and a methodology that changed with every patch would never compare.
    string[] version = runtime.Replace(".NET ", "", StringComparison.Ordinal).Split('.', ' ');
    string framework = version.Length >= 2 ? $"net{version[0]}.{version[1]}" : runtime;

    // BenchmarkDotNet records the OS as free text ("Ubuntu 24.04.5 LTS (Noble Numbat)"); the
    // family is what changes allocation sizes, and this script runs on the same machine.
    string os =
        OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "osx"
        : OperatingSystem.IsWindows() ? "windows"
        : RuntimeInformation.OSDescription;

    return $"benchmarkdotnet-{benchmarkDotNet};{framework};{os}-{architecture}";
}

static string Text(JsonElement element, string name) =>
    element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;

static int Usage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(
        "Usage: dotnet run scripts/BenchmarkDiffraPayload.cs -- <results-dir> <output-dir> [--scope <scope>]"
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

internal sealed record Value(long? Number, string Text, string Unit)
{
    public static Value Integer(long value, string unit) => new(value, string.Empty, unit);

    public static Value Fraction(decimal value, string unit) =>
        new(null, value.ToString("0.0#####", CultureInfo.InvariantCulture), unit);
}
