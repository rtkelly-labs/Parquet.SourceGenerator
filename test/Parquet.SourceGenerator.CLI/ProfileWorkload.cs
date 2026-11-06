using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator.CLI;

[ParquetSerializable]
public partial record ProfileEvent
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("score")]
    public double Score { get; init; }

    [ParquetColumn("is_active")]
    public bool IsActive { get; init; }
}

public static class ProfileWorkload
{
    public static async Task ExecuteAsync(int iterations = 100, int rowCount = 20_000)
    {
        Console.WriteLine(
            $"🔥 Starting Profile Workload ({iterations} iterations x {rowCount} rows)..."
        );
        var sw = Stopwatch.StartNew();

        var items = Enumerable
            .Range(0, rowCount)
            .Select(i => new ProfileEvent
            {
                Id = i,
                Name = $"user_profile_event_{i}",
                Score = i * 1.618,
                IsActive = i % 2 == 0,
            })
            .ToList();

        long totalBytesWritten = 0;

        for (int iteration = 1; iteration <= iterations; iteration++)
        {
            using var ms = new MemoryStream();
            await items.WriteParquetAsync(ms);
            totalBytesWritten += ms.Length;

            ms.Position = 0;
            List<ProfileEvent> readItems = await ProfileEventParquetExtensions.ReadParquetAsync(ms);
            if (readItems.Count != rowCount)
            {
                throw new InvalidOperationException(
                    $"Mismatch at iteration {iteration}: expected {rowCount}, got {readItems.Count}"
                );
            }

            if (iteration % 20 == 0)
            {
                Console.WriteLine(
                    $"   Completed iteration {iteration}/{iterations} ({sw.ElapsedMilliseconds} ms elapsed)..."
                );
            }
        }

        sw.Stop();
        Console.WriteLine($"✅ Profile Workload Complete!");
        Console.WriteLine($"   Total Time: {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"   Total Data Processed: {totalBytesWritten / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine(
            $"   Throughput: {(totalBytesWritten / 1024.0 / 1024.0) / (sw.ElapsedMilliseconds / 1000.0):F2} MB/s"
        );
    }
}
