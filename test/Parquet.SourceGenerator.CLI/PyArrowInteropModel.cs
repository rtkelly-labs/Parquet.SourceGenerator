using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator.CLI;

public enum PyArrowInteropStatus
{
    Pending = 0,
    Active = 1,
    Closed = 2,
}

[ParquetSerializable]
public partial record PyArrowInteropRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("required_name")]
    public string RequiredName { get; init; } = string.Empty;

    [ParquetColumn("optional_name")]
    public string? OptionalName { get; init; }

    [ParquetColumn("payload")]
    public byte[]? Payload { get; init; }

    [ParquetColumn("amount")]
    [ParquetDecimal(18, 4)]
    public decimal Amount { get; init; }

    [ParquetColumn("timestamp")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime Timestamp { get; init; }

    [ParquetColumn("status")]
    public PyArrowInteropStatus? Status { get; init; }
}

public static class PyArrowInteropGenerator
{
    public static async Task GenerateAsync(string outputPath)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var rows = new List<PyArrowInteropRecord>
        {
            new()
            {
                Id = 1,
                RequiredName = "one",
                OptionalName = string.Empty,
                Payload = Array.Empty<byte>(),
                Amount = 123.4567m,
                Timestamp = new DateTime(2024, 6, 15, 12, 30, 0, 123, DateTimeKind.Utc),
                Status = PyArrowInteropStatus.Active,
            },
            new()
            {
                Id = 2,
                RequiredName = "two",
                OptionalName = null,
                Payload = null,
                Amount = -0.0001m,
                Timestamp = new DateTime(2024, 6, 16, 12, 30, 0, 456, DateTimeKind.Utc),
                Status = null,
            },
            new()
            {
                Id = 3,
                RequiredName = "three",
                OptionalName = "three",
                Payload = new byte[] { 0, 1, 255 },
                Amount = 0m,
                Timestamp = new DateTime(2024, 6, 17, 12, 30, 0, 789, DateTimeKind.Utc),
                Status = PyArrowInteropStatus.Closed,
            },
        };

        await using var stream = System.IO.File.Create(outputPath);
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions
            {
                CompressionMethod = ParquetCompressionMethod.Snappy,
                RowGroupSize = 2,
            }
        );
    }
}
