using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// A model whose nullable columns cover every branch of the emitter's nullable payload
/// conversion switch (plain value, enum, TimeSpan, TimeOnly, DateOnly, Guid), so the
/// branchless <c>GetValueOrDefault()</c> forms introduced by issue #145 are exercised for all
/// of them.
/// </summary>
[ParquetSerializable]
public partial record BranchlessNullModel
{
    [ParquetColumn("row")]
    public int Row { get; init; }

    [ParquetColumn("opt_int")]
    public int? OptInt { get; init; }

    [ParquetColumn("opt_long")]
    public long? OptLong { get; init; }

    [ParquetColumn("opt_double")]
    public double? OptDouble { get; init; }

    [ParquetColumn("opt_bool")]
    public bool? OptBool { get; init; }

    [ParquetColumn("opt_enum")]
    public BranchlessStatus? OptEnum { get; init; }

    [ParquetColumn("opt_guid")]
    public Guid? OptGuid { get; init; }

    [ParquetColumn("opt_timespan")]
    public TimeSpan? OptTimeSpan { get; init; }

    [ParquetColumn("opt_dateonly")]
    public DateOnly? OptDateOnly { get; init; }

    [ParquetColumn("opt_timeonly")]
    public TimeOnly? OptTimeOnly { get; init; }

    [ParquetColumn("opt_datetime")]
    public DateTime? OptDateTime { get; init; }
}

public enum BranchlessStatus
{
    Unknown = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>
/// End-to-end round trips for the branchless definition-level / value-compaction extraction
/// (issue #145). The emitter has three distinct extraction shapes — the <c>List&lt;T&gt;</c>
/// span loop, the <c>T[]</c> span loop, and the <c>IEnumerable</c> fallback — and each has its
/// own copy of the nullable packing code, so every null-density shape is run through all three.
/// </summary>
public sealed class BranchlessNullExtractionTests
{
    /// <summary>An IReadOnlyCollection that is neither a List nor an array, forcing the fallback loop.</summary>
    private sealed class OpaqueCollection(List<BranchlessNullModel> inner)
        : IReadOnlyCollection<BranchlessNullModel>
    {
        public int Count => inner.Count;

        public IEnumerator<BranchlessNullModel> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => inner.GetEnumerator();
    }

    public static TheoryData<int, string> Shapes
    {
        get
        {
            var data = new TheoryData<int, string>();
            foreach (
                int count in new[] { 1, 2, 3, 7, 8, 15, 16, 17, 31, 33, 63, 64, 65, 127, 129, 1000 }
            )
            {
                foreach (
                    string shape in new[]
                    {
                        "all-null",
                        "no-null",
                        "alternating",
                        "alternating-offset",
                        "leading-null",
                        "trailing-null",
                        "sparse",
                        "dense",
                    }
                )
                {
                    data.Add(count, shape);
                }
            }

            return data;
        }
    }

    private static bool IsPresent(string shape, int i, int count) =>
        shape switch
        {
            "all-null" => false,
            "no-null" => true,
            "alternating" => i % 2 == 0,
            "alternating-offset" => i % 2 == 1,
            "leading-null" => i > 0,
            "trailing-null" => i < count - 1,
            "sparse" => i % 10 == 0,
            "dense" => i % 10 != 0,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    private static List<BranchlessNullModel> Build(int count, string shape)
    {
        var rows = new List<BranchlessNullModel>(count);
        for (int i = 0; i < count; i++)
        {
            bool present = IsPresent(shape, i, count);
            rows.Add(
                new BranchlessNullModel
                {
                    Row = i,
                    OptInt = present ? i * 3 : null,
                    OptLong = present ? (long)i * 1_000_003L : null,
                    OptDouble = present ? i + 0.5 : null,
                    OptBool = present ? i % 3 == 0 : null,
                    OptEnum = present ? (BranchlessStatus)(i % 3) : null,
                    OptGuid = present ? Guid.Parse($"00000000-0000-0000-0000-{i:D12}") : null,
                    OptTimeSpan = present ? TimeSpan.FromMilliseconds(i * 37) : null,
                    OptDateOnly = present ? new DateOnly(2020, 1, 1).AddDays(i % 900) : null,
                    OptTimeOnly = present
                        ? TimeOnly.MinValue.Add(TimeSpan.FromSeconds(i % 86_000))
                        : null,
                    OptDateTime = present
                        ? new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i)
                        : null,
                }
            );
        }

        return rows;
    }

    private static void AssertMatches(
        List<BranchlessNullModel> expected,
        List<BranchlessNullModel> actual,
        string because
    )
    {
        actual.Count.ShouldBe(expected.Count, because);
        for (int i = 0; i < expected.Count; i++)
        {
            BranchlessNullModel e = expected[i];
            BranchlessNullModel a = actual[i];
            a.Row.ShouldBe(e.Row, because);
            a.OptInt.ShouldBe(e.OptInt, because);
            a.OptLong.ShouldBe(e.OptLong, because);
            a.OptDouble.ShouldBe(e.OptDouble, because);
            a.OptBool.ShouldBe(e.OptBool, because);
            a.OptEnum.ShouldBe(e.OptEnum, because);
            a.OptGuid.ShouldBe(e.OptGuid, because);
            a.OptTimeSpan.ShouldBe(e.OptTimeSpan, because);
            a.OptDateOnly.ShouldBe(e.OptDateOnly, because);
            a.OptTimeOnly.ShouldBe(e.OptTimeOnly, because);
            (a.OptDateTime?.Ticks).ShouldBe(e.OptDateTime?.Ticks, because);
        }
    }

    private static async Task<List<BranchlessNullModel>> RoundTripAsync(
        IReadOnlyCollection<BranchlessNullModel> source
    )
    {
        using var stream = new MemoryStream();
        await source.WriteParquetAsync(stream);
        stream.Position = 0;
        return await BranchlessNullModelParquetExtensions.ReadParquetAsync(stream);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ListSourceRoundTripsEveryNullShape(int count, string shape)
    {
        List<BranchlessNullModel> rows = Build(count, shape);
        AssertMatches(rows, await RoundTripAsync(rows), $"list/{shape}/{count}");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ArraySourceRoundTripsEveryNullShape(int count, string shape)
    {
        List<BranchlessNullModel> rows = Build(count, shape);
        AssertMatches(rows, await RoundTripAsync(rows.ToArray()), $"array/{shape}/{count}");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task OpaqueCollectionSourceRoundTripsEveryNullShape(int count, string shape)
    {
        List<BranchlessNullModel> rows = Build(count, shape);
        AssertMatches(
            rows,
            await RoundTripAsync(new OpaqueCollection(rows)),
            $"opaque/{shape}/{count}"
        );
    }

    /// <summary>
    /// The three extraction shapes must agree byte-for-byte on the produced Parquet stream, not
    /// merely round trip: the branchless compaction writes a discardable payload into the value
    /// buffer for null slots, and any leak of those defaults into the written page would show up
    /// here as a differing byte stream.
    /// </summary>
    [Theory]
    [InlineData(17, "alternating")]
    [InlineData(129, "sparse")]
    [InlineData(1000, "dense")]
    [InlineData(64, "all-null")]
    [InlineData(64, "no-null")]
    public async Task ListArrayAndFallbackProduceIdenticalBytes(int count, string shape)
    {
        List<BranchlessNullModel> rows = Build(count, shape);

        static async Task<byte[]> WriteAsync(IReadOnlyCollection<BranchlessNullModel> source)
        {
            using var stream = new MemoryStream();
            await source.WriteParquetAsync(stream);
            return stream.ToArray();
        }

        byte[] fromList = await WriteAsync(rows);
        byte[] fromArray = await WriteAsync(rows.ToArray());
        byte[] fromOpaque = await WriteAsync(new OpaqueCollection(rows));

        fromArray.ShouldBe(fromList, "list vs array");
        fromOpaque.ShouldBe(fromList, "list vs opaque");
    }
}
