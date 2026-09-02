// Verifies the produced NuGet packages actually work when consumed, which building them does not.
//
// Everything here is written from the outside in: the only references are the Parquet.SourceGenerator
// package and Parquet.Net. If the generator ships wrongly — no analyzer in analyzers/dotnet/cs, a
// missing dependency on the Attributes package, or the generator assembly leaking into lib/ — this
// file stops compiling or stops running, and CI fails at the step after `dotnet pack`.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.SourceGenerator;

namespace PackageConsumption;

public enum DeviceStatus
{
    Standby = 0,
    Active = 1,
    Error = 2,
}

[ParquetSerializable]
public sealed partial record Reading
{
    [ParquetColumn("sensor_id", Order = 1)]
    public string SensorId { get; init; } = string.Empty;

    [ParquetColumn("celsius", Order = 2)]
    public double Celsius { get; init; }

    [ParquetColumn("captured_at_ms", Order = 3)]
    public long CapturedAtMs { get; init; }

    [ParquetColumn("is_calibrated", Order = 4)]
    public bool IsCalibrated { get; init; }

    [ParquetColumn("device_id", Order = 5)]
    public Guid DeviceId { get; init; }

    [ParquetColumn("status", Order = 6)]
    public DeviceStatus? Status { get; init; }

    [ParquetColumn("raw_bytes", Order = 7)]
    public byte[]? RawBytes { get; init; }

    [ParquetIgnore]
    public string Scratch { get; init; } = "not persisted";
}

internal static class Program
{
    private static async Task<int> Main()
    {
        var expected = new List<Reading>
        {
            new()
            {
                SensorId = "sensor-a",
                Celsius = 21.5,
                CapturedAtMs = 1_700_000_000_000,
                IsCalibrated = true,
                DeviceId = Guid.NewGuid(),
                Status = DeviceStatus.Active,
                RawBytes = new byte[] { 1, 2, 3, 4 },
            },
            new()
            {
                SensorId = "sensor-b",
                Celsius = -4.25,
                CapturedAtMs = 1_700_000_060_000,
                IsCalibrated = false,
                DeviceId = Guid.NewGuid(),
                Status = null,
                RawBytes = null,
            },
            new()
            {
                SensorId = "sensor-c",
                Celsius = 0.0,
                CapturedAtMs = 1_700_000_120_000,
                IsCalibrated = true,
                DeviceId = Guid.Empty,
                Status = DeviceStatus.Standby,
                RawBytes = Array.Empty<byte>(),
            },
        };

        using var stream = new MemoryStream();
        await expected.WriteParquetAsync(stream);

        if (stream.Length == 0)
        {
            return Fail("WriteParquetAsync produced an empty stream.");
        }

        stream.Position = 0;
        List<Reading> actual = await ReadingParquetExtensions.ReadParquetAsync(stream);

        if (actual.Count != expected.Count)
        {
            return Fail($"Expected {expected.Count} records, read {actual.Count}.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (actual[i].SensorId != expected[i].SensorId
                || Math.Abs(actual[i].Celsius - expected[i].Celsius) > 1e-9
                || actual[i].CapturedAtMs != expected[i].CapturedAtMs
                || actual[i].IsCalibrated != expected[i].IsCalibrated
                || actual[i].DeviceId != expected[i].DeviceId
                || actual[i].Status != expected[i].Status)
            {
                return Fail($"Record {i} did not round-trip correctly.");
            }

            if (!ByteArraysEqual(actual[i].RawBytes, expected[i].RawBytes))
            {
                return Fail($"Record {i} byte[] did not round-trip correctly.");
            }
        }

        if (actual[0].Scratch != "not persisted")
        {
            return Fail($"Expected [ParquetIgnore] member to be left at its initializer, got '{actual[0].Scratch}'.");
        }

        if (ReadingParquetExtensions.Schema.Fields.Count != 7)
        {
            return Fail($"Expected 7 schema fields, found {ReadingParquetExtensions.Schema.Fields.Count}.");
        }

        // Test multi-row-group batched write and parallel reader over byte buffer
        if (!await TestBatchedAndParallelReadAsync())
        {
            return 1;
        }

        // Test streaming reader (IAsyncEnumerable)
        if (!await TestStreamingReaderAsync(stream.ToArray(), expected.Count))
        {
            return 1;
        }

        Console.WriteLine($"Package consumption OK: round-tripped {actual.Count} records across all entry points, schema has "
                          + $"{ReadingParquetExtensions.Schema.Fields.Count} fields.");
        return 0;
    }

    private static async Task<bool> TestBatchedAndParallelReadAsync()
    {
        const int totalCount = 1_000;
        const int rowGroupSize = 200; // Produces 5 row groups

        List<Reading> data = Enumerable.Range(0, totalCount)
            .Select(i => new Reading
            {
                SensorId = $"sensor-{i % 10}",
                Celsius = 20.0 + (i * 0.1),
                CapturedAtMs = 1_700_000_000_000 + i,
                IsCalibrated = i % 2 == 0,
                DeviceId = Guid.NewGuid(),
                Status = (DeviceStatus)(i % 3),
                RawBytes = new byte[] { (byte)(i & 0xFF), 42 },
            })
            .ToList();

        using var mem = new MemoryStream();
        await data.WriteParquetBatchedAsync(mem, rowGroupSize: rowGroupSize);
        byte[] bytes = mem.ToArray();

        // 1. Sequential buffer read
        List<Reading> sequentialRead = await ReadingParquetExtensions.ReadParquetAsync(new ReadOnlyMemory<byte>(bytes));
        if (sequentialRead.Count != totalCount)
        {
            Console.Error.WriteLine($"FAILED: ReadParquetAsync(buffer) expected {totalCount} rows, got {sequentialRead.Count}.");
            return false;
        }

        // 2. Parallel buffer read with degree of parallelism = 4
        List<Reading> parallelRead = await ReadingParquetExtensions.ReadParquetParallelAsync(
            new ReadOnlyMemory<byte>(bytes),
            maxDegreeOfParallelism: 4);

        if (parallelRead.Count != totalCount)
        {
            Console.Error.WriteLine($"FAILED: ReadParquetParallelAsync(buffer) expected {totalCount} rows, got {parallelRead.Count}.");
            return false;
        }

        // Validate ordering and data equality between sequential and parallel
        for (int i = 0; i < totalCount; i++)
        {
            if (sequentialRead[i].CapturedAtMs != parallelRead[i].CapturedAtMs
                || sequentialRead[i].DeviceId != parallelRead[i].DeviceId
                || sequentialRead[i].Status != parallelRead[i].Status)
            {
                Console.Error.WriteLine($"FAILED: Parallel read row {i} does not match sequential read in file order.");
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> TestStreamingReaderAsync(byte[] bytes, int expectedCount)
    {
        using var stream = new MemoryStream(bytes);
        int count = 0;
        await foreach (var item in ReadingParquetExtensions.ReadParquetStreamAsync(stream))
        {
            count++;
        }

        if (count != expectedCount)
        {
            Console.Error.WriteLine($"FAILED: ReadParquetStreamAsync streamed {count} records, expected {expectedCount}.");
            return false;
        }

        return true;
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

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"Package consumption FAILED: {message}");
        return 1;
    }
}
