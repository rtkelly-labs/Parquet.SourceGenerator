using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

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
    public int? Status { get; init; }
}

[ParquetSerializable]
public partial record DuckDbInteropRecord
{
    [ParquetColumn("id")]
    public int? Id { get; init; }

    [ParquetColumn("required_name")]
    public string? RequiredName { get; init; }

    [ParquetColumn("optional_name")]
    public string? OptionalName { get; init; }

    [ParquetColumn("payload")]
    public byte[]? Payload { get; init; }

    [ParquetColumn("amount")]
    [ParquetDecimal(18, 4)]
    public decimal? Amount { get; init; }

    [ParquetColumn("timestamp")]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime? Timestamp { get; init; }

    [ParquetColumn("status")]
    public int? Status { get; init; }
}

public sealed class PyArrowInteropTests
{
    [Fact]
    [Trait("Category", "ExternalInterop")]
    public async Task GeneratedReaderReadsPyArrowCanonicalFixture()
    {
        string? path = Environment.GetEnvironmentVariable("PARQUET_PYARROW_INTEROP_INPUT");
        // The regular suite remains self-contained; CI's ExternalInterop job supplies the fixture.
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        System.IO.File.Exists(path).ShouldBeTrue($"PyArrow fixture does not exist: {path}");

        await using var stream = System.IO.File.OpenRead(path!);
        List<PyArrowInteropRecord> actual =
            await PyArrowInteropRecordParquetExtensions.ReadParquetAsync(stream);

        ParquetCompatibilityOracle.AssertEquivalent(
            ExpectedRows(),
            actual,
            new CompatibilityComparisonOptions { TimestampPrecision = TimeSpan.FromMicroseconds(1) }
        );
    }

    [Fact]
    [Trait("Category", "ExternalInterop")]
    public async Task GeneratedReaderReadsDuckDbCanonicalFixture()
    {
        string? path = Environment.GetEnvironmentVariable("DUCKDB_INTEROP_INPUT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        System.IO.File.Exists(path).ShouldBeTrue($"DuckDB fixture does not exist: {path}");
        await using var stream = System.IO.File.OpenRead(path);
        List<DuckDbInteropRecord> actual =
            await DuckDbInteropRecordParquetExtensions.ReadParquetAsync(stream);

        ParquetCompatibilityOracle.AssertEquivalent(
            ExpectedDuckDbRows(),
            actual,
            new CompatibilityComparisonOptions { TimestampPrecision = TimeSpan.FromMicroseconds(1) }
        );
    }

    private static List<PyArrowInteropRecord> ExpectedRows() =>
        [
            new()
            {
                Id = 1,
                RequiredName = "one",
                OptionalName = string.Empty,
                Payload = Array.Empty<byte>(),
                Amount = 123.4567m,
                Timestamp = new DateTime(2024, 6, 15, 12, 30, 0, 123, DateTimeKind.Utc),
                Status = 1,
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
                Status = 2,
            },
        ];

    private static List<DuckDbInteropRecord> ExpectedDuckDbRows() =>
        [
            new()
            {
                Id = 1,
                RequiredName = "one",
                OptionalName = string.Empty,
                Payload = Array.Empty<byte>(),
                Amount = 123.4567m,
                Timestamp = new DateTime(2024, 6, 15, 12, 30, 0, 123, DateTimeKind.Utc),
                Status = 1,
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
                Status = 2,
            },
        ];
}
