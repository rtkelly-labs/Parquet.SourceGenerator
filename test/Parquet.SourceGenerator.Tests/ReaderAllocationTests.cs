using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record MultiRowGroupModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Guards the read path's result-list sizing. The list is pre-sized once to the file's total row
/// count; assigning <c>Capacity</c> again inside the row-group loop reallocated a smaller backing
/// array and copied into it on every group, which cost O(groups x rows) for no benefit.
/// </summary>
public sealed class ReaderAllocationTests
{
    private static readonly PropertyModel[] SingleProperty =
    {
        new("Id", "id", "int", null, null, 1, null, null, PropertyKind.Primitive, false),
    };

    [Fact]
    public void EmittedReaderDoesNotReassignResultCapacityPerRowGroup()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(SingleProperty)
        );

        string source = CodeEmitter.EmitSource(model);

        // Sizing happens upfront from the summed row count, avoiding per-row-group reallocations.
        source.ShouldContain("int totalRows = checked((int)totalRowsLong);");
        source.ShouldContain("new global::System.Collections.Generic.List<TestEntity>(totalRows)");
        source.ShouldNotContain("results.Capacity");
    }

    [Fact]
    public async Task ReadsEveryRowAcrossManyRowGroups()
    {
        // 7 rows at 2 per group => 4 row groups, the last one partial. Reading back the full set in
        // order is what the removed Capacity assignment was silently taxing.
        List<MultiRowGroupModel> written = Enumerable
            .Range(1, 7)
            .Select(i => new MultiRowGroupModel { Id = i, Name = $"Item_{i}" })
            .ToList();

        using var stream = new MemoryStream();
        await written.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 2 }
        );
        stream.Position = 0;

        List<MultiRowGroupModel> read = await MultiRowGroupModelParquetExtensions.ReadParquetAsync(
            stream
        );

        read.Count.ShouldBe(7);
        read.Select(x => x.Id).ShouldBe(written.Select(x => x.Id));
        read.Select(x => x.Name).ShouldBe(written.Select(x => x.Name));
    }

    [Fact]
    public async Task ParallelReaderAgreesWithSequentialReaderAcrossRowGroups()
    {
        List<MultiRowGroupModel> written = Enumerable
            .Range(1, 7)
            .Select(i => new MultiRowGroupModel { Id = i, Name = $"Item_{i}" })
            .ToList();

        using var stream = new MemoryStream();
        await written.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 2 }
        );

        stream.Position = 0;
        List<MultiRowGroupModel> sequential =
            await MultiRowGroupModelParquetExtensions.ReadParquetAsync(stream);

        stream.Position = 0;
        List<MultiRowGroupModel> parallel =
            await MultiRowGroupModelParquetExtensions.ReadParquetParallelAsync(stream);

        parallel.Select(x => x.Id).ShouldBe(sequential.Select(x => x.Id));
        parallel.Select(x => x.Name).ShouldBe(sequential.Select(x => x.Name));
    }
}
