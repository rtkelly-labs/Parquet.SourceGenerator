using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Analytical model for the struct-of-arrays batch API (#147): a couple of numeric columns worth
/// aggregating, one nullable column, and one string column so the reference-type buffer path is
/// exercised too.
/// </summary>
[ParquetSerializable]
public partial record ColumnBatchOrder
{
    [ParquetColumn("order_id")]
    public long OrderId { get; init; }

    [ParquetColumn("amount")]
    public double Amount { get; init; }

    [ParquetColumn("discount")]
    public double? Discount { get; init; }

    [ParquetColumn("region")]
    public string Region { get; init; } = string.Empty;
}

/// <summary>
/// Numeric-only model. Strings allocate one object per row no matter which reader decodes them,
/// so the allocation assertions use this schema to isolate what the SoA path actually removes:
/// the domain objects and their list.
/// </summary>
[ParquetSerializable]
public partial record ColumnBatchMetric
{
    [ParquetColumn("ts")]
    public long Timestamp { get; init; }

    [ParquetColumn("value")]
    public double Value { get; init; }

    [ParquetColumn("weight")]
    public double Weight { get; init; }
}

/// <summary>Compound model — the batch API must not be emitted for it.</summary>
[ParquetSerializable]
public partial record ColumnBatchWithList
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("tags")]
    public List<int> Tags { get; init; } = new();
}

[Collection(AllocationMeasurementSuite.Name)]
public sealed class ColumnBatchReadTests
{
    private static readonly int[] ExpectedGroupSizes = { 2, 2, 2, 1 };

    private static readonly PropertyModel[] FlatProperties =
    {
        new("Id", "id", "int", null, null, 1, null, null, PropertyKind.Primitive, false),
        new("Name", "name", "string", null, null, 2, null, null, PropertyKind.Primitive, false),
    };

    private static List<ColumnBatchOrder> SampleRows(int count) =>
        Enumerable
            .Range(1, count)
            .Select(i => new ColumnBatchOrder
            {
                OrderId = i,
                Amount = i * 1.5,
                Discount = i % 3 == 0 ? null : i * 0.01,
                Region = i % 2 == 0 ? "emea" : "amer",
            })
            .ToList();

    private static async Task<MemoryStream> WriteAsync(
        List<ColumnBatchOrder> rows,
        int rowGroupSize
    )
    {
        var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
        );
        stream.Position = 0;
        return stream;
    }

    // ── Emission shape ────────────────────────────────────────────────

    [Fact]
    public void FlatModelEmitsBatchStructAndBatchReader()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(FlatProperties)
        );

        string source = CodeEmitter.EmitSource(model);

        source.ShouldContain("public readonly struct ColumnBatch");
        source.ShouldContain(
            "internal static async global::System.Collections.Generic.IAsyncEnumerable<ColumnBatch> ReadBatchesCoreAsync("
        );
        source.ShouldContain("public global::System.ReadOnlySpan<int> IdSpan =>");
        source.ShouldContain("public global::System.ReadOnlySpan<string> NameSpan =>");
        // ReadOnlyMemory<byte> overload comes along for the ride.
        source.ShouldContain("global::System.ReadOnlyMemory<byte> parquetBytes,");
    }

    [Fact]
    public void BatchReaderConstructsNoDomainObjects()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(FlatProperties)
        );

        string source = CodeEmitter.EmitSource(model);
        int start = source.IndexOf(
            "IAsyncEnumerable<ColumnBatch> ReadBatchesCoreAsync(",
            StringComparison.Ordinal
        );
        (start > 0).ShouldBeTrue();
        string batchApi = source.Substring(start);

        // The only allocation in the batch path is the batch struct itself (a value type) — no
        // `new TestEntity` anywhere below the batch reader.
        // Matched as a whole identifier rather than a prefix: the slice runs to the end of the
        // emitted file, which since #217 also contains the read builder, and
        // `new TestEntityParquetReader(...)` starts with the domain type's name without
        // constructing one.
        batchApi.ShouldNotMatch(@"new TestEntity(?![A-Za-z0-9_])");
    }

    [Fact]
    public void BatchReaderRentsAndReturnsPooledBuffers()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(FlatProperties)
        );

        string source = CodeEmitter.EmitSource(model);
        int start = source.IndexOf(
            "IAsyncEnumerable<ColumnBatch> ReadBatchesCoreAsync(",
            StringComparison.Ordinal
        );
        string batchApi = source.Substring(start);

        batchApi.ShouldContain("global::System.Buffers.ArrayPool<int>.Shared.Rent(rowCount);");
        // The return sits in the finally that runs on the consumer's next MoveNextAsync or on
        // disposal — that is what makes the buffers safe to hand out as spans.
        int yieldIndex = batchApi.IndexOf("yield return new ColumnBatch", StringComparison.Ordinal);
        int finallyIndex = batchApi.IndexOf("finally", StringComparison.Ordinal);
        int returnIndex = batchApi.IndexOf(
            "global::System.Buffers.ArrayPool<int>.Shared.Return(buffer_0",
            StringComparison.Ordinal
        );
        (yieldIndex > 0 && finallyIndex > yieldIndex && returnIndex > finallyIndex).ShouldBeTrue();
    }

    [Fact]
    public void CompoundModelGetsNoBatchApi()
    {
        var properties = new[]
        {
            new PropertyModel(
                "Id",
                "id",
                "int",
                null,
                null,
                1,
                null,
                null,
                PropertyKind.Primitive,
                false
            ),
            new PropertyModel(
                "Tags",
                "tags",
                "global::System.Collections.Generic.List<int>",
                null,
                null,
                2,
                null,
                null,
                PropertyKind.List,
                false
            )
            {
                Element = new PropertyModel(
                    "Item",
                    "item",
                    "int",
                    null,
                    null,
                    0,
                    null,
                    null,
                    PropertyKind.Primitive,
                    false
                ),
            },
        };

        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(properties)
        );

        string source = CodeEmitter.EmitSource(model);

        source.ShouldNotContain("ColumnBatch");
        source.ShouldNotContain("ReadBatchesCoreAsync");
    }

    // ── Runtime behaviour ─────────────────────────────────────────────

    [Fact]
    public async Task BatchesCoverEveryRowInRowGroupOrder()
    {
        List<ColumnBatchOrder> rows = SampleRows(7);
        using MemoryStream stream = await WriteAsync(rows, rowGroupSize: 2);

        var seenIds = new List<long>();
        var seenAmounts = new List<double>();
        var seenDiscounts = new List<double?>();
        var seenRegions = new List<string>();
        var groupSizes = new List<int>();
        int expectedGroupIndex = 0;

        await foreach (
            ColumnBatchOrderParquetExtensions.ColumnBatch batch in ColumnBatchOrderParquet
                .From(stream)
                .Batches()
        )
        {
            batch.RowGroupIndex.ShouldBe(expectedGroupIndex++);
            groupSizes.Add(batch.RowCount);

            ReadOnlySpan<long> ids = batch.OrderIdSpan;
            ReadOnlySpan<double> amounts = batch.AmountSpan;
            ReadOnlySpan<double?> discounts = batch.DiscountSpan;
            ReadOnlySpan<string> regions = batch.RegionSpan;

            ids.Length.ShouldBe(batch.RowCount);
            amounts.Length.ShouldBe(batch.RowCount);
            discounts.Length.ShouldBe(batch.RowCount);
            regions.Length.ShouldBe(batch.RowCount);

            for (int i = 0; i < batch.RowCount; i++)
            {
                seenIds.Add(ids[i]);
                seenAmounts.Add(amounts[i]);
                seenDiscounts.Add(discounts[i]);
                seenRegions.Add(regions[i]);
            }
        }

        groupSizes.ShouldBe(ExpectedGroupSizes);
        seenIds.ShouldBe(rows.Select(r => r.OrderId));
        seenAmounts.ShouldBe(rows.Select(r => r.Amount));
        seenDiscounts.ShouldBe(rows.Select(r => r.Discount));
        seenRegions.ShouldBe(rows.Select(r => r.Region));
    }

    [Fact]
    public async Task BatchAggregationMatchesPocoAggregation()
    {
        List<ColumnBatchOrder> rows = SampleRows(5_000);

        using MemoryStream stream = await WriteAsync(rows, rowGroupSize: 1_000);
        double batchTotal = 0;
        await foreach (
            ColumnBatchOrderParquetExtensions.ColumnBatch batch in ColumnBatchOrderParquet
                .From(stream)
                .Batches()
        )
        {
            ReadOnlySpan<double> amounts = batch.AmountSpan;
            ReadOnlySpan<double?> discounts = batch.DiscountSpan;
            for (int i = 0; i < batch.RowCount; i++)
            {
                batchTotal += amounts[i] * (1 - (discounts[i] ?? 0));
            }
        }

        stream.Position = 0;
        ColumnBatchOrder[] poco = await ColumnBatchOrderParquet.From(stream).ToArrayAsync();
        double pocoTotal = poco.Sum(r => r.Amount * (1 - (r.Discount ?? 0)));

        batchTotal.ShouldBe(pocoTotal, 0.000001);
    }

    [Fact]
    public async Task BatchReaderAllocatesFarLessThanPocoReader()
    {
        using var writeStream = new MemoryStream();
        await SampleMetrics(20_000)
            .WriteParquetBatchedAsync(
                writeStream,
                new ParquetSerializerOptions { RowGroupSize = 5_000 }
            );
        byte[] bytes = writeStream.ToArray();

        // Warm the ArrayPool and every lazily-initialised path so the measurement below sees the
        // steady-state cost rather than first-call setup.
        await SumViaBatchesAsync(bytes);
        await SumViaPocoAsync(bytes);

        long batchAllocated = (
            await AllocationMeasurement.MeasureAsync(() => SumViaBatchesAsync(bytes))
        ).AllocatedBytes;

        long pocoAllocated = (
            await AllocationMeasurement.MeasureAsync(() => SumViaPocoAsync(bytes))
        ).AllocatedBytes;

        // The batch path never pays for the 20k records or the List backing them: measured on an
        // Apple M1 under .NET 9 the split is ~0.5 MB versus ~1.5 MB, a saving of ~48 bytes per
        // row. What the batch path still allocates is Parquet.Net's own decoded page array (one
        // per column per row group, sized to the data) — the public reader surface offers no way
        // to decode straight into a caller-owned buffer, so "zero allocation" here means zero
        // *domain object* allocation, not zero bytes.
        (batchAllocated * 2 < pocoAllocated).ShouldBeTrue(
            $"batch allocated {batchAllocated} bytes, POCO allocated {pocoAllocated} bytes"
        );
        (pocoAllocated - batchAllocated > 20_000 * 30).ShouldBeTrue(
            $"expected the POCOs to cost at least 30 bytes/row more; batch {batchAllocated}, POCO {pocoAllocated}"
        );
    }

    private static List<ColumnBatchMetric> SampleMetrics(int count) =>
        Enumerable
            .Range(1, count)
            .Select(i => new ColumnBatchMetric
            {
                Timestamp = i,
                Value = i * 1.5,
                Weight = i * 0.25,
            })
            .ToList();

    private static async Task<double> SumViaBatchesAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        double total = 0;
        await foreach (
            ColumnBatchMetricParquetExtensions.ColumnBatch batch in ColumnBatchMetricParquet
                .From(stream)
                .Batches()
        )
        {
            ReadOnlySpan<double> values = batch.ValueSpan;
            ReadOnlySpan<double> weights = batch.WeightSpan;
            for (int i = 0; i < values.Length; i++)
            {
                total += values[i] * weights[i];
            }
        }
        return total;
    }

    private static async Task<double> SumViaPocoAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatchMetric[] rows = await ColumnBatchMetricParquet.From(stream).ToArrayAsync();
        double total = 0;
        foreach (ColumnBatchMetric row in rows)
        {
            total += row.Value * row.Weight;
        }
        return total;
    }

    [Fact]
    public async Task MemoryOverloadYieldsTheSameBatches()
    {
        List<ColumnBatchOrder> rows = SampleRows(9);
        using MemoryStream stream = await WriteAsync(rows, rowGroupSize: 4);
        byte[] bytes = stream.ToArray();

        var ids = new List<long>();
        await foreach (
            ColumnBatchOrderParquetExtensions.ColumnBatch batch in ColumnBatchOrderParquet
                .From(new ReadOnlyMemory<byte>(bytes))
                .Batches()
        )
        {
            ReadOnlySpan<long> span = batch.OrderIdSpan;
            for (int i = 0; i < span.Length; i++)
            {
                ids.Add(span[i]);
            }
        }

        ids.ShouldBe(rows.Select(r => r.OrderId));
    }

    [Fact]
    public async Task PooledBuffersAreRecycledAcrossRowGroups()
    {
        // Ten row groups of identical size: if the buffers were leaked rather than returned, the
        // pool would hand out ten distinct arrays. Reading the pool's own accounting is not
        // possible, so this instead proves the far cheaper thing the return buys — steady
        // allocation regardless of how many row groups are traversed.
        using var fewStream = new MemoryStream();
        await SampleMetrics(200)
            .WriteParquetBatchedAsync(
                fewStream,
                new ParquetSerializerOptions { RowGroupSize = 100 }
            );
        using var manyStream = new MemoryStream();
        await SampleMetrics(2_000)
            .WriteParquetBatchedAsync(
                manyStream,
                new ParquetSerializerOptions { RowGroupSize = 100 }
            );
        byte[] fewBytes = fewStream.ToArray();
        byte[] manyBytes = manyStream.ToArray();

        await SumViaBatchesAsync(fewBytes);
        await SumViaBatchesAsync(manyBytes);

        long twoGroups = (
            await AllocationMeasurement.MeasureAsync(() => SumViaBatchesAsync(fewBytes))
        ).AllocatedBytes;

        long twentyGroups = (
            await AllocationMeasurement.MeasureAsync(() => SumViaBatchesAsync(manyBytes))
        ).AllocatedBytes;

        // 10x the row groups must not cost 10x the allocation: the column buffers are recycled,
        // so only the per-group Parquet.Net bookkeeping scales.
        (twentyGroups < twoGroups * 10).ShouldBeTrue(
            $"2 groups allocated {twoGroups} bytes, 20 groups allocated {twentyGroups} bytes"
        );
    }

    [Fact]
    public async Task EmptyFileYieldsNoBatches()
    {
        using MemoryStream stream = await WriteAsync(SampleRows(0), rowGroupSize: 10);

        int batches = 0;
        await foreach (
            ColumnBatchOrderParquetExtensions.ColumnBatch batch in ColumnBatchOrderParquet
                .From(stream)
                .Batches()
        )
        {
            batches += batch.RowCount;
        }

        batches.ShouldBe(0);
    }

    [Fact]
    public void CompoundConsumerTypeHasNoGeneratedBatchApi()
    {
        // Runtime mirror of the emitter-level gate: the generated extensions class for a model
        // with a list member exposes no ColumnBatch nested type.
        Type extensions = typeof(ColumnBatchWithListParquetExtensions);
        extensions.GetNestedType("ColumnBatch").ShouldBeNull();
        extensions
            .GetMethod(
                "ReadBatchesCoreAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            )
            .ShouldBeNull();
    }
}
