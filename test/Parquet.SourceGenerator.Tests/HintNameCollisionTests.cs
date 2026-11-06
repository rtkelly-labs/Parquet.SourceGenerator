using System.Threading.Tasks;
using Shouldly;
using Xunit;

// Two [ParquetSerializable] types sharing a class name in different namespaces — an Order in Sales
// and one in Billing is an ordinary thing to have. The generator derived its hint name from the
// class name alone, and AddSource throws on a duplicate hint name rather than warning, so this pair
// used to take the entire generator down with CS8785: not just these two types, but every generated
// serializer in the compilation vanished.
//
// The mere presence of these two declarations is the regression test — unqualified, the project
// does not build. The assertions confirm both types really did get their own generated output.

namespace Parquet.SourceGenerator.Tests.Sales
{
    [ParquetSerializable]
    public partial record SharedName
    {
        [ParquetColumn("order_id")]
        public int OrderId { get; init; }
    }
}

namespace Parquet.SourceGenerator.Tests.Billing
{
    [ParquetSerializable]
    public partial record SharedName
    {
        [ParquetColumn("invoice_id")]
        public int InvoiceId { get; init; }
    }
}

namespace Parquet.SourceGenerator.Tests
{
    public sealed class HintNameCollisionTests
    {
        [Fact]
        public void SameClassNameInDifferentNamespacesBothGenerate()
        {
            Sales.SharedNameParquetExtensions.Schema.Fields.Count.ShouldBe(1);
            Billing.SharedNameParquetExtensions.Schema.Fields.Count.ShouldBe(1);

            // Each got its own schema rather than one overwriting the other.
            Sales.SharedNameParquetExtensions.Schema.Fields[0].Name.ShouldBe("order_id");
            Billing.SharedNameParquetExtensions.Schema.Fields[0].Name.ShouldBe("invoice_id");
        }

        [Fact]
        public async Task BothGeneratedSerializersRoundtripIndependently()
        {
            // Called in static form rather than as extension methods. The generated class lands in
            // the model's own namespace, and extension methods are only found in *enclosing*
            // namespaces — so from here, in the parent namespace, `list.WriteParquetAsync(...)` does
            // not resolve. A `using` for each would work but the two types share a name, so the
            // static call is the unambiguous way to exercise both in one file.
            using var salesStream = new System.IO.MemoryStream();
            await Sales.SharedNameParquetExtensions.WriteParquetAsync(
                new System.Collections.Generic.List<Sales.SharedName> { new() { OrderId = 42 } },
                salesStream
            );
            salesStream.Position = 0;
            var sales = await Sales.SharedNameParquet.From(salesStream).ToListAsync();

            using var billingStream = new System.IO.MemoryStream();
            await Billing.SharedNameParquetExtensions.WriteParquetAsync(
                new System.Collections.Generic.List<Billing.SharedName>
                {
                    new() { InvoiceId = 99 },
                },
                billingStream
            );
            billingStream.Position = 0;
            var billing = await Billing.SharedNameParquet.From(billingStream).ToListAsync();

            sales[0].OrderId.ShouldBe(42);
            billing[0].InvoiceId.ShouldBe(99);
        }
    }
}
