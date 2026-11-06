using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public abstract partial record AuditedEntity
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("created_by")]
    public string CreatedBy { get; init; } = string.Empty;
}

[ParquetSerializable]
public sealed partial record InvoiceRow : AuditedEntity
{
    [ParquetColumn("amount")]
    public double Amount { get; init; }
}

/// <summary>
/// Inherited members used to be dropped without a diagnostic: <c>GetMembers()</c> returns declared
/// members only, so a derived model silently serialized none of its base's columns.
/// </summary>
public sealed class InheritanceTests
{
    private static readonly string[] ExpectedColumnOrder = { "id", "created_by", "amount" };

    [Fact]
    public void SchemaCarriesBaseColumnsBeforeDerivedOnes()
    {
        string[] fields = InvoiceRowParquetExtensions.Schema.Fields.Select(f => f.Name).ToArray();

        // Base-first ordering is deliberate: a derived declaration that shadows a base one replaces
        // it in the base's position, so adding an override never reorders the schema.
        fields.ShouldBe(ExpectedColumnOrder);
    }

    [Fact]
    public async Task InheritedColumnsRoundTrip()
    {
        var written = new List<InvoiceRow>
        {
            new()
            {
                Id = 1,
                CreatedBy = "ada",
                Amount = 12.5,
            },
            new()
            {
                Id = 2,
                CreatedBy = "grace",
                Amount = 99.0,
            },
        };

        using var stream = new MemoryStream();
        await written.WriteParquetAsync(stream);
        stream.Position = 0;

        List<InvoiceRow> read = await InvoiceRowParquetExtensions.ReadParquetAsync(stream);

        read.Count.ShouldBe(2);
        read[0].Id.ShouldBe(1);
        read[0].CreatedBy.ShouldBe("ada");
        read[0].Amount.ShouldBe(12.5);
        read[1].Id.ShouldBe(2);
        read[1].CreatedBy.ShouldBe("grace");
        read[1].Amount.ShouldBe(99.0);
    }
}
