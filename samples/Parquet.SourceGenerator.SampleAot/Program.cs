using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Parquet.SourceGenerator;

namespace Parquet.SourceGenerator.SampleAot;

[ParquetSerializable]
public sealed partial record FinancialTransaction
{
    [ParquetColumn("account_number", Order = 1)]
    public string AccountNumber { get; init; } = string.Empty;

    [ParquetColumn("amount", Order = 2)]
    public double Amount { get; init; }

    [ParquetColumn("is_settled", Order = 3)]
    public bool IsSettled { get; init; }

    [ParquetColumn("timestamp_ms", Order = 4)]
    public long TimestampMs { get; init; }
}

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("🚀 Parquet.SourceGenerator - Native AOT Sample");
        Console.WriteLine("=================================================");

        var transactions = new List<FinancialTransaction>
        {
            new()
            {
                AccountNumber = "ACC-98765",
                Amount = 1450.75,
                IsSettled = true,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            new()
            {
                AccountNumber = "ACC-12345",
                Amount = 89.99,
                IsSettled = false,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        };

        using var memoryStream = new MemoryStream();

        // 1. Serialize using zero-reflection compile-time generated stream writer
        Console.WriteLine(
            $"[1/2] Serializing {transactions.Count} transactions to Parquet stream (Native AOT safe)..."
        );
        await transactions.WriteParquetAsync(memoryStream);

        // 2. Deserialize using zero-reflection compile-time generated stream reader
        memoryStream.Position = 0;
        Console.WriteLine("[2/2] Deserializing Parquet stream back to strong typed objects...");
        List<FinancialTransaction> deserialized =
            await FinancialTransactionParquetExtensions.ReadParquetAsync(memoryStream);

        Console.WriteLine($"✅ Successfully deserialized {deserialized.Count} records!");
        Console.WriteLine(
            $"   Record 1 Account: {deserialized[0].AccountNumber}, Amount: ${deserialized[0].Amount}"
        );
        Console.WriteLine(
            $"   Record 2 Account: {deserialized[1].AccountNumber}, Amount: ${deserialized[1].Amount}"
        );
        Console.WriteLine("=================================================");
        Console.WriteLine("🎉 Native AOT Sample Pathway Executed Cleanly!");
        Console.WriteLine("=================================================");
    }
}
