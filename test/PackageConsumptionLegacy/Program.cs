using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.SourceGenerator;

namespace PackageConsumptionLegacy;

public enum MeasurementGrade
{
    Low = 0,
    High = 1,
}

[ParquetSerializable]
public sealed partial class Measurement
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; set; }

    [ParquetColumn("val", Order = 2)]
    public double Val { get; set; }

    // A byte[] column used to emit `new byte[][count]`, which is not valid C# — every model with
    // one produced a generated file the compiler could not parse.
    [ParquetColumn("payload", Order = 3)]
    public byte[]? Payload { get; set; }

    // A nullable enum used to be written into a non-nullable underlying array via a straight cast,
    // so a null threw before it could be encoded.
    [ParquetColumn("grade", Order = 4)]
    public MeasurementGrade? Grade { get; set; }

    [ParquetColumn("label", Order = 5)]
    public string? Label { get; set; }
}

internal static class Program
{
    private static async Task<int> Main()
    {
        var expected = new List<Measurement>
        {
            new() { Id = 1, Val = 10.5, Payload = new byte[] { 1, 2, 3 }, Grade = MeasurementGrade.High, Label = "first" },
            new() { Id = 2, Val = 20.5, Payload = null, Grade = null, Label = null },
        };

        using var stream = new MemoryStream();
        await expected.WriteParquetAsync(stream);

        if (stream.Length == 0)
        {
            Console.Error.WriteLine("FAILED: WriteParquetAsync produced empty stream.");
            return 1;
        }

        stream.Position = 0;
        List<Measurement> actual = await MeasurementParquetLegacyExtensions.ReadParquetAsync(stream);

        if (actual.Count != expected.Count)
        {
            Console.Error.WriteLine($"FAILED: Expected {expected.Count} items, got {actual.Count}");
            return 1;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (actual[i].Id != expected[i].Id || Math.Abs(actual[i].Val - expected[i].Val) > 1e-9)
            {
                Console.Error.WriteLine($"FAILED: row {i} scalar mismatch.");
                return 1;
            }

            if (!ByteArraysEqual(actual[i].Payload, expected[i].Payload))
            {
                Console.Error.WriteLine($"FAILED: row {i} byte[] column did not round-trip.");
                return 1;
            }

            if (actual[i].Grade != expected[i].Grade)
            {
                Console.Error.WriteLine(
                    $"FAILED: row {i} nullable enum round-tripped as {actual[i].Grade}, expected {expected[i].Grade}.");
                return 1;
            }

            if (actual[i].Label != expected[i].Label)
            {
                Console.Error.WriteLine($"FAILED: row {i} string column did not round-trip.");
                return 1;
            }
        }

        if (!await CompressionIsAppliedAsync() || !await BatchedWriteRoundTripsAsync())
        {
            return 1;
        }

        Console.WriteLine($"PackageConsumptionLegacy OK: round-tripped {actual.Count} records.");
        return 0;
    }

    /// <summary>
    /// The compression options used to be dropped on the floor — BuildFormatOptions returned an
    /// empty ParquetOptions, and v4/v5 keeps compression on the writer rather than on the options.
    /// Uncompressed output being far larger than compressed output is the observable difference.
    /// </summary>
    private static async Task<bool> CompressionIsAppliedAsync()
    {
        // Distinct labels on purpose: with 4,000 identical strings Parquet.Net's dictionary encoding
        // would shrink the uncompressed file to nothing on its own, and the assertion below would
        // then be measuring dictionary encoding rather than compression.
        List<Measurement> rows = Enumerable.Range(0, 4_000)
            .Select(i => new Measurement { Id = i, Val = i, Label = "row-" + i + "-" + new string('a', 128) })
            .ToList();

        long uncompressed = await WriteWithAsync(rows, ParquetCompressionMethod.None);
        long compressed = await WriteWithAsync(rows, ParquetCompressionMethod.Gzip);

        if (uncompressed <= compressed * 2)
        {
            Console.Error.WriteLine(
                $"FAILED: compression option ignored — None wrote {uncompressed} bytes, Gzip wrote {compressed}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Exercises the multi-row-group path: the read side resolves schema fields once for the file
    /// and then loops row groups, so a file with several of them is the shape that would expose a
    /// field resolved against the wrong group or a row count taken from the wrong place.
    /// </summary>
    private static async Task<bool> BatchedWriteRoundTripsAsync()
    {
        List<Measurement> rows = Enumerable.Range(0, 250)
            .Select(i => new Measurement { Id = i, Val = i * 1.5, Label = "r" + i })
            .ToList();

        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(stream, rowGroupSize: 100);
        stream.Position = 0;

        List<Measurement> read = await MeasurementParquetLegacyExtensions.ReadParquetAsync(stream);

        if (read.Count != rows.Count)
        {
            Console.Error.WriteLine($"FAILED: batched write produced {read.Count} rows, expected {rows.Count}.");
            return false;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (read[i].Id != rows[i].Id || read[i].Label != rows[i].Label)
            {
                Console.Error.WriteLine($"FAILED: batched row {i} did not round-trip.");
                return false;
            }
        }

        return true;
    }

    private static async Task<long> WriteWithAsync(List<Measurement> rows, ParquetCompressionMethod method)
    {
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream, new ParquetSerializerOptions { CompressionMethod = method });
        return stream.Length;
    }

    private static bool ByteArraysEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
