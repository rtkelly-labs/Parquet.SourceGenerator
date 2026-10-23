using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Parquet.SourceGenerator;

namespace PackageConsumptionMultiTarget;

// One model, one calling source, two backends (#493). Everything outside `#if` below compiles
// against both the legacy generator (net472, Parquet.Net 4.25) and the modern one (net8.0,
// Parquet.Net 6.x). That shared part is deliberately only what is at parity today; each `#if`
// names the allowlist section (src/api/legacy-parity-allowlist.txt) that keeps it out. When a
// parity issue removes those entries, the call moves out of its `#if` here in the same change.
[ParquetSerializable]
public sealed partial class Reading
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; set; }

    [ParquetColumn("sensor", Order = 2)]
    public string? Sensor { get; set; }

    [ParquetColumn("value", Order = 3)]
    public double Value { get; set; }

    [ParquetColumn("sequence", Order = 4)]
    public long Sequence { get; set; }

    [ParquetColumn("ok", Order = 5)]
    public bool Ok { get; set; }
}

internal static class Program
{
    private static async Task<int> Main()
    {
        var expected = new List<Reading>
        {
            new()
            {
                Id = 1,
                Sensor = "north",
                Value = 1.5,
                Sequence = 10,
                Ok = true,
            },
            new()
            {
                Id = 2,
                Sensor = null,
                Value = -2.25,
                Sequence = 11,
                Ok = false,
            },
            new()
            {
                Id = 3,
                Sensor = "south",
                Value = 0,
                Sequence = long.MaxValue,
                Ok = true,
            },
        };

        // ---- Shared: at parity on both backends -------------------------------------------
        // WriteParquetAsync(this IEnumerable<T>, Stream, ParquetSerializerOptions?, CancellationToken)
        // is the same normalised .api.txt line on both backends, and ParquetSerializerOptions is the
        // shared Attributes package type.
        var options = new ParquetSerializerOptions { RowGroupSize = 2 };
        using var stream = new MemoryStream();
        await expected.WriteParquetAsync(stream, options);
        if (stream.Length == 0)
        {
            return Fail("the shared write produced no bytes");
        }

        stream.Position = 0;

        // ---- Not at parity yet --------------------------------------------------------------
#if NET8_0_OR_GREATER
        // [temporary #494] The builder reader exists only on the modern backend.
        Reading[] actual = await ReadingParquet.From(stream).ToArrayAsync();

        // [permanent #490] AsBatches() is modern-only by decision: legacy callers get a compile
        // error, not a runtime NotSupportedException, so it is guarded here for good.
        int batchedRows = 0;
        await foreach (ReadingBatch batch in ReadingParquet.From(stream.ToArray()).AsBatches())
        {
            batchedRows += batch.RowCount;
        }

        if (batchedRows != expected.Count)
        {
            return Fail($"AsBatches() yielded {batchedRows} rows, expected {expected.Count}");
        }
#else
        // [temporary #494] Legacy-only flat read, removed when the legacy builder lands.
        Reading[] actual = await ReadingParquetLegacyExtensions.ReadParquetArrayAsync(stream);
#endif

        // ---- Shared again ------------------------------------------------------------------
        if (actual.Length != expected.Count)
        {
            return Fail($"read {actual.Length} rows, expected {expected.Count}");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            Reading e = expected[i];
            Reading a = actual[i];
            if (
                a.Id != e.Id
                || a.Sensor != e.Sensor
                || !a.Value.Equals(e.Value)
                || a.Sequence != e.Sequence
                || a.Ok != e.Ok
            )
            {
                return Fail($"row {i} did not round-trip");
            }
        }

        Console.WriteLine(
            $"Multi-target consumer ({Backend}): {actual.Length} rows round-tripped through the shared write."
        );
        return 0;
    }

#if NET8_0_OR_GREATER
    private const string Backend = "modern, Parquet.Net 6.x";
#else
    private const string Backend = "legacy, Parquet.Net 4.25";
#endif

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"FAIL ({Backend}): {message}");
        return 1;
    }
}
