// Shared by test/PackageConsumption (Parquet.Net 6.x, modern generator) and
// test/PackageConsumptionLegacy (Parquet.Net 4.25, classic generator) via <Compile Include=.../>.
//
// The point of sharing the source is that both packages then describe the *same* columns, so a file
// written by one can be handed to the other. That is the cross-version leg of the compatibility
// matrix (issue #168): the current generated writer read by a supported legacy consumer, and the
// legacy writer read by the current consumer — neither of which any in-solution test can cover,
// because a single test assembly can only reference one Parquet.Net at a time.
//
// Plain classes with settable properties on purpose: this source has to compile under the classic
// backend on net472 as well as the modern one on net10.0.

using Parquet.SourceGenerator;

namespace CrossVersionInterop;

/// <summary>The producer schema. Whichever package writes the interop file writes this shape.</summary>
[ParquetSerializable]
public sealed partial class InteropRow
{
    [ParquetColumn("id", Order = 1)]
    public int Id { get; set; }

    [ParquetColumn("name", Order = 2)]
    public string? Name { get; set; }

    [ParquetColumn("value", Order = 3)]
    public double? Value { get; set; }

    [ParquetColumn("ticks", Order = 4)]
    public long Ticks { get; set; }
}

/// <summary>
/// The consumer schema, one version ahead of the producer: the same columns in a different order
/// plus two optional columns the producer's file cannot contain. Reading a producer file with this
/// model exercises name-based resolution and absent-optional-column handling across the version
/// boundary in one go.
/// </summary>
[ParquetSerializable]
public sealed partial class InteropRowEvolved
{
    [ParquetColumn("ticks", Order = 1)]
    public long Ticks { get; set; }

    [ParquetColumn("value", Order = 2)]
    public double? Value { get; set; }

    [ParquetColumn("name", Order = 3)]
    public string? Name { get; set; }

    [ParquetColumn("id", Order = 4)]
    public int Id { get; set; }

    [ParquetColumn("added_note", Order = 5)]
    public string? AddedNote { get; set; }

    [ParquetColumn("added_count", Order = 6)]
    public int? AddedCount { get; set; }
}
