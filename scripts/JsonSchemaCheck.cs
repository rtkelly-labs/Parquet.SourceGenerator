#:package JsonSchema.Net@7.4.0
#pragma warning disable CA1305, CA1852, MA0009, MA0011, MA0047, MA0051

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

// -----------------------------------------------------------------------------
// JsonSchemaCheck.cs
//
// Validates JSON documents against JSON Schema (draft 2020-12) files. The shadow Diffra steps
// use it to check the payloads they emit against the schemas vendored under diffra/schemas/,
// so a payload that drifts from Diffra's shape is reported on the run that produced it.
//
// Usage:
//   dotnet run scripts/JsonSchemaCheck.cs -- --schema <schema.json> <document.json>...
//       [--schema <other-schema.json> <document.json>...]
//
// Each document is checked against the schema named before it.
//
// Exit codes:
//   0  every document is valid
//   1  a document is invalid or unreadable, or a schema is unreadable
//   2  usage error, including no document given
// -----------------------------------------------------------------------------

var options = new EvaluationOptions { OutputFormat = OutputFormat.List };

// Positive control: a validator that accepts everything would pass every payload.
JsonSchema control = JsonSchema.FromText(
    """{ "type": "object", "required": ["a"], "properties": { "a": {} }, "additionalProperties": false }"""
);
if (
    !control.Evaluate(JsonNode.Parse("""{ "a": 1 }"""), options).IsValid
    || control.Evaluate(JsonNode.Parse("""{ "b": 1 }"""), options).IsValid
)
{
    Console.Error.WriteLine("JSON Schema check positive controls failed.");
    return 1;
}

JsonSchema? schema = null;
string schemaPath = "";
int examined = 0;
int invalid = 0;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    if (arg == "--schema")
    {
        if (i + 1 >= args.Length)
        {
            return Usage("--schema needs a value.");
        }

        schemaPath = args[++i];
        try
        {
            schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Console.Error.WriteLine($"{schemaPath}: unreadable schema: {exception.Message}");
            return 1;
        }

        continue;
    }

    if (arg.StartsWith("--", StringComparison.Ordinal))
    {
        return Usage($"Unknown option '{arg}'.");
    }

    if (schema is null)
    {
        return Usage($"'{arg}' has no --schema before it.");
    }

    examined++;
    JsonNode? document;
    try
    {
        document = JsonNode.Parse(File.ReadAllText(arg));
    }
    catch (Exception exception) when (exception is IOException or JsonException)
    {
        invalid++;
        Console.Error.WriteLine($"{arg}: unreadable document: {exception.Message}");
        continue;
    }

    EvaluationResults results = schema.Evaluate(document, options);
    if (results.IsValid)
    {
        Console.WriteLine($"{arg}: valid against {schemaPath}");
        continue;
    }

    invalid++;
    Console.Error.WriteLine($"{arg}: INVALID against {schemaPath}");
    var messages = new List<string>();
    foreach (EvaluationResults detail in results.Details)
    {
        if (detail.Errors is null)
        {
            continue;
        }

        foreach (KeyValuePair<string, string> error in detail.Errors)
        {
            messages.Add($"  {detail.InstanceLocation}: {error.Key}: {error.Value}");
        }
    }

    foreach (string message in messages.Distinct(StringComparer.Ordinal).Take(20))
    {
        Console.Error.WriteLine(message);
    }
}

if (examined == 0)
{
    return Usage("No document was given.");
}

Console.WriteLine($"Examined {examined} document(s); {invalid} invalid.");
return invalid == 0 ? 0 : 1;

static int Usage(string error)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(
        "Usage: dotnet run scripts/JsonSchemaCheck.cs -- --schema <schema.json> <document.json>... "
            + "[--schema <other-schema.json> <document.json>...]"
    );
    return 2;
}
