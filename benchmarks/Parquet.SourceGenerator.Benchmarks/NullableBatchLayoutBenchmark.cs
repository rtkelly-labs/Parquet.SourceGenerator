using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;

namespace Parquet.SourceGenerator.Benchmarks;

/// <summary>
/// Issue #508 experiment: which nullable column layout should the unified <c>&lt;Model&gt;Batch</c>
/// type use? Three candidates for one nullable column of <c>T</c>:
/// <list type="bullet">
/// <item><b>A</b> — packed non-null values plus definition levels (what Parquet.Net's
/// <c>WriteAllPartsAsync</c> and read decoder handle without an intermediate buffer).</item>
/// <item><b>B</b> — <c>T?</c> per row, same length as the row count.</item>
/// <item><b>C</b> — store A and offer a <c>T?</c> accessor that materialises on demand (cached per
/// batch, or rebuilt on every call).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Everything here is benchmark-local and changes no shipped API. The extraction routine is a
/// copy of the branchless one in <c>ColumnarNullExtractionProbe</c> (PR #210's shape), not a
/// second shipped copy.
/// </para>
/// <para>
/// Four operations, each measured per layout: WRITE handoff (obtain the buffers the columnar
/// writer takes), READ decode (produce the batch from decoded packed values + levels), CONSUMER
/// (sum of non-null values and count of nulls, written naturally per layout) and memory
/// (<c>MemoryDiagnoser</c> allocation plus the residency printed by
/// <c>PSG_LAYOUT_PRINT_BYTES=1</c>). Null placement is pseudo-random with a fixed seed so branchy
/// loops see unpredictable nulls, as real data would.
/// </para>
/// <para>
/// The string-like column uses <c>ReadOnlyMemory&lt;char&gt;</c> as <c>T</c>. Its upstream wire
/// shape is already the inline-nullable <c>ReadOnlyMemory&lt;ReadOnlyMemory&lt;char&gt;?&gt;</c>
/// (<c>WriteAsync</c> is constrained <c>where T : struct</c>), so for strings the write handoff
/// cost is inverted: B is free and A/C must expand. <see cref="ILayoutElement{T}.WireIsNullableInline"/>
/// encodes that.
/// </para>
/// </remarks>
public interface ILayoutElement<T>
    where T : struct
{
    static abstract T Make(int index);

    /// <summary>Folds a value into the consumer's running sum (length for text).</summary>
    static abstract long Weight(T value);

    /// <summary>True when the writer's wire shape is <c>T?</c> per row rather than packed + levels.</summary>
    static abstract bool WireIsNullableInline { get; }
}

public readonly struct Int64Element : ILayoutElement<long>
{
    public static long Make(int index) => index * 7L + 1;

    public static long Weight(long value) => value;

    public static bool WireIsNullableInline => false;
}

public readonly struct Int32Element : ILayoutElement<int>
{
    public static int Make(int index) => index * 7 + 1;

    public static long Weight(int value) => value;

    public static bool WireIsNullableInline => false;
}

public readonly struct DoubleElement : ILayoutElement<double>
{
    public static double Make(int index) => index * 0.5 + 1;

    public static long Weight(double value) => (long)value;

    public static bool WireIsNullableInline => false;
}

public readonly struct TextElement : ILayoutElement<ReadOnlyMemory<char>>
{
    private static readonly ReadOnlyMemory<char>[] Pool =
    [
        "A".AsMemory(),
        "DELIVER IN PERSON".AsMemory(),
        "TRUCK".AsMemory(),
        "line item comment 12345".AsMemory(),
    ];

    public static ReadOnlyMemory<char> Make(int index) => Pool[index & 3];

    public static long Weight(ReadOnlyMemory<char> value) => value.Length;

    public static bool WireIsNullableInline => true;
}

/// <summary>Benchmark-local column builders and the layout transforms under test.</summary>
internal static class NullableLayoutKernels
{
    /// <summary>Branchless single pass: copy of <c>ColumnarNullExtractionProbe.ExtractBranchless</c>.</summary>
    public static int ExtractBranchless<T>(
        ReadOnlySpan<T?> source,
        Span<int> definitionLevels,
        Span<T> values
    )
        where T : struct
    {
        int length = source.Length;
        int packed = 0;
        for (int i = 0; i < length; i++)
        {
            T? slot = source[i];
            int flag = slot.HasValue ? 1 : 0;
            values[packed] = slot.GetValueOrDefault();
            packed += flag;
            definitionLevels[i] = flag;
        }

        return packed;
    }

    /// <summary>Packed + levels to <c>T?</c>, conditional per row (the obvious decoder loop).</summary>
    public static void ExpandBranchy<T>(
        ReadOnlySpan<T> packed,
        ReadOnlySpan<int> definitionLevels,
        Span<T?> destination
    )
        where T : struct
    {
        int valueIndex = 0;
        for (int i = 0; i < definitionLevels.Length; i++)
        {
            destination[i] = definitionLevels[i] != 0 ? (T?)packed[valueIndex++] : null;
        }
    }

    /// <summary>
    /// Packed + levels to <c>T?</c> with the value index advanced arithmetically. The bounds guard
    /// keeps the read in range when trailing rows are null; it is perfectly predictable.
    /// </summary>
    public static void ExpandBranchless<T>(
        ReadOnlySpan<T> packed,
        ReadOnlySpan<int> definitionLevels,
        Span<T?> destination
    )
        where T : struct
    {
        int valueIndex = 0;
        int packedLength = packed.Length;
        for (int i = 0; i < definitionLevels.Length; i++)
        {
            int flag = definitionLevels[i];
            T value = valueIndex < packedLength ? packed[valueIndex] : default;
            destination[i] = flag != 0 ? value : default(T?);
            valueIndex += flag;
        }
    }

    public static bool IsNull(Random rng, int nullPercent) =>
        nullPercent != 0 && rng.Next(100) < nullPercent;
}

/// <summary>One nullable column held in all three storage forms, built once in setup.</summary>
internal sealed class ColumnData<T>
    where T : struct
{
    public ColumnData(int count, int nullPercent, Random rng, Func<int, T> make)
    {
        Nullable = new T?[count];
        for (int i = 0; i < count; i++)
        {
            Nullable[i] = NullableLayoutKernels.IsNull(rng, nullPercent) ? null : make(i);
        }

        Packed = new T[count];
        Levels = new int[count];
        PackedCount = NullableLayoutKernels.ExtractBranchless<T>(Nullable, Levels, Packed);
    }

    public readonly T?[] Nullable;

    public readonly T[] Packed;

    public readonly int[] Levels;

    public int PackedCount { get; }

    public ReadOnlyMemory<T> PackedMemory => Packed.AsMemory(0, PackedCount);

    public ReadOnlyMemory<int> LevelMemory => Levels;
}

/// <summary>
/// Layout C: layout A storage plus a <c>T?</c> accessor. The cached accessor builds lazily on first
/// call and keeps the array for the batch's lifetime (no dispose in this model, so it is a plain
/// GC array). The uncached accessors rebuild on every call.
/// </summary>
internal sealed class LayoutCBatch<T>
    where T : struct
{
    private readonly ReadOnlyMemory<T> _values;
    private readonly ReadOnlyMemory<int> _levels;
    private T?[]? _cache;

    public LayoutCBatch(ReadOnlyMemory<T> values, ReadOnlyMemory<int> levels)
    {
        _values = values;
        _levels = levels;
    }

    public ReadOnlyMemory<T> Values => _values;

    public ReadOnlyMemory<int> Levels => _levels;

    public ReadOnlySpan<T?> CachedNullable()
    {
        T?[]? cache = _cache;
        if (cache is null)
        {
            cache = new T?[_levels.Length];
            NullableLayoutKernels.ExpandBranchless<T>(_values.Span, _levels.Span, cache);
            _cache = cache;
        }

        return cache;
    }

    public T?[] AllocNullable()
    {
        var array = new T?[_levels.Length];
        NullableLayoutKernels.ExpandBranchless<T>(_values.Span, _levels.Span, array);
        return array;
    }

    public T?[] RentNullable()
    {
        T?[] array = ArrayPool<T?>.Shared.Rent(_levels.Length);
        NullableLayoutKernels.ExpandBranchless<T>(
            _values.Span,
            _levels.Span,
            array.AsSpan(0, _levels.Length)
        );
        return array;
    }
}

/// <summary>Per-column micro benchmarks; one concrete subclass per element type below.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Benchmark descriptions and naming follow the existing benchmark files."
)]
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "BenchmarkDotNet discovers the Counts parameter source by name on the concrete subclass."
)]
public abstract class NullableBatchLayoutBenchmarkBase<T, TElement>
    where T : struct
    where TElement : struct, ILayoutElement<T>
{
    private ColumnData<T> _column = null!;
    private LayoutCBatch<T> _warmC = null!;
    private T[] _decodedPacked = null!;
    private int[] _decodedLevels = null!;

    [ParamsSource(nameof(Counts))]
    public int Count { get; set; }

    public static IEnumerable<int> Counts => BenchmarkParameterSource.GetCounts(10_000, 50_000);

    /// <summary>Share of rows that are null. Pseudo-random placement, fixed seed.</summary>
    [Params(0, 6, 50)]
    public int NullPercent { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _column = new ColumnData<T>(
            Count,
            NullPercent,
            new Random(508 + NullPercent),
            TElement.Make
        );
        _warmC = new LayoutCBatch<T>(_column.PackedMemory, _column.LevelMemory);
        _warmC.CachedNullable();
        _decodedPacked = _column.Packed;
        _decodedLevels = _column.Levels;

        if (Environment.GetEnvironmentVariable("PSG_LAYOUT_PRINT_BYTES") == "1")
        {
            int packedBytes = _column.PackedCount * Unsafe.SizeOf<T>();
            int sizeA = packedBytes + Count * sizeof(int);
            int sizeAByteLevels = packedBytes + Count * sizeof(byte);
            int sizeB = Count * Unsafe.SizeOf<T?>();
            Console.WriteLine(
                $"LAYOUT-BYTES {typeof(T).Name} rows={Count} null%={NullPercent} "
                    + $"A(int levels)={sizeA} A(byte levels)={sizeAByteLevels} B={sizeB} "
                    + $"C(first access, A+B)={sizeA + sizeB}"
            );
        }
    }

    // ─────────────────────────── WRITE handoff ───────────────────────────

    /// <summary>
    /// Layout A (and C, which stores A). Numeric: hand the packed values and levels straight over,
    /// nothing to do. Text: the wire shape is <c>T?</c> per row, so packed + levels must be expanded.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Write A/C: packed + levels handoff")]
    [BenchmarkCategory("1-Write")]
    public long WriteHandoffA()
    {
        if (!TElement.WireIsNullableInline)
        {
            ReadOnlyMemory<T> values = _column.PackedMemory;
            ReadOnlyMemory<int> levels = _column.LevelMemory;
            return values.Length + levels.Length;
        }

        T?[] buffer = ArrayPool<T?>.Shared.Rent(Count);
        NullableLayoutKernels.ExpandBranchless<T>(
            _column.PackedMemory.Span,
            _column.Levels,
            buffer.AsSpan(0, Count)
        );
        long checksum = buffer.Length;
        ArrayPool<T?>.Shared.Return(buffer, clearArray: true);
        return checksum;
    }

    /// <summary>
    /// Layout B. Numeric: one branchless extraction pass into rented packed + levels buffers.
    /// Text: the wire shape is already <c>T?</c> per row, so the handoff is free.
    /// </summary>
    [Benchmark(Description = "Write B: T? spans (extract pass for numeric)")]
    [BenchmarkCategory("1-Write")]
    public long WriteHandoffB()
    {
        if (TElement.WireIsNullableInline)
        {
            ReadOnlyMemory<T?> wire = _column.Nullable;
            return wire.Length;
        }

        T[] values = ArrayPool<T>.Shared.Rent(Count);
        int[] levels = ArrayPool<int>.Shared.Rent(Count);
        int packed = NullableLayoutKernels.ExtractBranchless<T>(
            _column.Nullable,
            levels.AsSpan(0, Count),
            values
        );
        ArrayPool<T>.Shared.Return(values, clearArray: false);
        ArrayPool<int>.Shared.Return(levels, clearArray: false);
        return packed;
    }

    // ─────────────────────────── READ decode ───────────────────────────

    /// <summary>A/C, lower bound: wrap the decoder's packed + level arrays with no copy.</summary>
    [Benchmark(Baseline = true, Description = "Read A/C: wrap decoded arrays (no copy)")]
    [BenchmarkCategory("2-Read")]
    public long ReadWrapA()
    {
        ReadOnlyMemory<T> values = _decodedPacked.AsMemory(0, _column.PackedCount);
        ReadOnlyMemory<int> levels = _decodedLevels;
        return values.Length + levels.Length;
    }

    /// <summary>A/C when the batch must own its buffers (the #369 pooled-ownership model): copy into rentals.</summary>
    [Benchmark(Description = "Read A/C: copy into owned pooled buffers")]
    [BenchmarkCategory("2-Read")]
    public long ReadCopyA()
    {
        T[] values = ArrayPool<T>.Shared.Rent(_column.PackedCount);
        int[] levels = ArrayPool<int>.Shared.Rent(Count);
        _decodedPacked.AsSpan(0, _column.PackedCount).CopyTo(values);
        _decodedLevels.AsSpan(0, Count).CopyTo(levels);
        long checksum = values.Length + levels.Length;
        ArrayPool<T>.Shared.Return(values, clearArray: false);
        ArrayPool<int>.Shared.Return(levels, clearArray: false);
        return checksum;
    }

    [Benchmark(Description = "Read B: expand to T? (conditional loop)")]
    [BenchmarkCategory("2-Read")]
    public long ReadExpandBranchyB()
    {
        T?[] buffer = ArrayPool<T?>.Shared.Rent(Count);
        NullableLayoutKernels.ExpandBranchy<T>(
            _decodedPacked.AsSpan(0, _column.PackedCount),
            _decodedLevels.AsSpan(0, Count),
            buffer.AsSpan(0, Count)
        );
        long checksum = buffer.Length;
        ArrayPool<T?>.Shared.Return(buffer, clearArray: true);
        return checksum;
    }

    [Benchmark(Description = "Read B: expand to T? (arithmetic index)")]
    [BenchmarkCategory("2-Read")]
    public long ReadExpandBranchlessB()
    {
        T?[] buffer = ArrayPool<T?>.Shared.Rent(Count);
        NullableLayoutKernels.ExpandBranchless<T>(
            _decodedPacked.AsSpan(0, _column.PackedCount),
            _decodedLevels.AsSpan(0, Count),
            buffer.AsSpan(0, Count)
        );
        long checksum = buffer.Length;
        ArrayPool<T?>.Shared.Return(buffer, clearArray: true);
        return checksum;
    }

    // ─────────────────────────── CONSUMER ───────────────────────────

    /// <summary>A, positional: walk the levels with a running value index.</summary>
    [Benchmark(Baseline = true, Description = "Consume A: walk levels + running index")]
    [BenchmarkCategory("3-Consume")]
    public long ConsumeLevelsA()
    {
        ReadOnlySpan<T> values = _column.PackedMemory.Span;
        ReadOnlySpan<int> levels = _column.Levels;
        long sum = 0;
        long nulls = 0;
        int valueIndex = 0;
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] != 0)
            {
                sum += TElement.Weight(values[valueIndex++]);
            }
            else
            {
                nulls++;
            }
        }

        return sum * 31 + nulls;
    }

    /// <summary>A, aggregate: a consumer that does not care where the nulls are just sums the packed lane.</summary>
    [Benchmark(Description = "Consume A: aggregate only (sum packed, nulls = rows - packed)")]
    [BenchmarkCategory("3-Consume")]
    public long ConsumeAggregateA()
    {
        ReadOnlySpan<T> values = _column.PackedMemory.Span;
        long sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += TElement.Weight(values[i]);
        }

        return sum * 31 + (Count - values.Length);
    }

    [Benchmark(Description = "Consume B: T? loop with HasValue")]
    [BenchmarkCategory("3-Consume")]
    public long ConsumeNullableB() => SumNullable(_column.Nullable);

    [Benchmark(Description = "Consume C: cached accessor, first access (build + loop)")]
    [BenchmarkCategory("3-Consume")]
    public long ConsumeCachedFirstC()
    {
        var batch = new LayoutCBatch<T>(_column.PackedMemory, _column.LevelMemory);
        return SumNullable(batch.CachedNullable());
    }

    [Benchmark(Description = "Consume C: cached accessor, warm (loop only)")]
    [BenchmarkCategory("3-Consume")]
    public long ConsumeCachedWarmC() => SumNullable(_warmC.CachedNullable());

    [Benchmark(Description = "Consume C: uncached accessor, rented each call")]
    [BenchmarkCategory("3-Consume")]
    public long ConsumeUncachedRentC()
    {
        T?[] array = _warmC.RentNullable();
        long result = SumNullable(array.AsSpan(0, Count));
        ArrayPool<T?>.Shared.Return(array, clearArray: true);
        return result;
    }

    [Benchmark(Description = "Consume C: uncached accessor, allocated each call")]
    [BenchmarkCategory("3-Consume")]
    public long ConsumeUncachedAllocC() => SumNullable(_warmC.AllocNullable());

    private static long SumNullable(ReadOnlySpan<T?> column)
    {
        long sum = 0;
        long nulls = 0;
        foreach (T? item in column)
        {
            if (item.HasValue)
            {
                sum += TElement.Weight(item.GetValueOrDefault());
            }
            else
            {
                nulls++;
            }
        }

        return sum * 31 + nulls;
    }
}

[MemoryDiagnoser]
[InProcess]
[WarmupCount(3)]
[IterationCount(15)]
[IterationTime(250)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[CategoriesColumn]
public class NullableLayoutInt64Benchmark : NullableBatchLayoutBenchmarkBase<long, Int64Element>;

[MemoryDiagnoser]
[InProcess]
[WarmupCount(3)]
[IterationCount(15)]
[IterationTime(250)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[CategoriesColumn]
public class NullableLayoutInt32Benchmark : NullableBatchLayoutBenchmarkBase<int, Int32Element>;

[MemoryDiagnoser]
[InProcess]
[WarmupCount(3)]
[IterationCount(15)]
[IterationTime(250)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[CategoriesColumn]
public class NullableLayoutDoubleBenchmark
    : NullableBatchLayoutBenchmarkBase<double, DoubleElement>;

[MemoryDiagnoser]
[InProcess]
[WarmupCount(3)]
[IterationCount(15)]
[IterationTime(250)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[CategoriesColumn]
public class NullableLayoutTextBenchmark
    : NullableBatchLayoutBenchmarkBase<ReadOnlyMemory<char>, TextElement>;

/// <summary>
/// The same question on the 16-column TPC-H line item schema docs/12 uses: 11 nullable value
/// columns (4 x long?, 4 x decimal?, 3 x DateTime?) and 5 nullable string columns, with the chosen
/// null density applied to every nullable column. End-to-end write goes through the generated
/// <c>WriteParquetAsync</c> of <c>BenchmarkTpchLineItemColumnarBatch</c>, so layout A is the
/// shipped direct handoff and layout B pays the extraction pass first. Layout C stores A, so its
/// write cost is A's. String columns are inline-nullable on the wire in every layout and are the
/// same buffers in both.
/// </summary>
[MemoryDiagnoser]
[InProcess]
[WarmupCount(3)]
[IterationCount(15)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[CategoriesColumn]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Benchmark descriptions and naming follow the existing benchmark files."
)]
public class NullableLayoutTpchBenchmark
{
    private ColumnData<long> _orderKey = null!;
    private ColumnData<long> _partKey = null!;
    private ColumnData<long> _suppKey = null!;
    private ColumnData<long> _lineNumber = null!;
    private ColumnData<decimal> _quantity = null!;
    private ColumnData<decimal> _extendedPrice = null!;
    private ColumnData<decimal> _discount = null!;
    private ColumnData<decimal> _tax = null!;
    private ColumnData<DateTime> _shipDate = null!;
    private ColumnData<DateTime> _commitDate = null!;
    private ColumnData<DateTime> _receiptDate = null!;
    private ReadOnlyMemory<char>?[] _returnFlag = null!;
    private ReadOnlyMemory<char>?[] _lineStatus = null!;
    private ReadOnlyMemory<char>?[] _shipInstruct = null!;
    private ReadOnlyMemory<char>?[] _shipMode = null!;
    private ReadOnlyMemory<char>?[] _comment = null!;
    private BenchmarkTpchLineItemColumnarBatch _batchA;

    [ParamsSource(nameof(Counts))]
    public int Count { get; set; }

    public static IEnumerable<int> Counts => BenchmarkParameterSource.GetCounts(10_000, 50_000);

    [Params(0, 6, 50)]
    public int NullPercent { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(508 + NullPercent);
        var baseDate = new DateTime(1996, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _orderKey = new ColumnData<long>(Count, NullPercent, rng, i => i);
        _partKey = new ColumnData<long>(Count, NullPercent, rng, i => i * 3L);
        _suppKey = new ColumnData<long>(Count, NullPercent, rng, i => i * 7L);
        _lineNumber = new ColumnData<long>(Count, NullPercent, rng, i => i % 7);
        _quantity = new ColumnData<decimal>(Count, NullPercent, rng, i => i % 50 + 1m);
        _extendedPrice = new ColumnData<decimal>(Count, NullPercent, rng, i => i * 1.25m);
        _discount = new ColumnData<decimal>(Count, NullPercent, rng, i => i % 10 * 0.01m);
        _tax = new ColumnData<decimal>(Count, NullPercent, rng, _ => 0.08m);
        _shipDate = new ColumnData<DateTime>(
            Count,
            NullPercent,
            rng,
            i => baseDate.AddDays(i % 2000)
        );
        _commitDate = new ColumnData<DateTime>(
            Count,
            NullPercent,
            rng,
            i => baseDate.AddDays(i % 2000 + 3)
        );
        _receiptDate = new ColumnData<DateTime>(
            Count,
            NullPercent,
            rng,
            i => baseDate.AddDays(i % 2000 + 7)
        );
        _returnFlag = Text(rng);
        _lineStatus = Text(rng);
        _shipInstruct = Text(rng);
        _shipMode = Text(rng);
        _comment = Text(rng);

        _batchA = new BenchmarkTpchLineItemColumnarBatch
        {
            RowCount = Count,
            OrderKey = _orderKey.PackedMemory,
            OrderKeyDefinitionLevels = _orderKey.LevelMemory,
            PartKey = _partKey.PackedMemory,
            PartKeyDefinitionLevels = _partKey.LevelMemory,
            SuppKey = _suppKey.PackedMemory,
            SuppKeyDefinitionLevels = _suppKey.LevelMemory,
            LineNumber = _lineNumber.PackedMemory,
            LineNumberDefinitionLevels = _lineNumber.LevelMemory,
            Quantity = _quantity.PackedMemory,
            QuantityDefinitionLevels = _quantity.LevelMemory,
            ExtendedPrice = _extendedPrice.PackedMemory,
            ExtendedPriceDefinitionLevels = _extendedPrice.LevelMemory,
            Discount = _discount.PackedMemory,
            DiscountDefinitionLevels = _discount.LevelMemory,
            Tax = _tax.PackedMemory,
            TaxDefinitionLevels = _tax.LevelMemory,
            ReturnFlag = _returnFlag,
            LineStatus = _lineStatus,
            ShipDate = _shipDate.PackedMemory,
            ShipDateDefinitionLevels = _shipDate.LevelMemory,
            CommitDate = _commitDate.PackedMemory,
            CommitDateDefinitionLevels = _commitDate.LevelMemory,
            ReceiptDate = _receiptDate.PackedMemory,
            ReceiptDateDefinitionLevels = _receiptDate.LevelMemory,
            ShipInstruct = _shipInstruct,
            ShipMode = _shipMode,
            Comment = _comment,
        };
    }

    private ReadOnlyMemory<char>?[] Text(Random rng)
    {
        var column = new ReadOnlyMemory<char>?[Count];
        for (int i = 0; i < Count; i++)
        {
            column[i] = NullableLayoutKernels.IsNull(rng, NullPercent) ? null : TextElement.Make(i);
        }

        return column;
    }

    /// <summary>Layout A (and C): the shipped direct handoff, packed + levels straight into the writer.</summary>
    [Benchmark(Baseline = true, Description = "Write A/C: direct columnar handoff")]
    [BenchmarkCategory("1-Write")]
    public async Task WriteAAsync()
    {
        using var stream = new MemoryStream();
        await _batchA.WriteParquetAsync(stream);
    }

    /// <summary>Layout B: extract the 11 nullable value columns from <c>T?</c> into rented packed + levels, then write.</summary>
    [Benchmark(Description = "Write B: T? columns, extract then handoff")]
    [BenchmarkCategory("1-Write")]
    public async Task WriteBAsync()
    {
        int n = Count;
        var c0 = new Pooled<long>(n);
        var c1 = new Pooled<long>(n);
        var c2 = new Pooled<long>(n);
        var c3 = new Pooled<long>(n);
        var c4 = new Pooled<decimal>(n);
        var c5 = new Pooled<decimal>(n);
        var c6 = new Pooled<decimal>(n);
        var c7 = new Pooled<decimal>(n);
        var c8 = new Pooled<DateTime>(n);
        var c9 = new Pooled<DateTime>(n);
        var c10 = new Pooled<DateTime>(n);
        try
        {
            c0.Fill(_orderKey.Nullable);
            c1.Fill(_partKey.Nullable);
            c2.Fill(_suppKey.Nullable);
            c3.Fill(_lineNumber.Nullable);
            c4.Fill(_quantity.Nullable);
            c5.Fill(_extendedPrice.Nullable);
            c6.Fill(_discount.Nullable);
            c7.Fill(_tax.Nullable);
            c8.Fill(_shipDate.Nullable);
            c9.Fill(_commitDate.Nullable);
            c10.Fill(_receiptDate.Nullable);

            var batch = new BenchmarkTpchLineItemColumnarBatch
            {
                RowCount = n,
                OrderKey = c0.Values,
                OrderKeyDefinitionLevels = c0.Levels,
                PartKey = c1.Values,
                PartKeyDefinitionLevels = c1.Levels,
                SuppKey = c2.Values,
                SuppKeyDefinitionLevels = c2.Levels,
                LineNumber = c3.Values,
                LineNumberDefinitionLevels = c3.Levels,
                Quantity = c4.Values,
                QuantityDefinitionLevels = c4.Levels,
                ExtendedPrice = c5.Values,
                ExtendedPriceDefinitionLevels = c5.Levels,
                Discount = c6.Values,
                DiscountDefinitionLevels = c6.Levels,
                Tax = c7.Values,
                TaxDefinitionLevels = c7.Levels,
                ReturnFlag = _returnFlag,
                LineStatus = _lineStatus,
                ShipDate = c8.Values,
                ShipDateDefinitionLevels = c8.Levels,
                CommitDate = c9.Values,
                CommitDateDefinitionLevels = c9.Levels,
                ReceiptDate = c10.Values,
                ReceiptDateDefinitionLevels = c10.Levels,
                ShipInstruct = _shipInstruct,
                ShipMode = _shipMode,
                Comment = _comment,
            };

            using var stream = new MemoryStream();
            await batch.WriteParquetAsync(stream);
        }
        finally
        {
            c0.Release();
            c1.Release();
            c2.Release();
            c3.Release();
            c4.Release();
            c5.Release();
            c6.Release();
            c7.Release();
            c8.Release();
            c9.Release();
            c10.Release();
        }
    }

    /// <summary>The extraction pass on its own (11 columns, no Parquet call): the stable measure of what B adds to a write.</summary>
    [Benchmark(Description = "Write B: extraction pass only")]
    [BenchmarkCategory("2-ExtractOnly")]
    public int ExtractOnlyB()
    {
        int n = Count;
        var c0 = new Pooled<long>(n);
        var c1 = new Pooled<long>(n);
        var c2 = new Pooled<long>(n);
        var c3 = new Pooled<long>(n);
        var c4 = new Pooled<decimal>(n);
        var c5 = new Pooled<decimal>(n);
        var c6 = new Pooled<decimal>(n);
        var c7 = new Pooled<decimal>(n);
        var c8 = new Pooled<DateTime>(n);
        var c9 = new Pooled<DateTime>(n);
        var c10 = new Pooled<DateTime>(n);
        int total =
            c0.Fill(_orderKey.Nullable)
            + c1.Fill(_partKey.Nullable)
            + c2.Fill(_suppKey.Nullable)
            + c3.Fill(_lineNumber.Nullable)
            + c4.Fill(_quantity.Nullable)
            + c5.Fill(_extendedPrice.Nullable)
            + c6.Fill(_discount.Nullable)
            + c7.Fill(_tax.Nullable)
            + c8.Fill(_shipDate.Nullable)
            + c9.Fill(_commitDate.Nullable)
            + c10.Fill(_receiptDate.Nullable);
        c0.Release();
        c1.Release();
        c2.Release();
        c3.Release();
        c4.Release();
        c5.Release();
        c6.Release();
        c7.Release();
        c8.Release();
        c9.Release();
        c10.Release();
        return total;
    }

    /// <summary>A/C lower bound: wrap the decoder's 22 packed + level arrays (11 columns) with no copy.</summary>
    [Benchmark(Baseline = true, Description = "Read A/C: wrap 11 decoded columns")]
    [BenchmarkCategory("3-Read")]
    public long ReadWrapA() =>
        _orderKey.PackedMemory.Length
        + _partKey.PackedMemory.Length
        + _suppKey.PackedMemory.Length
        + _lineNumber.PackedMemory.Length
        + _quantity.PackedMemory.Length
        + _extendedPrice.PackedMemory.Length
        + _discount.PackedMemory.Length
        + _tax.PackedMemory.Length
        + _shipDate.PackedMemory.Length
        + _commitDate.PackedMemory.Length
        + _receiptDate.PackedMemory.Length;

    [Benchmark(Description = "Read A/C: copy 11 columns into pooled buffers")]
    [BenchmarkCategory("3-Read")]
    public long ReadCopyA()
    {
        return CopyOwned(_orderKey)
            + CopyOwned(_partKey)
            + CopyOwned(_suppKey)
            + CopyOwned(_lineNumber)
            + CopyOwned(_quantity)
            + CopyOwned(_extendedPrice)
            + CopyOwned(_discount)
            + CopyOwned(_tax)
            + CopyOwned(_shipDate)
            + CopyOwned(_commitDate)
            + CopyOwned(_receiptDate);
    }

    private static long CopyOwned<T>(ColumnData<T> column)
        where T : struct
    {
        T[] values = ArrayPool<T>.Shared.Rent(column.PackedCount);
        int[] levels = ArrayPool<int>.Shared.Rent(column.Levels.Length);
        column.Packed.AsSpan(0, column.PackedCount).CopyTo(values);
        column.Levels.CopyTo(levels, 0);
        long total = values.Length + levels.Length;
        ArrayPool<T>.Shared.Return(values, clearArray: false);
        ArrayPool<int>.Shared.Return(levels, clearArray: false);
        return total;
    }

    [Benchmark(Description = "Read B: expand 11 columns to T? (arithmetic index)")]
    [BenchmarkCategory("3-Read")]
    public long ReadExpandB() =>
        Expand(_orderKey)
        + Expand(_partKey)
        + Expand(_suppKey)
        + Expand(_lineNumber)
        + Expand(_quantity)
        + Expand(_extendedPrice)
        + Expand(_discount)
        + Expand(_tax)
        + Expand(_shipDate)
        + Expand(_commitDate)
        + Expand(_receiptDate);

    private int Expand<T>(ColumnData<T> column)
        where T : struct
    {
        T?[] buffer = ArrayPool<T?>.Shared.Rent(Count);
        NullableLayoutKernels.ExpandBranchless<T>(
            column.Packed.AsSpan(0, column.PackedCount),
            column.Levels.AsSpan(0, Count),
            buffer.AsSpan(0, Count)
        );
        int length = buffer.Length;
        ArrayPool<T?>.Shared.Return(buffer, clearArray: false);
        return length;
    }

    /// <summary>Rented packed + levels buffers for one extracted column.</summary>
    private struct Pooled<T>
        where T : struct
    {
        private readonly T[] _values;
        private readonly int[] _levels;
        private readonly int _count;
        private int _packed;

        public Pooled(int count)
        {
            _count = count;
            _values = ArrayPool<T>.Shared.Rent(count);
            _levels = ArrayPool<int>.Shared.Rent(count);
            _packed = 0;
        }

        public readonly ReadOnlyMemory<T> Values => _values.AsMemory(0, _packed);

        public readonly ReadOnlyMemory<int> Levels => _levels.AsMemory(0, _count);

        public int Fill(T?[] source)
        {
            _packed = NullableLayoutKernels.ExtractBranchless<T>(
                source,
                _levels.AsSpan(0, _count),
                _values
            );
            return _packed;
        }

        public readonly void Release()
        {
            ArrayPool<T>.Shared.Return(_values, clearArray: false);
            ArrayPool<int>.Shared.Return(_levels, clearArray: false);
        }
    }
}
