using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parquet.SourceGenerator;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// M3a of issue #176: row-level lists/arrays with leaf elements. Null-vs-empty list
/// distinction, null elements, element conversions (string, Guid, byte[], DateTime?),
/// and the repetition-level entry walk — per docs/internals/nested-types.md §1.
/// </summary>
public sealed class NestedListRoundTripTests
{
    [Fact]
    public async Task ListMembersRoundTripWithNullAndEmptyDistinction()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var d1 = new DateTime(2026, 5, 4, 3, 2, 1);
        var expectedTags0 = new string?[] { "a", null, "b" };
        var expectedScores0 = new[] { 1, 2, 3 };
        var expectedWhen0 = new DateTime?[] { d1, null };
        var expectedBlob0 = new byte[] { 1, 2 };
        var expectedBlobList = new[] { expectedBlob0 };
        var rows = new List<ListRow>
        {
            new()
            {
                Id = 0,
                Tags = new List<string?> { "a", null, "b" },
                Scores = new List<int> { 1, 2, 3 },
                Keys = new[] { g1, g2 },
                When = new List<DateTime?> { d1, null },
                Blobs = new List<byte[]> { new byte[] { 1, 2 } },
            },
            new()
            {
                Id = 1,
                Tags = new List<string?>(),
                Scores = new List<int>(),
                Keys = Array.Empty<Guid>(),
                When = new List<DateTime?>(),
                Blobs = new List<byte[]>(),
            },
            new()
            {
                Id = 2,
                Tags = null,
                Scores = null!,
                Keys = null,
                When = null,
                Blobs = null,
            },
            new()
            {
                Id = 3,
                Tags = new List<string?> { null },
                Scores = new List<int> { 7 },
                Keys = null,
                When = new List<DateTime?> { null },
                Blobs = null,
            },
        };

        var ms = new MemoryStream();
        await ListRowParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;
        var back = await ListRowParquet.From(ms).ToListAsync();

        back.Count.ShouldBe(4);
        back[0].Tags!.ToArray().ShouldBe(expectedTags0);
        back[0].Scores!.ToArray().ShouldBe(expectedScores0);
        back[0].Keys!.ShouldBe(new[] { g1, g2 });
        back[0].When!.ToArray().ShouldBe(expectedWhen0);
        back[0].Blobs!.Select(b => b.ToArray()).ToArray().ShouldBe(expectedBlobList);

        back[1].Tags!.ShouldBeEmpty();
        back[1].Scores!.ShouldBeEmpty();
        back[1].Keys!.ShouldBeEmpty();
        back[1].When!.ShouldBeEmpty();
        back[1].Blobs!.ShouldBeEmpty();

        back[2].Tags.ShouldBeNull();
        back[2].Keys.ShouldBeNull();
        back[2].When.ShouldBeNull();
        back[2].Blobs.ShouldBeNull();

        var expectedTags3 = new string?[] { null };
        var expectedScores3 = new[] { 7 };
        var expectedWhen3 = new DateTime?[] { null };
        back[3].Tags!.ToArray().ShouldBe(expectedTags3);
        back[3].Scores!.ToArray().ShouldBe(expectedScores3);
        back[3].Keys.ShouldBeNull();
        back[3].When!.ToArray().ShouldBe(expectedWhen3);
    }

    [Fact]
    public async Task MultiRowGroupsAndArrayPathsKeepListEntries()
    {
        var rows = Enumerable
            .Range(0, 50)
            .Select(i => new ListRow
            {
                Id = i,
                Tags = i % 3 == 0 ? null : new List<string?> { $"t{i}", null },
                Scores = Enumerable.Range(0, i % 5).ToList(),
                Keys = new[] { Guid.NewGuid() },
                When = new List<DateTime?> { new DateTime(2020 + (i % 7), 1, 1) },
                Blobs = new List<byte[]> { new byte[] { (byte)i } },
            })
            .ToList();

        var ms = new MemoryStream();
        await rows.WriteParquetBatchedAsync(ms, new ParquetSerializerOptions { RowGroupSize = 7 });
        ms.Position = 0;
        var back = await ListRowParquet.From(ms.ToArray()).Parallel().ToArrayAsync();

        back.Length.ShouldBe(50);
        for (int i = 0; i < 50; i++)
        {
            if (i % 3 == 0)
                back[i].Tags.ShouldBeNull();
            else
            {
                var expectedTagsI = new string?[] { $"t{i}", null };
                back[i].Tags!.ToArray().ShouldBe(expectedTagsI);
            }
            back[i].Scores!.ToArray().ShouldBe(Enumerable.Range(0, i % 5).ToArray());
        }
    }
}

[ParquetSerializable]
public sealed partial record ListRow
{
    public int Id { get; init; }
    public List<string?>? Tags { get; init; }
    public List<int>? Scores { get; init; }
    public Guid[]? Keys { get; init; }
    public List<DateTime?>? When { get; init; }
    public List<byte[]>? Blobs { get; init; }

    [Fact]
    public async Task ListOfPocoRoundTripPreservesMarkersFieldsAndAlignment()
    {
        var node1 = Guid.NewGuid();
        var node2 = Guid.NewGuid();
        var rows = new List<TripRow>
        {
            new()
            {
                Id = 0,
                Stops = null,
                Route = null,
            },
            new()
            {
                Id = 1,
                Stops = [],
                Route = [],
            },
            new()
            {
                Id = 2,
                Stops =
                [
                    new PitStop
                    {
                        City = "A",
                        Zip = 1,
                        Node = node1,
                    },
                    new PitStop
                    {
                        City = null,
                        Zip = null,
                        Node = node2,
                    },
                ],
                Route =
                [
                    new PitStop
                    {
                        City = "R",
                        Zip = 9,
                        Node = node1,
                    },
                ],
            },
            new()
            {
                Id = 3,
                Stops =
                [
                    new PitStop
                    {
                        City = "Z",
                        Zip = 0,
                        Node = Guid.NewGuid(),
                    },
                ],
            },
        };

        var ms = new MemoryStream();
        await TripRowParquetExtensions.WriteParquetAsync(rows, ms);
        ms.Position = 0;

        var back = await TripRowParquet.From(ms.ToArray()).Parallel().ToArrayAsync();

        back.Length.ShouldBe(4);
        back[0].Stops.ShouldBeNull();
        back[0].Route.ShouldBeNull();
        back[1].Stops!.ShouldBeEmpty();
        back[1].Route!.ShouldBeEmpty();
        var stops2 = back[2].Stops!;
        stops2.Count.ShouldBe(2);
        stops2[0].City.ShouldBe("A");
        stops2[0].Zip.ShouldBe(1);
        stops2[0].Node.ShouldBe(node1);
        stops2[1].City.ShouldBeNull();
        stops2[1].Zip.ShouldBeNull();
        stops2[1].Node.ShouldBe(node2);
        back[2].Route!.Length.ShouldBe(1);
        back[2].Route![0].City.ShouldBe("R");
        back[2].Route![0].Zip!.Value.ShouldBe(9);
        back[3].Stops!.Count.ShouldBe(1);
        back[3].Stops![0].Zip!.Value.ShouldBe(0);
    }

    [Fact]
    public async Task MultiRowGroupListOfPocoReadsBackAligned()
    {
        var rows = new List<TripRow>();
        for (int i = 0; i < 50; i++)
        {
            rows.Add(
                new TripRow
                {
                    Id = i,
                    Stops =
                    [
                        new PitStop
                        {
                            City = $"c{i}",
                            Zip = i % 2,
                            Node = Guid.NewGuid(),
                        },
                        new PitStop
                        {
                            City = null,
                            Zip = null,
                            Node = Guid.NewGuid(),
                        },
                    ],
                }
            );
        }

        var ms = new MemoryStream();
        await rows.WriteParquetBatchedAsync(ms, new ParquetSerializerOptions { RowGroupSize = 10 });
        ms.Position = 0;

        var back = await TripRowParquet.From(ms.ToArray()).Parallel().ToArrayAsync();

        back.Length.ShouldBe(50);
        for (int i = 0; i < 50; i++)
        {
            var stopsI = back[i].Stops!;
            stopsI.Count.ShouldBe(2);
            stopsI[0].City.ShouldBe($"c{i}");
            stopsI[0].Zip.ShouldBe(i % 2);
            stopsI[1].City.ShouldBeNull();
        }
    }
}

[ParquetSerializable]
public sealed partial class PitStop
{
    public string? City { get; init; }
    public int? Zip { get; init; }
    public Guid Node { get; init; }
}

[ParquetSerializable]
public sealed partial record TripRow
{
    public int Id { get; init; }
    public List<PitStop>? Stops { get; init; }
    public PitStop[]? Route { get; init; }
}
