// Model declarations for NestedOrderParquetExtensions.g.cs. See OrderEvent.cs for why this exists.
// Mirrors GoldenCodeGenRegressionTests.GoldenMasterNestedStructModel — Point is a struct on
// purpose, because the emitted value-type ladder depends on it.
namespace SampleDomain.Models;

public partial record Address
{
    public string? City { get; init; }

    public int? Zip { get; init; }
}

public partial struct Point
{
    public int X { get; init; }

    public int Y { get; init; }
}

public partial record NestedOrder
{
    public int Id { get; init; }

    public Address? Ship { get; init; }

    public Address Bill { get; init; } = new();

    public Point Origin { get; init; }

    public Point? Start { get; init; }
}
