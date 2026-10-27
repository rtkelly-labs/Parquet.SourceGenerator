// Before the shared consumption program runs: proves it runs on Lake.Parquet, not on a Parquet.Net that slipped in,
// and that the package says what it was built from.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;

namespace PackageConsumption;

internal static class LakeProvenance
{
    [ModuleInitializer]
    internal static void Check()
    {
        Assembly parquet = typeof(ParquetReader).Assembly;
        Require(
            parquet.GetName().Name == "Lake.Parquet",
            $"Parquet types come from '{parquet.GetName().Name}', not Lake.Parquet"
        );
        Require(
            AppDomain.CurrentDomain.GetAssemblies().All(a => a.GetName().Name != "Parquet"),
            "a Parquet.Net assembly (Parquet.dll) is loaded beside Lake.Parquet"
        );

        foreach (
            string key in new[] { "LakeForkCommit", "LakeUpstreamCommit", "LakeUpstreamVersion" }
        )
        {
            string? value = parquet
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)
                ?.Value;
            Require(!string.IsNullOrEmpty(value), $"Lake.Parquet has no {key} metadata");
            Console.WriteLine($"Lake.Parquet {key}: {value}");
        }

        // files written through Lake.Parquet say so
        string createdBy = CreatedByAsync().GetAwaiter().GetResult();
        Require(
            createdBy.StartsWith("Lake.Parquet version ", StringComparison.Ordinal),
            $"created_by is '{createdBy}'"
        );
        Console.WriteLine($"Lake.Parquet created_by: {createdBy}");
    }

    private static async Task<string> CreatedByAsync()
    {
        var field = new DataField<int>("x");
        using var ms = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)
        )
        {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(field, new[] { 1 });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        return reader.Metadata?.CreatedBy ?? "";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine($"FAIL: {message}");
            Environment.Exit(1);
        }
    }
}
