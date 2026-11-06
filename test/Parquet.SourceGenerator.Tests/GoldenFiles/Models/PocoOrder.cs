// Model declarations for PocoOrderParquetExtensions.g.cs. See OrderEvent.cs for why this exists.
// Mirrors GoldenCodeGenRegressionTests.GoldenMasterListOfPocoModel.
namespace SampleDomain.Models;

public partial class PitStop
{
    public string? City { get; init; }

    public int? Zip { get; init; }

    public System.Guid Node { get; init; }
}

public partial record PocoOrder
{
    public int Id { get; init; }

    public System.Collections.Generic.List<PitStop>? Stops { get; init; }

    public PitStop[]? Route { get; init; }
}
