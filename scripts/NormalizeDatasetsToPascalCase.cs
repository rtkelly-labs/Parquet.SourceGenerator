#:package Parquet.Net@6.0.3

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;

// -----------------------------------------------------------------------------
// NormalizeDatasetsToPascalCase.cs
// Standalone C# .NET 10 file-based script to normalize public benchmark Parquet
// datasets from raw upstream schemas (snake_case, dot-separated fields) to clean
// PascalCase column names matching C# properties.
//
// RATIONALE:
// In Parquet.Net (v6.0.3+), ParquetSerializer lacks column mapping attributes
// (such as [ParquetColumn]) and only matches reflected properties by exact name
// (or case-insensitively when flags are set). Unmatched columns are skipped with
// zero I/O, skewing reflection baselines artificially fast. Furthermore, in
// environments where IsDynamicCodeSupported is false (e.g. .NET 10 scripting,
// NativeAOT), the expression interpreter throws NotSupportedException on Span<T>
// locals when compiling Dremel field assemblers.
//
// Normalizing datasets to PascalCase ensures both Parquet.Net reflection and
// Parquet.SourceGenerator deserialize identical columns, types, and values,
// enabling fair, honest, and reproducible performance comparisons.
// -----------------------------------------------------------------------------

string dataDir = FindBenchmarkDataDir();
Console.WriteLine($"📦 Normalizing benchmark datasets in: {dataDir}\n");

string[] files = ["tpch_lineitem_sf001.parquet", "adult_census_income.parquet", "diamonds.parquet"];

foreach (string filename in files)
{
    string filePath = Path.Combine(dataDir, filename);
    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"⚠️  Skipping missing file: {filePath}");
        continue;
    }

    Console.WriteLine($"🔄 Normalizing {filename}...");
    string tempOut = filePath + ".normalized.tmp";

    await NormalizeFileAsync(filePath, tempOut);

    // Replace original file with normalized version
    File.Move(tempOut, filePath, overwrite: true);
    long newSize = new FileInfo(filePath).Length;
    Console.WriteLine($"✅ Successfully normalized {filename} ({newSize:N0} bytes)\n");
}

Console.WriteLine("🎉 Dataset normalization complete!");
return 0;

static async Task NormalizeFileAsync(string inputPath, string outputPath)
{
    await using var inStream = File.OpenRead(inputPath);
    await using var reader = await ParquetReader.CreateAsync(inStream);

    var inFields = reader.Schema.GetDataFields();
    var outFields = new List<Field>();

    foreach (var f in inFields)
    {
        string pascalName = ToPascalCase(f.Name);
        if (f is DecimalDataField ddf)
        {
            outFields.Add(new DecimalDataField(pascalName, ddf.Precision, ddf.Scale, isNullable: true));
        }
        else if (f is DateTimeDataField dtf)
        {
            outFields.Add(new DateTimeDataField(pascalName, dtf.DateTimeFormat, dtf.IsAdjustedToUTC, dtf.Unit, isNullable: true));
        }
        else if (f.ClrType == typeof(DateOnly))
        {
            // Normalize DateOnly to DateTime with DateAndTime format for universal serializer compatibility
            outFields.Add(new DateTimeDataField(pascalName, DateTimeFormat.DateAndTime, isNullable: true));
        }
        else
        {
            outFields.Add(new DataField(pascalName, f.ClrType, isNullable: true));
        }
    }

    var outSchema = new ParquetSchema(outFields);

    var options = new ParquetOptions
    {
        CompressionMethod = CompressionMethod.Zstd,
    };

    await using var outStream = File.Create(outputPath);
    await using var writer = await ParquetWriter.CreateAsync(outSchema, outStream, options: options);

    for (int rgi = 0; rgi < reader.RowGroupCount; rgi++)
    {
        using var rgReader = reader.OpenRowGroupReader(rgi);
        using var rgWriter = writer.CreateRowGroup();
        int count = checked((int)rgReader.RowCount);

        for (int fi = 0; fi < inFields.Length; fi++)
        {
            var inField = inFields[fi];
            var outField = (DataField)outSchema.Fields[fi];

            if (inField.ClrType == typeof(string))
            {
                var mem = new ReadOnlyMemory<char>?[count];
                await rgReader.ReadAsync(inField, mem.AsMemory());
                var strings = mem.Select(m => m.HasValue ? new string(m.Value.Span) : null).ToArray();
                await rgWriter.WriteAsync(outField, strings);
            }
            else if (inField.ClrType == typeof(long))
            {
                var mem = new long?[count];
                await rgReader.ReadAsync<long>(inField, mem.AsMemory());
                await rgWriter.WriteAsync<long>(outField, mem);
            }
            else if (inField.ClrType == typeof(decimal))
            {
                var mem = new decimal?[count];
                await rgReader.ReadAsync<decimal>(inField, mem.AsMemory());
                await rgWriter.WriteAsync<decimal>(outField, mem);
            }
            else if (inField.ClrType == typeof(DateTime))
            {
                var mem = new DateTime?[count];
                await rgReader.ReadAsync<DateTime>(inField, mem.AsMemory());
                await rgWriter.WriteAsync<DateTime>(outField, mem);
            }
            else if (inField.ClrType == typeof(DateOnly))
            {
                var mem = new DateOnly?[count];
                await rgReader.ReadAsync<DateOnly>(inField, mem.AsMemory());
                var dts = mem.Select(d => d.HasValue ? d.Value.ToDateTime(TimeOnly.MinValue) : (DateTime?)null).ToArray();
                await rgWriter.WriteAsync(outField, dts);
            }
            else if (inField.ClrType == typeof(double))
            {
                var mem = new double?[count];
                await rgReader.ReadAsync<double>(inField, mem.AsMemory());
                await rgWriter.WriteAsync<double>(outField, mem);
            }
            else if (inField.ClrType == typeof(int))
            {
                var mem = new int?[count];
                await rgReader.ReadAsync<int>(inField, mem.AsMemory());
                await rgWriter.WriteAsync<int>(outField, mem);
            }
            else if (inField.ClrType == typeof(bool))
            {
                var mem = new bool?[count];
                await rgReader.ReadAsync<bool>(inField, mem.AsMemory());
                await rgWriter.WriteAsync<bool>(outField, mem);
            }
            else
            {
                throw new NotSupportedException($"Column '{inField.Name}' has unsupported type '{inField.ClrType}'");
            }
        }
    }
}

static string ToPascalCase(string s)
{
    if (s.StartsWith("l_")) s = s.Substring(2);
    s = s.Replace(".", "_");
    var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
    return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
}

static string FindBenchmarkDataDir()
{
    string[] candidates =
    [
        Path.Combine(Directory.GetCurrentDirectory(), "benchmarks", "data"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "benchmarks", "data"),
        Path.Combine(AppContext.BaseDirectory, "benchmarks", "data"),
        Path.Combine(AppContext.BaseDirectory, "data"),
    ];

    foreach (var candidate in candidates)
    {
        string full = Path.GetFullPath(candidate);
        if (Directory.Exists(full))
        {
            return full;
        }
    }

    return Path.GetFullPath(Path.Combine("benchmarks", "data"));
}
