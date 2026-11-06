using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// A model whose string columns exercise the raw UTF-16 span read path introduced for
/// issue #143 (span-keyed string deduplication).
/// </summary>
[ParquetSerializable]
public partial record SpanDedupRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    /// <summary>Non-nullable: read with no definition-level lane.</summary>
    [ParquetColumn("required_category")]
    public string RequiredCategory { get; init; } = string.Empty;

    /// <summary>Nullable: read with a definition-level lane and a packed value lane.</summary>
    [ParquetColumn("optional_category")]
    public string? OptionalCategory { get; init; }
}

/// <summary>
/// Covers the deduplicating raw-span read path: value fidelity for awkward payloads,
/// null/empty discrimination, hash-collision safety and the allocation claim.
/// </summary>
public sealed class SpanStringDeduplicationTests
{
    private static readonly ParquetSerializerOptions Deduplicating = new()
    {
        DeduplicateStrings = true,
    };

    private static readonly ParquetSerializerOptions NotDeduplicating = new()
    {
        DeduplicateStrings = false,
    };

    private static async Task<byte[]> WriteAsync(List<SpanDedupRecord> items)
    {
        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);
        return ms.ToArray();
    }

    private static async Task AssertRoundTripsAsync(List<SpanDedupRecord> items)
    {
        byte[] bytes = await WriteAsync(items);

        var deduplicated = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(Deduplicating)
            .ToListAsync();
        var plain = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(NotDeduplicating)
            .ToListAsync();

        deduplicated.Count.ShouldBe(items.Count);
        plain.Count.ShouldBe(items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            deduplicated[i].RequiredCategory.ShouldBe(items[i].RequiredCategory);
            deduplicated[i].OptionalCategory.ShouldBe(items[i].OptionalCategory);

            // The deduplicating path must agree with the plain path value for value.
            deduplicated[i].RequiredCategory.ShouldBe(plain[i].RequiredCategory);
            deduplicated[i].OptionalCategory.ShouldBe(plain[i].OptionalCategory);
        }
    }

    [Fact]
    public async Task EmptyAndNullValuesStayDistinctThroughTheSpanPath()
    {
        var items = new List<SpanDedupRecord>();
        for (int i = 0; i < 64; i++)
        {
            items.Add(
                new SpanDedupRecord
                {
                    Id = i,
                    RequiredCategory = (i % 3) switch
                    {
                        0 => string.Empty,
                        1 => "Alpha",
                        _ => "Beta",
                    },
                    OptionalCategory = (i % 4) switch
                    {
                        0 => null,
                        1 => string.Empty,
                        2 => "Alpha",
                        _ => "Beta",
                    },
                }
            );
        }

        await AssertRoundTripsAsync(items);

        byte[] bytes = await WriteAsync(items);
        var read = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(Deduplicating)
            .ToListAsync();

        // An empty string must never be conflated with a null.
        read[0].OptionalCategory.ShouldBeNull();
        read[1].OptionalCategory.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task EmbeddedNulCharactersSurviveTheSpanPath()
    {
        string withNul = "left\0right";
        string onlyNuls = "\0\0\0";
        var items = new List<SpanDedupRecord>();
        for (int i = 0; i < 48; i++)
        {
            items.Add(
                new SpanDedupRecord
                {
                    Id = i,
                    RequiredCategory = (i % 2 == 0) ? withNul : onlyNuls,
                    OptionalCategory = (i % 3 == 0) ? withNul : null,
                }
            );
        }

        await AssertRoundTripsAsync(items);

        byte[] bytes = await WriteAsync(items);
        var read = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(Deduplicating)
            .ToListAsync();

        // A NUL must terminate nothing: full length has to be preserved.
        read[0].RequiredCategory.Length.ShouldBe(withNul.Length);
        read[0].RequiredCategory.ShouldBe(withNul);
        read[1].RequiredCategory.ShouldBe(onlyNuls);
    }

    [Fact]
    public async Task VeryLongAndNonBmpValuesSurviveTheSpanPath()
    {
        string veryLong = new string('x', 200_000) + "|tail";
        string nonBmp = "🎿🧪𝔘𝔫𝔦𝔠𝔬𝔡𝔢 — ünïcodé";
        string almostVeryLong = new string('x', 200_000) + "|tale"; // differs only at the end

        var items = new List<SpanDedupRecord>
        {
            new()
            {
                Id = 0,
                RequiredCategory = veryLong,
                OptionalCategory = nonBmp,
            },
            new()
            {
                Id = 1,
                RequiredCategory = almostVeryLong,
                OptionalCategory = null,
            },
            new()
            {
                Id = 2,
                RequiredCategory = veryLong,
                OptionalCategory = nonBmp,
            },
        };

        await AssertRoundTripsAsync(items);

        byte[] bytes = await WriteAsync(items);
        var read = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(Deduplicating)
            .ToListAsync();

        // Two values sharing a 200,000-character prefix must not be conflated.
        read[0].RequiredCategory.ShouldBe(veryLong);
        read[1].RequiredCategory.ShouldBe(almostVeryLong);
        read[1].RequiredCategory.ShouldNotBe(read[0].RequiredCategory);

        // The repeated long value should still be deduplicated to a single instance.
        read[2].RequiredCategory.ShouldBeSameAs(read[0].RequiredCategory);
    }

    [Fact]
    public async Task HashCollisionsFallBackToFullComparison()
    {
        // Build a set of distinct values that all land in the same 512-slot bucket under the
        // emitted FNV-1a hash, so every read after the first is a collision.
        const int TableMask = 511;
        int targetBucket = -1;
        var colliding = new List<string>();
        for (int i = 0; colliding.Count < 40 && i < 2_000_000; i++)
        {
            string candidate =
                "collide-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int bucket = (int)(Fnv1a(candidate) & TableMask);
            if (targetBucket < 0)
            {
                targetBucket = bucket;
            }

            if (bucket == targetBucket)
            {
                colliding.Add(candidate);
            }
        }

        colliding.Count.ShouldBe(40);

        var items = new List<SpanDedupRecord>();
        for (int i = 0; i < 400; i++)
        {
            string value = colliding[i % colliding.Count];
            items.Add(
                new SpanDedupRecord
                {
                    Id = i,
                    RequiredCategory = value,
                    OptionalCategory = value,
                }
            );
        }

        // Every row must come back with its own value even though all 40 distinct values
        // hash into a single bucket — the cache must never return a same-hash neighbour.
        await AssertRoundTripsAsync(items);
    }

    [Fact]
    public async Task RepeatedValuesShareASingleInstance()
    {
        var items = new List<SpanDedupRecord>();
        for (int i = 0; i < 500; i++)
        {
            items.Add(
                new SpanDedupRecord
                {
                    Id = i,
                    RequiredCategory = (i % 2 == 0) ? "Electronics" : "Clothing",
                    OptionalCategory = (i % 3 == 0) ? "OnSale" : null,
                }
            );
        }

        byte[] bytes = await WriteAsync(items);
        var read = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(Deduplicating)
            .ToListAsync();

        var distinctRequired = read.Select(r => r.RequiredCategory)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();
        distinctRequired.ShouldBe(2);

        var distinctOptional = read.Where(r => r.OptionalCategory is not null)
            .Select(r => (object)r.OptionalCategory!)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();
        distinctOptional.ShouldBe(1);
    }

    [Fact]
    public async Task DeduplicatedReadReturnsSharedStringInstances()
    {
        var items = new List<SpanDedupRecord>();
        for (int i = 0; i < 20_000; i++)
        {
            items.Add(
                new SpanDedupRecord
                {
                    Id = i,
                    // Long values so a regression that stopped sharing instances would also be
                    // visibly expensive, not merely wrong.
                    RequiredCategory =
                        "Category-"
                        + (i % 8).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + new string('c', 256),
                    OptionalCategory =
                        "Tag-"
                        + (i % 5).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + new string('t', 256),
                }
            );
        }

        byte[] bytes = await WriteAsync(items);

        // Assert the mechanism directly rather than by process-wide allocation counting.
        // GC.GetTotalAllocatedBytes is process-wide and this measurement spans awaits, so with
        // xUnit running collections in parallel an unrelated test's allocation lands inside the
        // window: the comparative form passed in isolation and failed in the full suite. Instance
        // identity is what deduplication actually promises, and it is deterministic.
        var deduplicatedRead = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(Deduplicating)
            .ToListAsync();
        var plainRead = await SpanDedupRecordParquet
            .From(new MemoryStream(bytes))
            .WithOptions(NotDeduplicating)
            .ToListAsync();

        deduplicatedRead.Count.ShouldBe(items.Count);
        plainRead.Count.ShouldBe(items.Count);

        // 8 distinct required + 5 distinct optional values, however many rows carry them.
        int deduplicatedInstances = CountDistinctInstances(deduplicatedRead);
        deduplicatedInstances.ShouldBe(13);

        // Without deduplication every row holds its own instance, so the count tracks rows.
        int plainInstances = CountDistinctInstances(plainRead);
        (plainInstances > deduplicatedInstances * 100).ShouldBeTrue(
            $"Expected the plain read to hold far more string instances; got {plainInstances:N0} "
                + $"versus {deduplicatedInstances:N0} deduplicated."
        );

        // Values must still be correct, not merely shared.
        deduplicatedRead
            .Select(r => r.RequiredCategory)
            .ToList()
            .ShouldBe(items.Select(i => i.RequiredCategory).ToList());
        deduplicatedRead
            .Select(r => r.OptionalCategory)
            .ToList()
            .ShouldBe(items.Select(i => i.OptionalCategory).ToList());

        static int CountDistinctInstances(List<SpanDedupRecord> rows)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (SpanDedupRecord row in rows)
            {
                if (row.RequiredCategory is not null)
                {
                    seen.Add(row.RequiredCategory);
                }

                if (row.OptionalCategory is not null)
                {
                    seen.Add(row.OptionalCategory);
                }
            }

            return seen.Count;
        }
    }

    /// <summary>
    /// Mirrors the FNV-1a hash emitted into <c>StringDeduplicator</c> so tests can construct
    /// deliberate bucket collisions.
    /// </summary>
    private static uint Fnv1a(string value)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= 16777619u;
        }

        return hash;
    }
}
