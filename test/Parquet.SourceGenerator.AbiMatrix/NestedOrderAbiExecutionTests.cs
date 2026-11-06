using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SampleDomain.Models;
using Xunit;

namespace Parquet.SourceGenerator.AbiMatrix;

/// <summary>
/// ABI Execution verification for frozen v1.0 compound generated code (issue #289, docs/27 §2.1).
///
/// Verifies that historical, pre-generated compound NestedOrderParquetExtensions (exercising
/// Dremel level shredding, WriteAllPartsAsync, and PropagateLevels) executes cleanly against
/// Parquet.Net runtime packages.
/// </summary>
public sealed class NestedOrderAbiExecutionTests
{
    private static List<NestedOrder> CreateSampleOrders()
    {
        return new List<NestedOrder>
        {
            new()
            {
                Id = 1,
                Ship = new Address { City = "Seattle", Zip = 98101 },
                Bill = new Address { City = "New York", Zip = 10001 },
                Origin = new Point { X = 10, Y = 20 },
                Start = new Point { X = 1, Y = 2 },
            },
            new()
            {
                Id = 2,
                Ship = null,
                Bill = new Address { City = "Chicago", Zip = null },
                Origin = new Point { X = 30, Y = 40 },
                Start = null,
            },
            new()
            {
                Id = 3,
                Ship = new Address { City = null, Zip = 90210 },
                Bill = new Address { City = null, Zip = null },
                Origin = new Point { X = 50, Y = 60 },
                Start = new Point { X = 5, Y = 6 },
            },
        };
    }

    [Fact]
    public async Task FrozenNestedOrderCanRoundtripSequentially()
    {
        var expected = CreateSampleOrders();
        using var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(expected, ms);
        ms.Position = 0;

        List<NestedOrder> actual = await NestedOrderParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(3, actual.Count);
        Assert.Equal("Seattle", actual[0].Ship?.City);
        Assert.Equal(98101, actual[0].Ship?.Zip);
        Assert.Equal("New York", actual[0].Bill.City);
        Assert.Equal(10, actual[0].Origin.X);
        Assert.Equal(1, actual[0].Start?.X);

        Assert.Null(actual[1].Ship);
        Assert.Equal("Chicago", actual[1].Bill.City);
        Assert.Null(actual[1].Bill.Zip);
        Assert.Null(actual[1].Start);
    }

    [Fact]
    public async Task FrozenNestedOrderCanRoundtripInParallel()
    {
        var expected = CreateSampleOrders();
        using var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(expected, ms);
        ms.Position = 0;

        NestedOrder[] actual = await NestedOrderParquetExtensions.ReadParquetParallelArrayAsync(ms);
        Assert.Equal(3, actual.Length);
        Assert.Equal("Seattle", actual[0].Ship?.City);
        Assert.Null(actual[1].Ship);
        Assert.Equal("Chicago", actual[1].Bill.City);
    }

    [Fact]
    public async Task FrozenNestedOrderCanStream()
    {
        var expected = CreateSampleOrders();
        using var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(expected, ms);
        ms.Position = 0;

        var actual = new List<NestedOrder>();
        await foreach (NestedOrder order in NestedOrderParquetExtensions.ReadParquetStreamAsync(ms))
        {
            actual.Add(order);
        }

        Assert.Equal(3, actual.Count);
        Assert.Equal("Seattle", actual[0].Ship?.City);
        Assert.Null(actual[1].Ship);
    }

    [Fact]
    public async Task FrozenNestedOrderCanReadFromMemory()
    {
        var expected = CreateSampleOrders();
        using var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(expected, ms);
        byte[] bytes = ms.ToArray();

        List<NestedOrder> actual = await NestedOrderParquetExtensions.ReadParquetAsync(bytes);
        Assert.Equal(3, actual.Count);
        Assert.Equal("Seattle", actual[0].Ship?.City);
    }
}
