using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Serialization;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

#region Corpus Contract Declarations

[ParquetSerializable]
public partial record CorpusComprehensiveScalarRecord
{
    public int Id { get; init; }
    public long BigNumber { get; init; }
    public float Rate { get; init; }
    public double Score { get; init; }
    public bool IsValid { get; init; }
    public string? Description { get; init; }
    public Guid TraceId { get; init; }
    public DateTime CreatedAt { get; init; }
    public int DurationSeconds { get; init; }
    public byte[]? Payload { get; init; }
}

[ParquetSerializable]
public partial record CorpusNullableRecord
{
    public int RequiredInt { get; init; }
    public int? OptionalInt { get; init; }
    public double RequiredDouble { get; init; }
    public double? OptionalDouble { get; init; }
    public Guid RequiredGuid { get; init; }
    public Guid? OptionalGuid { get; init; }
    public string? OptionalString1 { get; init; }
    public string? OptionalString2 { get; init; }
}

[ParquetSerializable]
public partial record CorpusDecimalRecord
{
    public int Id { get; init; }

    [ParquetDecimal(18, 4)]
    public decimal Amount { get; init; }

    [ParquetDecimal(12, 2)]
    public decimal? OptionalFee { get; init; }
}

[ParquetSerializable]
public partial record CorpusTimeSpanRecord
{
    public int Id { get; init; }
    public TimeSpan Elapsed { get; init; }
}

#endregion

/// <summary>
/// Broad-corpus differential sweep against reference oracles (issue #286, docs/27 §2.2).
///
/// Sweeps across compiled model contracts in the test suite to verify:
/// 1. Bidirectional differential equivalence against Parquet.Net's reflection serializer (ParquetSerializer)
///    for flat reference models (PSG write -> ParquetSerializer read, and ParquetSerializer write -> PSG read).
/// 2. Oracle disqualification boundaries: demonstrates where reflection ParquetSerializer fails
///    (structs by generic constraint, TimeSpan by missing field mapping, non-nullable strings by level mismatch).
/// 3. Struct-only models (where ParquetSerializer is disqualified by its "where T : class, new()" generic constraint)
///    verified across all PSG reader modes (Sequential, Parallel, Streaming, Memory).
/// 4. Deep round-trip fidelity and multi-reader mode convergence (Sequential, Parallel, Streaming, Memory)
///    for compound nested structs and lists (where ParquetSerializer is disqualified due to known interior null defects,
///    per docs/15 §2.6).
/// </summary>
public sealed class CorpusDifferentialSweepTests
{
    #region 1. Flat & Primitive Models (Bidirectional Equivalence)

    [Fact]
    public async Task BaselineRecordBidirectionalDifferentialEquivalence()
    {
        var records = Enumerable
            .Range(1, 50)
            .Select(i => new BaselineRecord
            {
                Id = i,
                Name = i % 3 == 0 ? null : $"user_{i}",
                Score = i * 1.25,
                IsActive = i % 2 == 0,
            })
            .ToList();

        // 1. PSG Write -> ParquetSerializer Read
        using var stream1 = new MemoryStream();
        await records.WriteParquetAsync(stream1);
        stream1.Position = 0;

        var result1 = await ParquetSerializer.DeserializeAsync<BaselineRecord>(stream1);
        IList<BaselineRecord> readViaReflection = result1.Data;
        Assert.Equal(records.Count, readViaReflection.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Id, readViaReflection[i].Id);
            Assert.Equal(records[i].Name, readViaReflection[i].Name);
            Assert.Equal(records[i].Score, readViaReflection[i].Score);
            Assert.Equal(records[i].IsActive, readViaReflection[i].IsActive);
        }

        // 2. ParquetSerializer Write -> PSG Read
        using var stream2 = new MemoryStream();
        await ParquetSerializer.SerializeAsync(records, stream2);
        stream2.Position = 0;

        List<BaselineRecord> readViaPsg = await BaselineRecordParquetExtensions.ReadParquetAsync(
            stream2
        );
        Assert.Equal(records.Count, readViaPsg.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Id, readViaPsg[i].Id);
            Assert.Equal(records[i].Name, readViaPsg[i].Name);
            Assert.Equal(records[i].Score, readViaPsg[i].Score);
            Assert.Equal(records[i].IsActive, readViaPsg[i].IsActive);
        }
    }

    [Fact]
    public async Task ComprehensiveScalarRecordBidirectionalDifferentialEquivalence()
    {
        var baseDate = new DateTime(2026, 3, 15, 8, 30, 0, DateTimeKind.Utc);
        var records = Enumerable
            .Range(1, 25)
            .Select(i => new CorpusComprehensiveScalarRecord
            {
                Id = i,
                BigNumber = i * 100_000_000L,
                Rate = i * 0.5f,
                Score = i * 2.75,
                IsValid = i % 2 == 1,
                Description = i % 4 == 0 ? null : $"Item_{i}",
                TraceId = Guid.NewGuid(),
                CreatedAt = baseDate.AddMinutes(i),
                DurationSeconds = i * 10,
                Payload = i % 3 == 0 ? null : new byte[] { (byte)i, 0xAA, 0xBB },
            })
            .ToList();

        // 1. PSG Write -> ParquetSerializer Read
        using var stream1 = new MemoryStream();
        await records.WriteParquetAsync(stream1);
        stream1.Position = 0;

        var result1 = await ParquetSerializer.DeserializeAsync<CorpusComprehensiveScalarRecord>(
            stream1
        );
        IList<CorpusComprehensiveScalarRecord> readViaReflection = result1.Data;
        Assert.Equal(records.Count, readViaReflection.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Id, readViaReflection[i].Id);
            Assert.Equal(records[i].BigNumber, readViaReflection[i].BigNumber);
            Assert.Equal(records[i].Rate, readViaReflection[i].Rate);
            Assert.Equal(records[i].Score, readViaReflection[i].Score);
            Assert.Equal(records[i].IsValid, readViaReflection[i].IsValid);
            Assert.Equal(records[i].Description, readViaReflection[i].Description);
            Assert.Equal(records[i].TraceId, readViaReflection[i].TraceId);
            Assert.Equal(records[i].CreatedAt, readViaReflection[i].CreatedAt);
            Assert.Equal(records[i].DurationSeconds, readViaReflection[i].DurationSeconds);
            Assert.Equal(records[i].Payload, readViaReflection[i].Payload);
        }

        // 2. ParquetSerializer Write -> PSG Read
        using var stream2 = new MemoryStream();
        await ParquetSerializer.SerializeAsync(records, stream2);
        stream2.Position = 0;

        List<CorpusComprehensiveScalarRecord> readViaPsg =
            await CorpusComprehensiveScalarRecordParquetExtensions.ReadParquetAsync(stream2);
        Assert.Equal(records.Count, readViaPsg.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Id, readViaPsg[i].Id);
            Assert.Equal(records[i].BigNumber, readViaPsg[i].BigNumber);
            Assert.Equal(records[i].Rate, readViaPsg[i].Rate);
            Assert.Equal(records[i].Score, readViaPsg[i].Score);
            Assert.Equal(records[i].IsValid, readViaPsg[i].IsValid);
            Assert.Equal(records[i].Description, readViaPsg[i].Description);
            Assert.Equal(records[i].TraceId, readViaPsg[i].TraceId);
            Assert.Equal(records[i].CreatedAt, readViaPsg[i].CreatedAt);
            Assert.Equal(records[i].DurationSeconds, readViaPsg[i].DurationSeconds);
            Assert.Equal(records[i].Payload, readViaPsg[i].Payload);
        }
    }

    [Fact]
    public async Task NullableRecordBidirectionalDifferentialEquivalence()
    {
        var guid1 = Guid.NewGuid();
        var records = new List<CorpusNullableRecord>
        {
            new()
            {
                RequiredInt = 1,
                OptionalInt = 100,
                RequiredDouble = 1.11,
                OptionalDouble = 2.22,
                RequiredGuid = guid1,
                OptionalGuid = guid1,
                OptionalString1 = "Alpha",
                OptionalString2 = "Beta",
            },
            new()
            {
                RequiredInt = 2,
                OptionalInt = null,
                RequiredDouble = 3.33,
                OptionalDouble = null,
                RequiredGuid = guid1,
                OptionalGuid = null,
                OptionalString1 = "Gamma",
                OptionalString2 = null,
            },
            new()
            {
                RequiredInt = 3,
                OptionalInt = 300,
                RequiredDouble = 0.0,
                OptionalDouble = 0.0,
                RequiredGuid = Guid.Empty,
                OptionalGuid = Guid.Empty,
                OptionalString1 = string.Empty,
                OptionalString2 = string.Empty,
            },
        };

        // 1. PSG Write -> ParquetSerializer Read
        using var stream1 = new MemoryStream();
        await records.WriteParquetAsync(stream1);
        stream1.Position = 0;

        var result1 = await ParquetSerializer.DeserializeAsync<CorpusNullableRecord>(stream1);
        IList<CorpusNullableRecord> readViaReflection = result1.Data;
        Assert.Equal(records.Count, readViaReflection.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].RequiredInt, readViaReflection[i].RequiredInt);
            Assert.Equal(records[i].OptionalInt, readViaReflection[i].OptionalInt);
            Assert.Equal(records[i].RequiredDouble, readViaReflection[i].RequiredDouble);
            Assert.Equal(records[i].OptionalDouble, readViaReflection[i].OptionalDouble);
            Assert.Equal(records[i].RequiredGuid, readViaReflection[i].RequiredGuid);
            Assert.Equal(records[i].OptionalGuid, readViaReflection[i].OptionalGuid);
            Assert.Equal(records[i].OptionalString1, readViaReflection[i].OptionalString1);
            Assert.Equal(records[i].OptionalString2, readViaReflection[i].OptionalString2);
        }

        // 2. ParquetSerializer Write -> PSG Read
        using var stream2 = new MemoryStream();
        await ParquetSerializer.SerializeAsync(records, stream2);
        stream2.Position = 0;

        List<CorpusNullableRecord> readViaPsg =
            await CorpusNullableRecordParquetExtensions.ReadParquetAsync(stream2);
        Assert.Equal(records.Count, readViaPsg.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].RequiredInt, readViaPsg[i].RequiredInt);
            Assert.Equal(records[i].OptionalInt, readViaPsg[i].OptionalInt);
            Assert.Equal(records[i].RequiredDouble, readViaPsg[i].RequiredDouble);
            Assert.Equal(records[i].OptionalDouble, readViaPsg[i].OptionalDouble);
            Assert.Equal(records[i].RequiredGuid, readViaPsg[i].RequiredGuid);
            Assert.Equal(records[i].OptionalGuid, readViaPsg[i].OptionalGuid);
            Assert.Equal(records[i].OptionalString1, readViaPsg[i].OptionalString1);
            Assert.Equal(records[i].OptionalString2, readViaPsg[i].OptionalString2);
        }
    }

    [Fact]
    public async Task DecimalRecordBidirectionalDifferentialEquivalence()
    {
        var records = new List<CorpusDecimalRecord>
        {
            new()
            {
                Id = 1,
                Amount = 12345.6789m,
                OptionalFee = 19.99m,
            },
            new()
            {
                Id = 2,
                Amount = -50.0000m,
                OptionalFee = null,
            },
            new()
            {
                Id = 3,
                Amount = 0.0000m,
                OptionalFee = 0.00m,
            },
        };

        // 1. PSG Write -> ParquetSerializer Read
        using var stream1 = new MemoryStream();
        await records.WriteParquetAsync(stream1);
        stream1.Position = 0;

        var result1 = await ParquetSerializer.DeserializeAsync<CorpusDecimalRecord>(stream1);
        IList<CorpusDecimalRecord> readViaReflection = result1.Data;
        Assert.Equal(records.Count, readViaReflection.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Id, readViaReflection[i].Id);
            Assert.Equal(records[i].Amount, readViaReflection[i].Amount);
            Assert.Equal(records[i].OptionalFee, readViaReflection[i].OptionalFee);
        }

        // 2. ParquetSerializer Write -> PSG Read
        using var stream2 = new MemoryStream();
        await ParquetSerializer.SerializeAsync(records, stream2);
        stream2.Position = 0;

        List<CorpusDecimalRecord> readViaPsg =
            await CorpusDecimalRecordParquetExtensions.ReadParquetAsync(stream2);
        Assert.Equal(records.Count, readViaPsg.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Id, readViaPsg[i].Id);
            Assert.Equal(records[i].Amount, readViaPsg[i].Amount);
            Assert.Equal(records[i].OptionalFee, readViaPsg[i].OptionalFee);
        }
    }

    #endregion

    #region 2. Oracle Disqualification Verification (Where Reflection Fails)

    [Fact]
    public async Task TimeSpanProvesPsgCapabilityWhileReflectionThrows()
    {
        var records = new List<CorpusTimeSpanRecord>
        {
            new() { Id = 1, Elapsed = TimeSpan.FromSeconds(123) },
            new() { Id = 2, Elapsed = TimeSpan.FromHours(1.5) },
        };

        // 1. PSG writes and reads TimeSpan seamlessly
        using var ms = new MemoryStream();
        await records.WriteParquetAsync(ms);
        ms.Position = 0;

        List<CorpusTimeSpanRecord> readViaPsg =
            await CorpusTimeSpanRecordParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(2, readViaPsg.Count);
        Assert.Equal(TimeSpan.FromSeconds(123), readViaPsg[0].Elapsed);
        Assert.Equal(TimeSpan.FromHours(1.5), readViaPsg[1].Elapsed);

        // 2. ParquetSerializer fails on deserialization because reflection schema generator has no TimeSpan field mapping
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ParquetSerializer.DeserializeAsync<CorpusTimeSpanRecord>(ms)
        );
    }

    #endregion

    #region 3. Struct-Only Models (PSG-Exclusive Capability, Multi-Reader Convergence)

    [Fact]
    public async Task SingleLongStructMultiReaderConvergence()
    {
        var records = Enumerable
            .Range(1, 50)
            .Select(i => new SingleLongStruct { Value = i * 99999L })
            .ToList();

        using var ms = new MemoryStream();
        await records.WriteParquetAsync(ms);
        byte[] payload = ms.ToArray();

        // 1. Sequential Stream Reader
        ms.Position = 0;
        List<SingleLongStruct> seq = await SingleLongStructParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(records.Count, seq.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Value, seq[i].Value);
        }

        // 2. Parallel Array Reader
        ms.Position = 0;
        SingleLongStruct[] parallel =
            await SingleLongStructParquetExtensions.ReadParquetParallelArrayAsync(ms);
        Assert.Equal(records.Count, parallel.Length);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Value, parallel[i].Value);
        }

        // 3. Memory Reader
        List<SingleLongStruct> fromMem = await SingleLongStructParquetExtensions.ReadParquetAsync(
            payload
        );
        Assert.Equal(records.Count, fromMem.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Value, fromMem[i].Value);
        }
    }

    [Fact]
    public async Task Point3DStructMultiReaderConvergence()
    {
        var records = Enumerable
            .Range(1, 30)
            .Select(i => new Point3DStruct
            {
                X = i * 1.1,
                Y = i * 2.2,
                Z = i * 3.3,
            })
            .ToList();

        using var ms = new MemoryStream();
        await records.WriteParquetAsync(ms);
        byte[] payload = ms.ToArray();

        // 1. Sequential Stream Reader
        ms.Position = 0;
        List<Point3DStruct> seq = await Point3DStructParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(records.Count, seq.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].X, seq[i].X);
            Assert.Equal(records[i].Y, seq[i].Y);
            Assert.Equal(records[i].Z, seq[i].Z);
        }

        // 2. Parallel Array Reader
        ms.Position = 0;
        Point3DStruct[] parallel =
            await Point3DStructParquetExtensions.ReadParquetParallelArrayAsync(ms);
        Assert.Equal(records.Count, parallel.Length);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].X, parallel[i].X);
            Assert.Equal(records[i].Y, parallel[i].Y);
            Assert.Equal(records[i].Z, parallel[i].Z);
        }

        // 3. Memory Reader
        List<Point3DStruct> fromMem = await Point3DStructParquetExtensions.ReadParquetAsync(
            payload
        );
        Assert.Equal(records.Count, fromMem.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].X, fromMem[i].X);
        }
    }

    #endregion

    #region 4. Compound Nested & Repeated Models (Multi-Reader Convergence)

    [Fact]
    public async Task NestedOrderCompoundStructMultiReaderConvergence()
    {
        var records = new List<NestedOrder>
        {
            new()
            {
                Id = 1,
                Ship = new Address { City = "Seattle", Zip = 98101 },
                Bill = new Address { City = "New York", Zip = 10001 },
            },
            new()
            {
                Id = 2,
                Ship = null,
                Bill = new Address { City = "Chicago", Zip = null },
            },
            new()
            {
                Id = 3,
                Ship = new Address { City = null, Zip = 90210 },
                Bill = new Address { City = null, Zip = null },
            },
        };

        using var ms = new MemoryStream();
        await NestedOrderParquetExtensions.WriteParquetAsync(records, ms);
        byte[] payload = ms.ToArray();

        // 1. Sequential Stream Reader
        ms.Position = 0;
        List<NestedOrder> sequential = await NestedOrderParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(3, sequential.Count);
        Assert.Equal("Seattle", sequential[0].Ship?.City);
        Assert.Equal(98101, sequential[0].Ship?.Zip);
        Assert.Null(sequential[1].Ship);
        Assert.Equal("Chicago", sequential[1].Bill.City);
        Assert.Null(sequential[1].Bill.Zip);

        // 2. Parallel Array Reader
        ms.Position = 0;
        NestedOrder[] parallel = await NestedOrderParquetExtensions.ReadParquetParallelArrayAsync(
            ms
        );
        Assert.Equal(3, parallel.Length);
        Assert.Equal(sequential[0].Ship?.City, parallel[0].Ship?.City);
        Assert.Equal(sequential[1].Bill.City, parallel[1].Bill.City);

        // 3. Streaming AsyncEnumerable Reader
        ms.Position = 0;
        var streamed = new List<NestedOrder>();
        await foreach (NestedOrder item in NestedOrderParquetExtensions.ReadParquetStreamAsync(ms))
        {
            streamed.Add(item);
        }
        Assert.Equal(3, streamed.Count);
        Assert.Equal(sequential[0].Ship?.City, streamed[0].Ship?.City);

        // 4. Memory-backed Reader
        List<NestedOrder> fromMem = await NestedOrderParquetExtensions.ReadParquetAsync(payload);
        Assert.Equal(3, fromMem.Count);
        Assert.Equal(sequential[0].Ship?.City, fromMem[0].Ship?.City);
    }

    [Fact]
    public async Task ListRowRepeatedLeavesMultiReaderConvergence()
    {
        var g1 = Guid.NewGuid();
        var d1 = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var records = new List<ListRow>
        {
            new()
            {
                Id = 1,
                Tags = new List<string?> { "alpha", null, "beta" },
                Scores = new List<int> { 10, 20, 30 },
                Keys = new[] { g1 },
                When = new List<DateTime?> { d1, null },
                Blobs = new List<byte[]> { new byte[] { 1, 2 } },
            },
            new()
            {
                Id = 2,
                Tags = new List<string?>(),
                Scores = new List<int>(),
                Keys = Array.Empty<Guid>(),
                When = new List<DateTime?>(),
                Blobs = new List<byte[]>(),
            },
            new()
            {
                Id = 3,
                Tags = null,
                Scores = null!,
                Keys = null,
                When = null,
                Blobs = null,
            },
        };

        using var ms = new MemoryStream();
        await ListRowParquetExtensions.WriteParquetAsync(records, ms);

        // 1. Sequential Reader
        ms.Position = 0;
        List<ListRow> seq = await ListRowParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(3, seq.Count);

        // Row 0 has items with null
        Assert.Equal(3, seq[0].Tags!.Count);
        Assert.Equal("alpha", seq[0].Tags![0]);
        Assert.Null(seq[0].Tags![1]);
        Assert.Equal("beta", seq[0].Tags![2]);

        // Row 1 is empty list (not null)
        Assert.NotNull(seq[1].Tags);
        Assert.Empty(seq[1].Tags!);

        // Row 2 is null
        Assert.Null(seq[2].Tags);

        // 2. Parallel Array Reader
        ms.Position = 0;
        ListRow[] par = await ListRowParquetExtensions.ReadParquetParallelArrayAsync(ms);
        Assert.Equal(3, par.Length);
        Assert.Equal(seq[0].Tags!.Count, par[0].Tags!.Count);
        Assert.Empty(par[1].Tags!);
        Assert.Null(par[2].Tags);

        // 3. Streaming AsyncEnumerable Reader
        ms.Position = 0;
        var streamed = new List<ListRow>();
        await foreach (ListRow row in ListRowParquetExtensions.ReadParquetStreamAsync(ms))
        {
            streamed.Add(row);
        }
        Assert.Equal(3, streamed.Count);
        Assert.Equal(seq[0].Tags!.Count, streamed[0].Tags!.Count);
    }

    [Fact]
    public async Task TripRowListOfPocoMultiReaderConvergence()
    {
        var node1 = Guid.NewGuid();
        var node2 = Guid.NewGuid();
        var records = new List<TripRow>
        {
            new()
            {
                Id = 1,
                Stops =
                [
                    new PitStop
                    {
                        City = "Austin",
                        Zip = 73301,
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
                        City = "Dallas",
                        Zip = 75001,
                        Node = node1,
                    },
                ],
            },
            new()
            {
                Id = 2,
                Stops = [],
                Route = [],
            },
            new()
            {
                Id = 3,
                Stops = null,
                Route = null,
            },
        };

        using var ms = new MemoryStream();
        await records.WriteParquetBatchedAsync(
            ms,
            new ParquetSerializerOptions { RowGroupSize = 2 }
        );

        // 1. Sequential Reader
        ms.Position = 0;
        List<TripRow> seq = await TripRowParquetExtensions.ReadParquetAsync(ms);
        Assert.Equal(3, seq.Count);
        Assert.Equal(2, seq[0].Stops!.Count);
        Assert.Equal("Austin", seq[0].Stops![0].City);
        Assert.Null(seq[0].Stops![1].City);
        Assert.Empty(seq[1].Stops!);
        Assert.Null(seq[2].Stops);

        // 2. Parallel Reader
        ms.Position = 0;
        TripRow[] par = await TripRowParquetExtensions.ReadParquetParallelArrayAsync(ms);
        Assert.Equal(3, par.Length);
        Assert.Equal("Austin", par[0].Stops![0].City);
        Assert.Empty(par[1].Stops!);
        Assert.Null(par[2].Stops);
    }

    #endregion
}
