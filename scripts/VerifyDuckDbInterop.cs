using System.Diagnostics;

string duckDb = RequiredOption(args, "--duckdb");
string generatedPath = RequiredOption(args, "--generated");
string outputPath = RequiredOption(args, "--output");

string generatedSummary = RunDuckDb(
    duckDb,
    $"SELECT count(*)::VARCHAR || '|' || count(*) FILTER (WHERE optional_name IS NULL)::VARCHAR || '|' || min(id)::VARCHAR || '|' || max(id)::VARCHAR || '|' || sum(amount)::VARCHAR || '|' || max(octet_length(payload))::VARCHAR || '|' || min(epoch_us(timestamp))::VARCHAR || '|' || max(epoch_us(timestamp))::VARCHAR FROM read_parquet('{SqlPath(generatedPath)}')"
);
AssertEqual(
    "3|1|1|3|123.4566|3|1718454600123000|1718627400789000",
    generatedSummary,
    "generated C# output queried by DuckDB"
);

string outputDirectory = Path.GetDirectoryName(outputPath) ?? ".";
Directory.CreateDirectory(outputDirectory);
string createSql =
    "CREATE TEMP TABLE interop ("
    + "id INTEGER NOT NULL, "
    + "required_name VARCHAR NOT NULL, "
    + "optional_name VARCHAR, "
    + "payload BLOB, "
    + "amount DECIMAL(18, 4) NOT NULL, "
    + "timestamp TIMESTAMP NOT NULL, "
    + "status INTEGER"
    + ");"
    + " INSERT INTO interop VALUES "
    + "(1, 'one', ''::VARCHAR, ''::BLOB, CAST('123.4567' AS DECIMAL(18,4)), TIMESTAMP '2024-06-15 12:30:00.123', 1),"
    + "(2, 'two', NULL::VARCHAR, NULL::BLOB, CAST('-0.0001' AS DECIMAL(18,4)), TIMESTAMP '2024-06-16 12:30:00.456', NULL::INTEGER),"
    + "(3, 'three', 'three', from_hex('0001ff'), CAST('0.0000' AS DECIMAL(18,4)), TIMESTAMP '2024-06-17 12:30:00.789', 2);"
    + $" COPY interop TO '{SqlPath(outputPath)}' (FORMAT PARQUET, COMPRESSION 'SNAPPY', ROW_GROUP_SIZE 2);";
RunDuckDb(duckDb, createSql);
Console.WriteLine($"DuckDB {RunDuckDb(duckDb, "SELECT version()")} created {outputPath}");

static string RequiredOption(string[] arguments, string option)
{
    int index = Array.IndexOf(arguments, option);
    if (
        index < 0
        || index + 1 >= arguments.Length
        || string.IsNullOrWhiteSpace(arguments[index + 1])
    )
    {
        throw new ArgumentException($"Missing required option {option}.");
    }

    return arguments[index + 1];
}

static string SqlPath(string path) => path.Replace("'", "''", StringComparison.Ordinal);

static string RunDuckDb(string executable, string sql)
{
    var startInfo = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("-csv");
    startInfo.ArgumentList.Add("-noheader");
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add(sql);

    using var process =
        Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start DuckDB.");
    string stdout = process.StandardOutput.ReadToEnd().Trim();
    string stderr = process.StandardError.ReadToEnd().Trim();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"DuckDB failed ({process.ExitCode}): {stderr}");
    }

    return stdout;
}

static void AssertEqual(string expected, string actual, string context)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{context}: expected '{expected}', actual '{actual}'");
    }
}
