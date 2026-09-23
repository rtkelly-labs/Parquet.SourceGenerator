using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Comparison rules shared by compatibility tests that read the same logical data through
/// different Parquet producers or consumers.
/// </summary>
public sealed class CompatibilityComparisonOptions
{
    /// <summary>
    /// Gets or sets the precision at which DateTime values are compared. Null requires exact ticks.
    /// </summary>
    public TimeSpan? TimestampPrecision { get; set; }

    /// <summary>
    /// Gets or sets the absolute tolerance for float and double values.
    /// </summary>
    public double FloatingPointTolerance { get; set; }
}

/// <summary>
/// Semantic oracle for compatibility tests. It intentionally compares logical values rather than
/// Parquet bytes, page boundaries, or producer-specific metadata.
/// </summary>
public static class ParquetCompatibilityOracle
{
    public static void AssertEquivalent<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        CompatibilityComparisonOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        List<T> expectedRows = expected.ToList();
        List<T> actualRows = actual.ToList();
        options ??= new CompatibilityComparisonOptions();

        (expectedRows.Count == actualRows.Count).ShouldBeTrue(
            $"row count: expected {expectedRows.Count}, actual {actualRows.Count}"
        );

        for (int i = 0; i < expectedRows.Count; i++)
        {
            CompareValue(expectedRows[i], actualRows[i], $"row {i}", options);
        }
    }

    private static void CompareValue(
        object? expected,
        object? actual,
        string path,
        CompatibilityComparisonOptions options
    )
    {
        if (expected is null || actual is null)
        {
            (expected is null && actual is null).ShouldBeTrue(
                $"{path}: expected {FormatValue(expected)}, actual {FormatValue(actual)}"
            );
            return;
        }

        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
        {
            expectedBytes
                .AsSpan()
                .SequenceEqual(actualBytes)
                .ShouldBeTrue(
                    $"{path}: expected {FormatBytes(expectedBytes)}, actual {FormatBytes(actualBytes)}"
                );
            return;
        }

        if (
            expected is ReadOnlyMemory<byte> expectedByteMemory
            && actual is ReadOnlyMemory<byte> actualByteMemory
        )
        {
            expectedByteMemory
                .Span.SequenceEqual(actualByteMemory.Span)
                .ShouldBeTrue(
                    $"{path}: expected {FormatBytes(expectedByteMemory.Span)}, actual {FormatBytes(actualByteMemory.Span)}"
                );
            return;
        }

        if (
            expected is ReadOnlyMemory<char> expectedCharMemory
            && actual is ReadOnlyMemory<char> actualCharMemory
        )
        {
            expectedCharMemory
                .Span.SequenceEqual(actualCharMemory.Span)
                .ShouldBeTrue(
                    $"{path}: expected '{expectedCharMemory}', actual '{actualCharMemory}'"
                );
            return;
        }

        if (expected is DateTime expectedDateTime && actual is DateTime actualDateTime)
        {
            if (options.TimestampPrecision is TimeSpan precision)
            {
                (precision > TimeSpan.Zero).ShouldBeTrue("TimestampPrecision must be positive.");
                long expectedBucket = expectedDateTime.Ticks / precision.Ticks;
                long actualBucket = actualDateTime.Ticks / precision.Ticks;
                (expectedBucket == actualBucket).ShouldBeTrue(
                    $"{path}: expected {expectedDateTime:o}, actual {actualDateTime:o}, precision {precision}"
                );
            }
            else
            {
                (expectedDateTime.Ticks == actualDateTime.Ticks).ShouldBeTrue(
                    $"{path}: expected {expectedDateTime:o}, actual {actualDateTime:o}"
                );
            }

            return;
        }

        if (expected is TimeOnly expectedTime && actual is TimeOnly actualTime)
        {
            if (options.TimestampPrecision is TimeSpan timePrecision)
            {
                (timePrecision > TimeSpan.Zero).ShouldBeTrue(
                    "TimestampPrecision must be positive."
                );
                long expectedBucket = expectedTime.Ticks / timePrecision.Ticks;
                long actualBucket = actualTime.Ticks / timePrecision.Ticks;
                (expectedBucket == actualBucket).ShouldBeTrue(
                    $"{path}: expected {expectedTime}, actual {actualTime}, precision {timePrecision}"
                );
            }
            else
            {
                (expectedTime.Ticks == actualTime.Ticks).ShouldBeTrue(
                    $"{path}: expected {expectedTime}, actual {actualTime}"
                );
            }

            return;
        }

        if (expected is double expectedDouble && actual is double actualDouble)
        {
            AssertFloatingPoint(expectedDouble, actualDouble, path, options.FloatingPointTolerance);
            return;
        }

        if (expected is float expectedFloat && actual is float actualFloat)
        {
            AssertFloatingPoint(expectedFloat, actualFloat, path, options.FloatingPointTolerance);
            return;
        }

        if (
            expected is string
            || expected.GetType().IsPrimitive
            || expected.GetType().IsEnum
            || expected.GetType().IsValueType
        )
        {
            Equals(expected, actual)
                .ShouldBeTrue(
                    $"{path}: expected {FormatValue(expected)}, actual {FormatValue(actual)}"
                );
            return;
        }

        if (expected is IEnumerable expectedItems && actual is IEnumerable actualItems)
        {
            List<object?> expectedList = expectedItems.Cast<object?>().ToList();
            List<object?> actualList = actualItems.Cast<object?>().ToList();
            (expectedList.Count == actualList.Count).ShouldBeTrue(
                $"{path}: expected {expectedList.Count} items, actual {actualList.Count}"
            );
            for (int i = 0; i < expectedList.Count; i++)
            {
                CompareValue(expectedList[i], actualList[i], $"{path}[{i}]", options);
            }

            return;
        }

        PropertyInfo[] properties = expected
            .GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToArray();
        (properties.Length > 0).ShouldBeTrue($"{path}: no comparable public values found.");

        foreach (PropertyInfo property in properties)
        {
            PropertyInfo? actualProperty = actual.GetType().GetProperty(property.Name);
            (actualProperty is not null).ShouldBeTrue(
                $"{path}: actual value has no property '{property.Name}'."
            );
            CompareValue(
                property.GetValue(expected),
                actualProperty!.GetValue(actual),
                $"{path}.{property.Name}",
                options
            );
        }
    }

    private static void AssertFloatingPoint(
        double expected,
        double actual,
        string path,
        double tolerance
    )
    {
        bool equal = double.IsNaN(expected) && double.IsNaN(actual);
        equal |=
            double.IsInfinity(expected) || double.IsInfinity(actual)
                ? expected == actual
                : Math.Abs(expected - actual) <= tolerance;
        equal.ShouldBeTrue(
            $"{path}: expected {expected:R}, actual {actual:R}, tolerance {tolerance}"
        );
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null => "<null>",
            byte[] bytes => FormatBytes(bytes),
            _ => value.ToString() ?? "<empty>",
        };

    private static string FormatBytes(ReadOnlySpan<byte> bytes)
    {
        const int previewLength = 16;
        string preview = Convert.ToHexString(bytes[..Math.Min(bytes.Length, previewLength)]);
        return bytes.Length > previewLength
            ? $"byte[{bytes.Length}]({preview}...)"
            : $"byte[{bytes.Length}]({preview})";
    }
}

[ParquetSerializable]
public partial record CompatibilityRecord
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

    [ParquetColumn("correlation_id")]
    public Guid? CorrelationId { get; init; }

    [ParquetColumn("status")]
    public EventStatus? Status { get; init; }
}

public sealed class CompatibilityTestHarnessTests
{
    [Fact]
    public async Task CanonicalModelRoundtripsThroughGeneratedReader()
    {
        var expected = new List<CompatibilityRecord>
        {
            new()
            {
                Id = 1,
                RequiredName = "one",
                OptionalName = string.Empty,
                Payload = Array.Empty<byte>(),
                Amount = 123.4567m,
                Timestamp = new DateTime(2024, 6, 15, 12, 30, 0, 123, DateTimeKind.Utc),
                CorrelationId = Guid.NewGuid(),
                Status = EventStatus.Active,
            },
            new()
            {
                Id = 2,
                RequiredName = "two",
                OptionalName = null,
                Payload = null,
                Amount = -0.0001m,
                Timestamp = new DateTime(2024, 6, 16, 12, 30, 0, 456, DateTimeKind.Utc),
                CorrelationId = null,
                Status = null,
            },
        };

        using var stream = new MemoryStream();
        await expected.WriteParquetAsync(stream);
        stream.Position = 0;

        CompatibilityRecord[] actual = await CompatibilityRecordParquet.From(stream).ToArrayAsync();

        ParquetCompatibilityOracle.AssertEquivalent(
            expected,
            actual,
            new CompatibilityComparisonOptions { TimestampPrecision = TimeSpan.FromMicroseconds(1) }
        );
    }

    [Fact]
    public void OracleDistinguishesNullFromEmptyAndReportsFieldPath()
    {
        var expected = new[] { new CompatibilityRecord { RequiredName = "expected" } };
        var actual = new[]
        {
            new CompatibilityRecord { RequiredName = "expected", OptionalName = string.Empty },
        };

        var exception = Should.Throw<ShouldAssertException>(() =>
            ParquetCompatibilityOracle.AssertEquivalent(expected, actual)
        );

        exception.Message.ShouldContain("row 0.OptionalName");
    }

    [Fact]
    public void OracleAppliesExplicitTimestampPrecisionAndFloatingPointTolerance()
    {
        var expected = new[]
        {
            new CompatibilityRecord { Timestamp = new DateTime(638540658001234567) },
        };
        var actual = new[]
        {
            new CompatibilityRecord { Timestamp = new DateTime(638540658001234560) },
        };

        ParquetCompatibilityOracle.AssertEquivalent(
            expected,
            actual,
            new CompatibilityComparisonOptions { TimestampPrecision = TimeSpan.FromMicroseconds(1) }
        );

        var expectedDoubles = new[] { new { Value = 1.0 } };
        var actualDoubles = new[] { new { Value = 1.000001 } };
        ParquetCompatibilityOracle.AssertEquivalent(
            expectedDoubles,
            actualDoubles,
            new CompatibilityComparisonOptions { FloatingPointTolerance = 0.00001 }
        );
    }
}
