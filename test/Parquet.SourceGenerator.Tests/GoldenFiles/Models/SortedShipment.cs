// The model declaration the golden file SortedShipmentParquetExtensions.g.cs is generated for.
//
// Generated code is a fragment: it extends a type the consumer wrote, and on its own it does not
// compile. Metrics computed over a compilation with unresolved types are fiction (see
// docs/21-CODE-METRICS.md), so scripts/CodeMetrics.cs compiles each golden file together with the
// declaration below and REQUIRES zero errors. That makes this file the thing that keeps the
// generated-code metrics honest, and it also buys a real compile check on the emitted output,
// which the golden suite's syntax-only parse never gave.
//
// This mirrors GoldenCodeGenRegressionTests.GoldenMasterSortedAndPrunableModel (#264's combined
// coverage: predicate pushdown and sorted-key pruning emitted into one model, deliberately on
// both sides of the eligibility boundary). It is not compiled into the test assembly
// (GoldenFiles/**/*.cs is Compile-Removed).
namespace SampleDomain.Models;

public sealed class SortedShipment
{
    public long Sequence { get; set; }

    public System.DateTime ShippedAt { get; set; }

    public int WeightGrams { get; set; }

    public string? Carrier { get; set; }
}
