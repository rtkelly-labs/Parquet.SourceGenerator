using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator.Tests.PropertyBased;

/// <summary>
/// The properties the suite checks, expressed as functions that return a failure description rather
/// than throwing.
/// </summary>
/// <remarks>
/// Returning the failure instead of asserting is what makes shrinking possible: the shrinker needs
/// to ask "does this smaller case still fail?" thousands of times, and an assertion framework's
/// exception is a poor answer to that question.
/// </remarks>
public static class FuzzRunner
{
    /// <summary>All checks, by name, so a failure can say which property broke.</summary>
    public static IReadOnlyDictionary<string, Func<FuzzCase, Task<string?>>> Checks { get; } =
        new Dictionary<string, Func<FuzzCase, Task<string?>>>(StringComparer.Ordinal)
        {
            ["generated-write/engine-read"] = GeneratedWriteEngineReadAsync,
            ["engine-write/generated-read"] = EngineWriteGeneratedReadAsync,
            ["generated-round-trip"] = GeneratedRoundTripAsync,
        };

    /// <summary>Runs every check and returns the first failure, or null when all pass.</summary>
    public static async Task<string?> RunAllAsync(FuzzCase fuzzCase)
    {
        foreach ((string name, Func<FuzzCase, Task<string?>> check) in Checks)
        {
            string? failure = await SafeAsync(check, fuzzCase);
            if (failure is not null)
            {
                return $"[{name}] {failure}";
            }
        }

        return null;
    }

    /// <summary>
    /// Runs one check, turning an unexpected exception into a failure description. A crash is a
    /// failing property too, and the shrinker has to be able to minimise towards it.
    /// </summary>
    public static async Task<string?> SafeAsync(
        Func<FuzzCase, Task<string?>> check,
        FuzzCase fuzzCase
    )
    {
        try
        {
            return await check(fuzzCase);
        }
        catch (Exception ex)
        {
            return $"threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Property: what the generated writer emits is what an independent engine reads back, value for
    /// value, with the row-group layout the options asked for.
    /// </summary>
    public static async Task<string?> GeneratedWriteEngineReadAsync(FuzzCase fuzzCase)
    {
        IReadOnlyList<FuzzWideRecord> rows = fuzzCase.BuildRows();
        byte[] bytes = await WriteWithGeneratedWriterAsync(rows, fuzzCase);

        EngineFile file = await IndependentParquetEngine.ReadAsync(bytes, FuzzColumns.All);

        if (file.TotalRows != rows.Count)
        {
            return $"row count: expected {rows.Count}, engine read {file.TotalRows}";
        }

        string? layout = CheckRowGroupLayout(file, rows.Count, fuzzCase.RowGroupSize);
        if (layout is not null)
        {
            return layout;
        }

        foreach (FuzzColumn column in FuzzColumns.All)
        {
            IReadOnlyList<object?> actual = file.Column(column.Name);
            for (int i = 0; i < rows.Count; i++)
            {
                object? expected = column.NormalizedValue(rows[i]);
                if (!FuzzCompare.Equal(expected, actual[i]))
                {
                    return Mismatch(column, i, expected, actual[i]);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Property: the generated reader agrees with an independent engine's writer, including when
    /// that writer lays the columns out in a different order from the generated schema.
    /// </summary>
    public static async Task<string?> EngineWriteGeneratedReadAsync(FuzzCase fuzzCase)
    {
        IReadOnlyList<FuzzWideRecord> rows = fuzzCase.BuildRows();
        IReadOnlyList<FuzzColumn> fileColumns = EngineFileColumns(fuzzCase);

        byte[] bytes = await IndependentParquetEngine.WriteAsync(
            rows,
            fileColumns,
            Math.Max(1, fuzzCase.RowGroupSize),
            fuzzCase.Compression
        );

        using var stream = new MemoryStream(bytes, writable: false);
        List<FuzzWideRecord> actual = await FuzzWideRecordParquetExtensions.ReadParquetAsync(
            stream,
            BuildOptions(fuzzCase)
        );

        if (actual.Count != rows.Count)
        {
            return $"row count: expected {rows.Count}, generated reader produced {actual.Count}";
        }

        foreach (FuzzColumn column in FuzzColumns.All)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                object? expected = column.NormalizedValue(rows[i]);
                object? read = column.NormalizedValue(actual[i]);
                if (!FuzzCompare.Equal(expected, read))
                {
                    return Mismatch(column, i, expected, read);
                }
            }
        }

        return null;
    }

    /// <summary>Property: the generated writer and reader agree with each other.</summary>
    public static async Task<string?> GeneratedRoundTripAsync(FuzzCase fuzzCase)
    {
        IReadOnlyList<FuzzWideRecord> rows = fuzzCase.BuildRows();
        byte[] bytes = await WriteWithGeneratedWriterAsync(rows, fuzzCase);

        using var stream = new MemoryStream(bytes, writable: false);
        List<FuzzWideRecord> actual = await FuzzWideRecordParquetExtensions.ReadParquetAsync(
            stream,
            BuildOptions(fuzzCase)
        );

        if (actual.Count != rows.Count)
        {
            return $"row count: expected {rows.Count}, generated reader produced {actual.Count}";
        }

        foreach (FuzzColumn column in FuzzColumns.All)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                object? expected = column.NormalizedValue(rows[i]);
                object? read = column.NormalizedValue(actual[i]);
                if (!FuzzCompare.Equal(expected, read))
                {
                    return Mismatch(column, i, expected, read);
                }
            }
        }

        return null;
    }

    /// <summary>Writes the case's rows with the generated writer and returns the file bytes.</summary>
    public static async Task<byte[]> WriteWithGeneratedWriterAsync(
        IReadOnlyList<FuzzWideRecord> rows,
        FuzzCase fuzzCase,
        CancellationToken cancellationToken = default
    )
    {
        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            options: BuildOptions(fuzzCase),
            cancellationToken: cancellationToken
        );
        return stream.ToArray();
    }

    /// <summary>Translates a case's knobs into serializer options.</summary>
    public static ParquetSerializerOptions BuildOptions(FuzzCase fuzzCase)
    {
        var options = new ParquetSerializerOptions
        {
            RowGroupSize = Math.Max(1, fuzzCase.RowGroupSize),
            CompressionMethod = fuzzCase.Compression,
            DeduplicateStrings = fuzzCase.DeduplicateStrings,
        };

        foreach ((string column, ParquetColumnEncoding encoding) in fuzzCase.EncodingHints)
        {
            options.ColumnEncodingHints[column] = encoding;
        }

        return options;
    }

    /// <summary>
    /// The columns the reference writer puts in the file, in a seed-derived order.
    /// </summary>
    /// <remarks>
    /// Every column, always — the ordering is what varies. Omitting an optional column is a valid
    /// Parquet file the generated reader tolerates as of #168 (see
    /// <c>SupportedSchemaPropertyTests.AbsentNullableColumnMaterialisesAsNulls</c>). Widening this
    /// method to drop optional columns is the one-line change that turns that one-off into a
    /// property, and is deliberately left as a follow-up rather than bundled into this PR.
    /// </remarks>
    public static IReadOnlyList<FuzzColumn> EngineFileColumns(FuzzCase fuzzCase)
    {
        List<FuzzColumn> columns = FuzzColumns.All.ToList();
        new FuzzRandom(fuzzCase.Seed + 1).Shuffle(columns);
        return columns;
    }

    private static string? CheckRowGroupLayout(EngineFile file, int rowCount, int rowGroupSize)
    {
        int size = Math.Max(1, rowGroupSize);
        int expectedGroups = Math.Max(1, (rowCount + size - 1) / size);
        if (file.RowGroups.Count != expectedGroups)
        {
            return $"row group count: expected {expectedGroups} for {rowCount} rows at "
                + $"rowGroupSize {size}, file has {file.RowGroups.Count}";
        }

        for (int g = 0; g < file.RowGroups.Count; g++)
        {
            long expected = Math.Min(size, rowCount - ((long)g * size));
            if (file.RowGroups[g].RowCount != expected)
            {
                return $"row group {g}: expected {expected} rows, file has {file.RowGroups[g].RowCount}";
            }
        }

        return null;
    }

    private static string Mismatch(FuzzColumn column, int row, object? expected, object? actual) =>
        $"column '{column.Name}' row {row.ToString(CultureInfo.InvariantCulture)}: "
        + $"expected {FuzzValues.Describe(expected)}, actual {FuzzValues.Describe(actual)}";
}

/// <summary>Value comparison for canonicalised Parquet values.</summary>
public static class FuzzCompare
{
    /// <summary>
    /// Compares two canonical values. NaN equals NaN here, because the property under test is
    /// "the value survived the round trip", and a NaN that survived is a pass.
    /// </summary>
    public static bool Equal(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return (expected, actual) switch
        {
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            (double a, double b) => double.IsNaN(a) ? double.IsNaN(b) : a.Equals(b),
            (float a, float b) => float.IsNaN(a) ? float.IsNaN(b) : a.Equals(b),
            (DateTime a, DateTime b) => a.Ticks == b.Ticks,
            (decimal a, decimal b) => a == b,
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            _ => expected.Equals(actual),
        };
    }
}
