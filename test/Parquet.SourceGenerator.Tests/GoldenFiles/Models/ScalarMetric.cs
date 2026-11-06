// Model declaration for ScalarMetricParquetExtensions.g.cs. See OrderEvent.cs for why this exists.
// Mirrors GoldenCodeGenRegressionTests.GoldenMasterScalarsAndEnumsModel.
namespace SampleDomain.Models;

public enum ProcessStatus
{
    Pending = 0,
    Complete = 1,
}

public sealed class ScalarMetric
{
    public long RowId { get; set; }

    public bool Flag { get; set; }

    public bool? NullableFlag { get; set; }

    public ProcessStatus StatusCode { get; set; }

    public ProcessStatus? OptionalStatus { get; set; }

    public byte TinyNum { get; set; }

    public short ShortNum { get; set; }

    public float FloatVal { get; set; }
}
