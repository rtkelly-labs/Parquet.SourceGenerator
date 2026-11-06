// Model declarations for ListOrderParquetExtensions.g.cs. See OrderEvent.cs for why this exists.
// Mirrors GoldenCodeGenRegressionTests.GoldenMasterRowLevelListsModel.
namespace SampleDomain.Models;

public partial record ListOrder
{
    public int Id { get; init; }

    public System.Collections.Generic.List<string?>? Tags { get; init; }

    public System.Collections.Generic.List<int> Scores { get; init; } = new();

    public System.Guid[]? Keys { get; init; }
}
