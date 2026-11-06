// The model declaration the golden file OrderEventParquetExtensions.g.cs is generated for.
//
// Generated code is a fragment: it extends a type the consumer wrote, and on its own it does not
// compile. Metrics computed over a compilation with unresolved types are fiction (see
// docs/21-CODE-METRICS.md), so scripts/CodeMetrics.cs compiles each golden file together with the
// declaration below and REQUIRES zero errors. That makes this file the thing that keeps the
// generated-code metrics honest, and it also buys a real compile check on the emitted output,
// which the golden suite's syntax-only parse never gave.
//
// This mirrors GoldenCodeGenRegressionTests.GoldenMasterComprehensiveModernV6Model. It is not
// compiled into the test assembly (GoldenFiles/**/*.cs is Compile-Removed).
namespace SampleDomain.Models;

public sealed class OrderEvent
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public double Score { get; set; }

    public decimal Price { get; set; }

    public System.DateTime CreatedAt { get; set; }

    public System.TimeSpan Duration { get; set; }

    public System.Guid CorrelationId { get; set; }

    public System.Guid? OptionalGuid { get; set; }

    public byte[]? Payload { get; set; }
}
