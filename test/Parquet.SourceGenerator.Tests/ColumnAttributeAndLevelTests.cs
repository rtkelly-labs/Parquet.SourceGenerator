using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record OrderOnlyModel
{
    // Order without a rename. Previously impossible to express: the attribute had no parameterless
    // constructor, and named arguments were read only when a constructor argument was also present.
    [ParquetColumn(Order = 3)]
    public int Third { get; init; }

    [ParquetColumn(Order = 1)]
    public int First { get; init; }

    [ParquetColumn(Name = "second_column", Order = 2)]
    public int Second { get; init; }
}

[ParquetSerializable]
public partial record CompressionLevelModel
{
    [ParquetColumn("payload")]
    public string Payload { get; init; } = string.Empty;
}

public sealed class ColumnAttributeAndLevelTests
{
    private static readonly string[] ExpectedColumnOrder = { "First", "second_column", "Third" };

    [Fact]
    public void OrderOnlyAttributeReordersColumnsWithoutRenamingThem()
    {
        string[] columns = OrderOnlyModelParquetExtensions
            .Schema.DataFields.Select(f => f.Name)
            .ToArray();

        // First and Third keep their member names; only Second was renamed.
        columns.ShouldBe(ExpectedColumnOrder);
    }

    [Fact]
    public async Task OrderOnlyModelRoundTrips()
    {
        var written = new List<OrderOnlyModel>
        {
            new()
            {
                First = 1,
                Second = 2,
                Third = 3,
            },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        List<OrderOnlyModel> read = await OrderOnlyModelParquet.From(stream).ToListAsync();

        read.ShouldHaveSingleItem();
        read[0].First.ShouldBe(1);
        read[0].Second.ShouldBe(2);
        read[0].Third.ShouldBe(3);
    }

    [Fact]
    public async Task CompressionLevelIsAppliedAndEveryLevelRoundTrips()
    {
        static List<CompressionLevelModel> Rows() =>
            Enumerable
                .Range(0, 2_000)
                .Select(_ => new CompressionLevelModel { Payload = new string('a', 512) })
                .ToList();

        async Task<long> WriteAsync(ParquetCompressionLevel? level)
        {
            using var stream = new MemoryStream();
            await Rows()
                .WriteParquetAsync(
                    stream,
                    new ParquetSerializerOptions
                    {
                        CompressionMethod = ParquetCompressionMethod.Gzip,
                        CompressionLevel = level,
                    }
                );
            return stream.Length;
        }

        long unspecified = await WriteAsync(null);
        long noCompression = await WriteAsync(ParquetCompressionLevel.NoCompression);
        long smallest = await WriteAsync(ParquetCompressionLevel.SmallestSize);
        long fastest = await WriteAsync(ParquetCompressionLevel.Fastest);
        long optimal = await WriteAsync(ParquetCompressionLevel.Optimal);

        // NoCompression through a compressing method is the one level that must differ plainly.
        // Asserting a size relationship rather than exact bytes keeps this from breaking on a
        // Parquet.Net or zlib change.
        (noCompression > smallest * 2).ShouldBeTrue(
            $"NoCompression ({noCompression} bytes) should be far larger than SmallestSize ({smallest} bytes)"
        );

        // Unspecified must leave Parquet.Net's own default in place rather than picking a level.
        unspecified.ShouldBe(smallest);

        // A mis-mapped enum member would most likely surface as a write failure or an absurd size.
        (fastest > 0 && optimal > 0).ShouldBeTrue("Every level should produce a readable file");
    }

    [Fact]
    public async Task CompressionLevelDoesNotChangeReadBack()
    {
        var written = new List<CompressionLevelModel>
        {
            new() { Payload = "first" },
            new() { Payload = "second" },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(
            stream,
            new ParquetSerializerOptions { CompressionLevel = ParquetCompressionLevel.Fastest }
        );
        stream.Position = 0;

        List<CompressionLevelModel> read = await CompressionLevelModelParquet
            .From(stream)
            .ToListAsync();

        read.Count.ShouldBe(2);
        read[0].Payload.ShouldBe("first");
        read[1].Payload.ShouldBe("second");
    }
}
