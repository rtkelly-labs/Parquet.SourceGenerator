using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NodaTime;
using Parquet.Schema;
using Parquet.SourceGenerator.NodaTime.Adapters;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Adapted collection elements and generic adapters (docs/44 §A.3–A.4), through the real
/// generated code: every supported collection shape, scalar and group surrogates, nullable
/// elements and collections, explicit per-member element adapters, generic sources closed per
/// member, and value-type struct list elements without any adapter at all.
/// </summary>
public sealed class AdaptedCollectionRoundTripTests
{
    [Fact]
    public async Task NodaTimeCollectionsRoundTrip()
    {
        var rows = new List<NodaCollections>
        {
            new()
            {
                Id = 0,
                Instants =
                [
                    Instant.MinValue,
                    NodaConstants.UnixEpoch - Duration.FromNanoseconds(1),
                    Instant.MaxValue,
                ],
                MaybeInstants = [NodaConstants.UnixEpoch, null, Instant.FromUnixTimeTicks(-3)],
                Times = [LocalTime.Midnight, LocalTime.MaxValue],
                Dates = [new LocalDate(2024, 2, 29).WithCalendar(CalendarSystem.Julian), null],
                Periods = [Period.FromMinutes(90), Period.Zero],
                Offsets = [Offset.FromHoursAndMinutes(5, 45), Offset.MinValue],
                Nanos = [NodaConstants.UnixEpoch + Duration.FromNanoseconds(7)],
            },
            new()
            {
                Id = 1,
                Instants = [],
                MaybeInstants = null,
                Times = [],
                Dates = [],
                Periods = [],
                Offsets = null,
                Nanos = [],
            },
            new()
            {
                Id = 2,
                Instants = [NodaConstants.UnixEpoch],
                MaybeInstants = [null],
                Times = [new LocalTime(12, 0).PlusNanoseconds(1)],
                Dates = [new LocalDate(-9998, 1, 1)],
                Periods = [new PeriodBuilder { Weeks = -2, Ticks = 3 }.Build()],
                Offsets = [],
                Nanos = [NodaConstants.UnixEpoch - Duration.FromNanoseconds(1)],
            },
        };

        using var ms = new MemoryStream();
        await NodaCollectionsParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<NodaCollections> back = await NodaCollectionsParquetExtensions.ReadParquetAsync(ms);

        back.Count.ShouldBe(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            back[i].Id.ShouldBe(rows[i].Id);
            back[i].Instants.ShouldBe(rows[i].Instants);
            back[i].MaybeInstants.ShouldBe(rows[i].MaybeInstants);
            back[i].Times.ShouldBe(rows[i].Times);
            back[i].Dates.ShouldBe(rows[i].Dates);
            back[i].Periods.ShouldBe(rows[i].Periods);
            back[i].Offsets.ShouldBe(rows[i].Offsets);
            back[i].Nanos.ShouldBe(rows[i].Nanos);
        }
    }

    [Fact]
    public async Task AdaptedElementsAreWrittenAsTheirSurrogateColumns()
    {
        using var ms = new MemoryStream();
        await NodaCollectionsParquetExtensions.WriteParquetAsync(
            [new NodaCollections { Instants = [NodaConstants.UnixEpoch] }],
            ms
        );
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);

        var instants = (ListField)reader.Schema.Fields.Single(f => f.Name == "Instants");
        var group = instants.Item.ShouldBeOfType<StructField>();
        group.Fields.Select(f => f.Name).ShouldBe(["days_since_unix_epoch", "nanosecond_of_day"]);

        var times = (ListField)reader.Schema.Fields.Single(f => f.Name == "Times");
        times.Item.ShouldBeOfType<DataField>().ClrType.ShouldBe(typeof(long));

        var nanos = (ListField)reader.Schema.Fields.Single(f => f.Name == "Nanos");
        nanos.Item.ShouldBeOfType<DataField>().ClrType.ShouldBe(typeof(long));
    }

    [Fact]
    public async Task GenericAdaptersCloseOverEachMemberType()
    {
        var special = new Id<SpecialEntity>(Guid.NewGuid());
        var rows = new List<Shipment>
        {
            new()
            {
                Id = new Id<Shipment>(Guid.NewGuid()),
                Customer = new Id<Customer>(Guid.NewGuid()),
                Orders = [new Id<Order>(Guid.NewGuid()), new Id<Order>(Guid.Empty)],
                Weight = new Tagged<int>(42, "kg"),
                Label = new Tagged<string>("fragile", "note"),
                Special = special,
            },
            new()
            {
                Id = new Id<Shipment>(Guid.Empty),
                Customer = null,
                Orders = [],
                Weight = new Tagged<int>(-1, ""),
                Label = null,
                Special = special,
            },
        };

        using var ms = new MemoryStream();
        await ShipmentParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<Shipment> back = await ShipmentParquetExtensions.ReadParquetAsync(ms);

        for (int i = 0; i < rows.Count; i++)
        {
            back[i].Id.ShouldBe(rows[i].Id);
            back[i].Customer.ShouldBe(rows[i].Customer);
            back[i].Orders.ShouldBe(rows[i].Orders);
            back[i].Weight.ShouldBe(rows[i].Weight);
            back[i].Label.ShouldBe(rows[i].Label);
            back[i].Special.ShouldBe(rows[i].Special);
        }

        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        // Guid columns are strings in this generator; the exact SpecialIdAdapter writes "N" text.
        reader.Schema.Fields.Single(f => f.Name == "Weight").ShouldBeOfType<StructField>();
    }

    [Fact]
    public async Task ValueTypeStructListElementsRoundTripWithoutAnAdapter()
    {
        var rows = new List<PointCloud>
        {
            new()
            {
                Points = [new Coord2 { X = 1, Y = 1.5 }, new Coord2()],
                Maybe = [null, new Coord2 { X = 3 }],
            },
            new() { Points = [], Maybe = null },
        };

        using var ms = new MemoryStream();
        await PointCloudParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        List<PointCloud> back = await PointCloudParquetExtensions.ReadParquetAsync(ms);

        back[0].Points.ShouldBe(rows[0].Points);
        back[0].Maybe.ShouldBe(rows[0].Maybe);
        back[1].Points.ShouldBeEmpty();
        back[1].Maybe.ShouldBeNull();
    }
}

[ParquetSerializable]
public sealed partial record NodaCollections
{
    public int Id { get; init; }
    public List<Instant> Instants { get; init; } = [];
    public Instant?[]? MaybeInstants { get; init; }
    public IReadOnlyList<LocalTime> Times { get; init; } = [];
    public List<LocalDate?> Dates { get; init; } = [];
    public IEnumerable<Period> Periods { get; init; } = [];
    public IList<Offset>? Offsets { get; init; }

    [ParquetAdapter(typeof(InstantAsUnixNanosecondsAdapter))]
    public List<Instant> Nanos { get; init; } = [];
}

public sealed class Customer;

public sealed class Order;

[ParquetSerializable]
public sealed partial record Shipment
{
    public Id<Shipment> Id { get; init; }
    public Id<Customer>? Customer { get; init; }
    public List<Id<Order>> Orders { get; init; } = [];
    public Tagged<int> Weight { get; init; } = new(0, "");
    public Tagged<string>? Label { get; init; }
    public Id<SpecialEntity> Special { get; init; }
}

[ParquetSerializable]
public sealed partial record PointCloud
{
    public List<Coord2> Points { get; init; } = [];
    public Coord2?[]? Maybe { get; init; }
}
