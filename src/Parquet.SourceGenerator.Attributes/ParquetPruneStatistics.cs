using System.Diagnostics.CodeAnalysis;

namespace Parquet.SourceGenerator;

/// <summary>
/// Diagnostics recorded by the generated sorted-column lookup overloads
/// (<c>ReadParquetBy&lt;Column&gt;Async</c> / <c>ReadParquet&lt;Column&gt;RangeAsync</c>).
/// <para>
/// Pass an instance to a lookup call to learn whether the file's row-group
/// <c>[Min, Max]</c> statistics certified the key column as sorted, and how many row
/// groups actually had to be decompressed. Nothing on the read path depends on it —
/// it exists so callers, tests and benchmarks can prove that pruning happened rather
/// than infer it from wall-clock time.
/// </para>
/// </summary>
[SuppressMessage(
    "Design",
    "CA1044:Properties should not be write only",
    Justification = "All members are read/write; generated code populates them."
)]
public sealed class ParquetPruneStatistics
{
    /// <summary>Total row groups present in the file.</summary>
    public int RowGroupCount { get; set; }

    /// <summary>Row groups actually opened and decompressed for the answer.</summary>
    public int RowGroupsScanned { get; set; }

    /// <summary>
    /// True when every row group carried usable <c>[Min, Max]</c> statistics for the key
    /// column and the intervals were non-overlapping and non-decreasing, so binary search
    /// was valid. False means the read fell back to a full linear scan.
    /// </summary>
    public bool SortedColumnDetected { get; set; }

    /// <summary>
    /// True when the certified intervals were additionally <em>strictly</em> monotonic
    /// (<c>Max(i) &lt; Min(i+1)</c>), i.e. no key value straddles a row-group boundary.
    /// </summary>
    public bool StrictlyMonotonic { get; set; }

    /// <summary>Index of the first row group read, or -1 when nothing matched.</summary>
    public int FirstRowGroupRead { get; set; } = -1;

    /// <summary>Index of the last row group read, or -1 when nothing matched.</summary>
    public int LastRowGroupRead { get; set; } = -1;

    /// <summary>Row groups skipped without being decompressed.</summary>
    public int RowGroupsPruned => RowGroupCount - RowGroupsScanned;

    /// <summary>Restores the instance to its pre-read state so it can be reused.</summary>
    public void Reset()
    {
        RowGroupCount = 0;
        RowGroupsScanned = 0;
        SortedColumnDetected = false;
        StrictlyMonotonic = false;
        FirstRowGroupRead = -1;
        LastRowGroupRead = -1;
    }
}
