using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record DeclarationOrderModel
{
    [ParquetColumn("zzz")]
    public string Zzz { get; init; } = string.Empty;

    [ParquetColumn("aaa")]
    public int Aaa { get; init; }
}

public sealed class DeclarationOrderTests
{
    [Fact]
    public async Task ColumnsAreOrderedByDeclarationOrderWhenOrderIsUnspecified()
    {
        var written = new List<DeclarationOrderModel>
        {
            new() { Zzz = "last_letter", Aaa = 42 },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        List<DeclarationOrderModel> read =
            await DeclarationOrderModelParquetExtensions.ReadParquetAsync(stream);

        read.ShouldHaveSingleItem();
        read[0].Zzz.ShouldBe("last_letter");
        read[0].Aaa.ShouldBe(42);
    }
}
