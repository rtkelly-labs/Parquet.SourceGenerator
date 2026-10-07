// The model declaration the golden file ArrowOrderParquetExtensions.g.cs is generated for.
//
// Generated code is a fragment: it extends a type the consumer wrote, and on its own it does not
// compile. Metrics computed over a compilation with unresolved types are fiction (see
// docs/internals/code-quality-and-metrics.md), so scripts/CodeMetrics.cs compiles each golden file together with the
// declaration below and REQUIRES zero errors.
//
// This mirrors the GoldenCorpus model behind GoldenCodeGenRegressionTests.GoldenMasterArrowRecordBatchModel.
// It is not compiled into the test assembly (GoldenModels/**/*.cs is Compile-Removed).
namespace SampleDomain.Models;

public sealed class ArrowOrder
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public double Score { get; set; }

    public decimal Price { get; set; }

    public System.DateTime CreatedAt { get; set; }

    public System.TimeSpan Duration { get; set; }

    public System.Guid CorrelationId { get; set; }

    public byte[]? Payload { get; set; }
}
