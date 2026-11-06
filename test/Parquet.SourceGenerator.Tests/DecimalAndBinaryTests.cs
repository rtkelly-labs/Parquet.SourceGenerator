using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

// decimal and byte[] are both supported PropertyKinds, but neither had a round-trip test — they
// appeared only in a PropertyModel constructed by hand and in a diagnostic test, so nothing ever
// wrote one to a Parquet stream and read it back.
//
// They are covered by the Native AOT matrix too, but that runs a natively compiled binary. Without
// coverage here, an ordinary functional break in either would be reported as an AOT failure, which
// points at entirely the wrong cause. These keep the layers honest: this file says "does it work",
// the AOT matrix says "does it still work compiled ahead of time".

[ParquetSerializable]
public partial record MoneyRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("amount")]
    [ParquetDecimal(18, 4)]
    public decimal Amount { get; init; }

    [ParquetColumn("blob")]
    public byte[] Blob { get; init; } = System.Array.Empty<byte>();
}

public sealed class DecimalAndBinaryTests
{
    [Fact]
    public async Task DecimalRoundtripsAtDeclaredScale()
    {
        var written = new List<MoneyRecord>
        {
            new() { Id = 1, Amount = 12345.6789m },
            new() { Id = 2, Amount = -0.0001m },
            new() { Id = 3, Amount = 0m },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        List<MoneyRecord> read = await MoneyRecordParquet.From(stream).ToListAsync();

        read.Count.ShouldBe(3);
        // Scale 4 is declared, so all three values are representable exactly; anything lost here is
        // a precision bug rather than a rounding artifact.
        read[0].Amount.ShouldBe(12345.6789m);
        read[1].Amount.ShouldBe(-0.0001m);
        read[2].Amount.ShouldBe(0m);
    }

    [Fact]
    public async Task ByteArrayRoundtripsIncludingEmptyAndHighBytes()
    {
        var written = new List<MoneyRecord>
        {
            new() { Id = 1, Blob = new byte[] { 0x00, 0x7F, 0x80, 0xFF } },
            new() { Id = 2, Blob = System.Array.Empty<byte>() },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        List<MoneyRecord> read = await MoneyRecordParquet.From(stream).ToListAsync();

        read.Count.ShouldBe(2);
        // High bytes and 0x00 are the values most likely to be mangled by an accidental
        // string conversion somewhere in the pipeline.
        read[0].Blob.ShouldBe(new byte[] { 0x00, 0x7F, 0x80, 0xFF });
        read[1].Blob.ShouldBeEmpty();
    }
}
