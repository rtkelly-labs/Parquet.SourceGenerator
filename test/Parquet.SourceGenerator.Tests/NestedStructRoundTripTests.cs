using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Parquet.SourceGenerator;
using Parquet.SourceGenerator.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// M2 of issue #176: nested POCO members (StructField groups) round-trip through the v6
/// generated code — optional and required members, null propagation at each level, and
/// struct-in-struct composition. The generator runs as an analyzer on this assembly, so
/// these exercise the real emitted extraction/reconstruction paths.
/// </summary>
public sealed class NestedStructRoundTripTests
{
    [Fact]
    public async Task OptionalStructMemberRoundTripsWithNullPropagation()
    {
        var rows = new List<NestedOrder>
        {
            new()
            {
                Id = 0,
                Ship = new Address { City = "NYC", Zip = 10001 },
                Bill = new Address { City = "Boston", Zip = 2000 },
            },
            new()
            {
                Id = 1,
                Ship = null,
                Bill = new Address(),
            },
            new()
            {
                Id = 2,
                Ship = new Address { City = null, Zip = 55 },
                Bill = new Address { City = "LA", Zip = null },
            },
        };

        var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;

        var back = await NestedOrderParquet.From(ms).ToListAsync();

        back.Count.ShouldBe(3);
        back[0].Ship!.City.ShouldBe("NYC");
        back[0].Ship!.Zip.ShouldBe(10001);
        back[0].Bill.City.ShouldBe("Boston");
        back[1].Ship.ShouldBeNull();
        back[1].Bill.City.ShouldBeNull();
        back[1].Bill.Zip.ShouldBeNull();
        back[2].Ship!.City.ShouldBeNull();
        back[2].Ship!.Zip.ShouldBe(55);
        back[2].Bill.City.ShouldBe("LA");
        back[2].Bill.Zip.ShouldBeNull();
    }

    [Fact]
    public async Task FakerGeneratedNestedOrdersRoundTrip()
    {
        var faker = TestFakers.CreateNestedOrderFaker(12345);
        var rows = faker.Generate(25);

        using var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;

        var back = await NestedOrderParquet.From(ms).ToListAsync();
        back.Count.ShouldBe(25);
        for (int i = 0; i < rows.Count; i++)
        {
            back[i].Id.ShouldBe(rows[i].Id);
            back[i].Ship?.City.ShouldBe(rows[i].Ship?.City);
            back[i].Ship?.Zip.ShouldBe(rows[i].Ship?.Zip);
            back[i].Bill.City.ShouldBe(rows[i].Bill.City);
            back[i].Bill.Zip.ShouldBe(rows[i].Bill.Zip);
        }
    }

    [Fact]
    public async Task StructInStructRoundTrips()
    {
        var rows = new List<Envelope>
        {
            new()
            {
                Id = 0,
                Outer = new OuterInfo
                {
                    Label = "first",
                    Inner = new Address { City = "Denver", Zip = 80001 },
                },
            },
            new()
            {
                Id = 1,
                Outer = new OuterInfo { Label = null, Inner = null },
            },
            new() { Id = 2, Outer = null },
        };

        var ms = new MemoryStream();
        await EnvelopeParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        var back = await EnvelopeParquet.From(ms).ToListAsync();

        back[0].Outer!.Label.ShouldBe("first");
        back[0].Outer!.Inner!.City.ShouldBe("Denver");
        back[1].Outer!.Label.ShouldBeNull();
        back[1].Outer!.Inner.ShouldBeNull();
        back[2].Outer.ShouldBeNull();
    }

    [Fact]
    public async Task ArrayAndStreamPathsAgreeWithListRead()
    {
        var rows = new List<NestedOrder>
        {
            new()
            {
                Id = 0,
                Ship = new Address { City = "A", Zip = 1 },
                Bill = new Address { City = "B", Zip = 2 },
            },
            new()
            {
                Id = 1,
                Ship = null,
                Bill = new Address(),
            },
        };

        var ms = new MemoryStream();
        await rows.WriteParquetAsync(ms);
        ms.Position = 0;
        var array = await NestedOrderParquet.From(ms).ToArrayAsync();

        array.Length.ShouldBe(2);
        array[0].Ship!.City.ShouldBe("A");
        array[1].Ship.ShouldBeNull();
    }

    [Fact]
    public async Task AllStructsNullColumnDecodesAsAllNull()
    {
        var rows = new List<NestedOrder>
        {
            new()
            {
                Id = 0,
                Ship = null,
                Bill = new Address(),
            },
            new()
            {
                Id = 1,
                Ship = null,
                Bill = new Address(),
            },
        };

        var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        var back = await NestedOrderParquet.From(ms).ToListAsync();
        back.ShouldAllBe(r => r.Ship == null);
    }

    [Fact]
    public async Task EveryPublicSurfaceHandlesCompoundModels()
    {
        var rows = new List<NestedOrder>
        {
            new()
            {
                Id = 0,
                Ship = new Address { City = "X", Zip = 1 },
                Bill = new Address { City = "Y", Zip = 2 },
            },
            new()
            {
                Id = 1,
                Ship = null,
                Bill = new Address(),
            },
            new()
            {
                Id = 2,
                Ship = new Address { City = null, Zip = null },
                Bill = new Address { City = "Z", Zip = null },
            },
        };

        // IEnumerable batched write (fallback extraction path)
        var ms = new MemoryStream();
        await System
            .Linq.Enumerable.AsEnumerable(rows)
            .WriteParquetBatchedAsync(ms, new ParquetSerializerOptions { RowGroupSize = 2 });
        ms.Position = 0;

        // stream read
        ms.Position = 0;
        var streamed = new List<NestedOrder>();
        await foreach (NestedOrder r in NestedOrderParquet.From(ms).AsAsyncEnumerable())
            streamed.Add(r);
        streamed.Count.ShouldBe(3);
        streamed[0].Ship!.City.ShouldBe("X");
        streamed[1].Ship.ShouldBeNull();
        streamed[2].Ship!.City.ShouldBeNull();
        streamed[2].Bill.City.ShouldBe("Z");

        // stream array read
        ms.Position = 0;
        var parallel = await NestedOrderParquet.From(ms).ToArrayAsync();
        parallel.Length.ShouldBe(3);
        parallel[0].Bill.City.ShouldBe("Y");

        // memory buffer reads
        byte[] bytes = ms.ToArray();
        var mem = await NestedOrderParquet.From(bytes).ToListAsync();
        mem.Count.ShouldBe(3);
        var memArr = await NestedOrderParquet.From(bytes).Parallel().ToArrayAsync();
        memArr[0].Ship!.City.ShouldBe("X");
        var memStream = new List<NestedOrder>();
        await foreach (NestedOrder r in NestedOrderParquet.From(bytes).AsAsyncEnumerable())
            memStream.Add(r);
        memStream.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AsyncEnumerableWriteStreamReadRoundTrips()
    {
        var ms = new MemoryStream();
        async System.Collections.Generic.IAsyncEnumerable<NestedOrder> Source()
        {
            yield return new NestedOrder
            {
                Id = 0,
                Ship = new Address { City = "S", Zip = 9 },
                Bill = new Address(),
            };
            await Task.Yield();
        }

        await Source().WriteParquetAsync(ms);
        ms.Position = 0;
        var back = await NestedOrderParquet.From(ms).ToListAsync();
        back[0].Ship!.City.ShouldBe("S");
    }

    [Fact]
    public async Task NullableValueTypeStructMembersRoundTrip()
    {
        var rows = new List<NullableStructRow>
        {
            new()
            {
                Id = 0,
                Origin = new Coord2 { X = 1, Y = 2.5 },
                Start = new Coord2 { X = 3, Y = 4.5 },
                Frame = new Frame2
                {
                    Tag = "f",
                    Mark = new Coord2 { X = 7, Y = 8.5 },
                },
            },
            new()
            {
                Id = 1,
                Origin = default,
                Start = null,
                Frame = null,
            },
            new()
            {
                Id = 2,
                Origin = new Coord2 { X = 5, Y = 6.5 },
                Start = new Coord2(),
                Frame = new Frame2(),
            },
        };

        var ms = new MemoryStream();
        await NullableStructRowParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;

        var back = await NullableStructRowParquet.From(ms).ToListAsync();

        back.Count.ShouldBe(3);
        back[0].Origin.X.ShouldBe(1);
        back[0].Start!.Value.X.ShouldBe(3);
        back[0].Start!.Value.Y.ShouldBe(4.5);
        back[0].Frame!.Mark!.Value.X.ShouldBe(7);
        back[1].Start.ShouldBeNull();
        back[1].Frame.ShouldBeNull();
        back[2].Start.ShouldNotBeNull();
        back[2].Start!.Value.X.ShouldBe(0);
        back[2].Frame.ShouldNotBeNull();
        back[2].Frame!.Tag.ShouldBeNull();
        back[2].Frame!.Mark.ShouldBeNull();
    }
}

[ParquetSerializable]
public sealed partial record Address
{
    public string? City { get; init; }
    public int? Zip { get; init; }
}

[ParquetSerializable]
public sealed partial record OuterInfo
{
    public string? Label { get; init; }
    public Address? Inner { get; init; }
}

[ParquetSerializable]
public sealed partial record NestedOrder
{
    public int Id { get; init; }
    public Address? Ship { get; init; }
    public Address Bill { get; init; } = new();
}

[ParquetSerializable]
public sealed partial record Envelope
{
    public int Id { get; init; }
    public OuterInfo? Outer { get; init; }
}

[ParquetSerializable]
public partial struct Coord2
{
    public int X { get; init; }
    public double Y { get; init; }
}

[ParquetSerializable]
public sealed partial record Frame2
{
    public string? Tag { get; init; }
    public Coord2? Mark { get; init; }
}

[ParquetSerializable]
public sealed partial record NullableStructRow
{
    public int Id { get; init; }
    public Coord2 Origin { get; init; }
    public Coord2? Start { get; init; }
    public Frame2? Frame { get; init; }
}
