// Model declaration for LegacyRecordParquetLegacyExtensions.g.cs. See OrderEvent.cs for why this
// exists. Mirrors GoldenCodeGenRegressionTests.GoldenMasterLegacyV4V5DataColumnModel.
//
// This one is compiled against Parquet.Net 4.25.0, not 6.1.0: the V5 emitter targets the classic
// DataColumn API and its output does not compile against Parquet.Net 6 at all.
namespace SampleDomain.Models;

public enum AccessLevel
{
    None = 0,
    Read = 1,
}

public sealed class LegacyRecord
{
    public int Id { get; set; }

    public string? Description { get; set; }

    public byte[]? RawData { get; set; }

    public AccessLevel Level { get; set; }
}
