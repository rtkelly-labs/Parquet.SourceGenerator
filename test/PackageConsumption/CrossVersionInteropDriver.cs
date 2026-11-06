using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CrossVersionInterop;

namespace PackageConsumption;

/// <summary>
/// The modern (Parquet.Net 6.x) half of the cross-version interoperability leg of the compatibility
/// matrix. <c>--write-interop</c> produces a file for the legacy consumer to read;
/// <c>--read-interop</c> consumes a file the legacy producer wrote. Each invocation appends a row to
/// the matrix report when one is requested, so the CI artifact records both directions.
/// </summary>
internal static class CrossVersionInteropDriver
{
    public const string Backend = "modern/Parquet.Net-6.x";

    public static List<InteropRow> CanonicalRows { get; } =
        new List<InteropRow>
        {
            new()
            {
                Id = 1,
                Name = "alpha",
                Value = 1.25,
                Ticks = 1_700_000_000_000,
            },
            new()
            {
                Id = 2,
                Name = null,
                Value = null,
                Ticks = 1_700_000_060_000,
            },
            new()
            {
                Id = 3,
                Name = string.Empty,
                Value = -0.5,
                Ticks = 1_700_000_120_000,
            },
        };

    public static async Task<int> WriteAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var stream = File.Create(path))
        {
            await CanonicalRows.WriteParquetAsync(stream);
        }

        Console.WriteLine(
            $"{Backend} wrote {CanonicalRows.Count} interop rows to {path} ({new FileInfo(path).Length} bytes)."
        );
        return 0;
    }

    public static async Task<int> ReadAsync(string path, string producerBackend, string? matrixPath)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FAILED: interop file not found: {path}");
            return 1;
        }

        using var stream = File.OpenRead(path);
        List<InteropRowEvolved> read = await InteropRowEvolvedParquetExtensions.ReadParquetAsync(
            stream
        );

        string? failure = InteropVerification.Verify(read, CanonicalRows);
        if (failure is not null)
        {
            Console.Error.WriteLine($"FAILED: {failure}");
            return 1;
        }

        Console.WriteLine(
            $"{Backend} read {read.Count} rows written by {producerBackend}: columns resolved by name, "
                + "added_note/added_count absent from the file and materialised as null."
        );

        MatrixReport.Append(
            matrixPath,
            producer: producerBackend,
            consumer: Backend,
            schemaCase: "column-reordering+missing-optional-columns",
            outcome: "CompatibleWithNulls",
            detail: $"{read.Count} rows"
        );
        return 0;
    }
}
