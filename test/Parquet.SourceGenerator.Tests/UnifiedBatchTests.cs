using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Parquet.File.Values.Primitives;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// A nullable <c>Value</c> next to a property literally named <c>ValueDefinitionLevels</c>: the
/// batch's derived definition-levels member for <c>Value</c> used to land on the second property's
/// name and emit the same member twice (#384 part a).
/// </summary>
[ParquetSerializable]
public partial record BatchNameClashModel
{
    [ParquetColumn("value")]
    public int? Value { get; init; }

    [ParquetColumn("value_levels")]
    public int ValueDefinitionLevels { get; init; }
}

/// <summary>Nullable value columns of several wire shapes, for the opt-in <c>T?</c> fill.</summary>
[ParquetSerializable]
public partial record BatchFillModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("score")]
    public double? Score { get; init; }

    [ParquetColumn("tag")]
    public Guid? Tag { get; init; }

    [ParquetColumn("day")]
    public DateOnly? Day { get; init; }

    [ParquetColumn("kind")]
    public TypeMatrixStatus? Kind { get; init; }
}

/// <summary>A required column in the file that the reading model declares nullable.</summary>
[ParquetSerializable]
public partial record BatchRequiredInFile
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("score")]
    public double Score { get; init; }
}

/// <summary>The same file read with <c>score</c> declared nullable.</summary>
[ParquetSerializable]
public partial record BatchOptionalInModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("score")]
    public double? Score { get; init; }
}

/// <summary>
/// The unified <c>&lt;Model&gt;Batch</c> (#508): one type that <c>AsBatches()</c> yields and the
/// columnar <c>WriteParquetAsync</c> takes. These tests pin the round trip, the layout the read
/// path produces, the opt-in <c>T?</c> fill and the borrowed-batch lifetime rule (#369).
/// </summary>
public sealed class UnifiedBatchTests
{
    private static readonly int[] Ids2 = [1, 2];
    private static readonly int[] Ids3 = [1, 2, 3];
    private static readonly int[] Zeros1 = [0];
    private static readonly int[] Zeros2 = [0, 0];
    private static readonly int[] Zeros3 = [0, 0, 0];
    private static readonly int[] Ones2 = [1, 1];
    private static readonly int[] Ones3 = [1, 1, 1];
    private static readonly int[] One = [1];
    private static readonly int[] Mask101 = [1, 0, 1];
    private static readonly int[] Packed1030 = [10, 30];
    private static readonly int[] Seven89 = [7, 8, 9];
    private static readonly double[] Pair = [1.5, 2.5];
    private static readonly double?[] ExpectedFill = [1.5, null, 2.5];
    private static readonly int?[] ExpectedValues = [10, null, 30];

    // ── Round trip: read batches, write them back, read again ─────────────────────────

    [Fact]
    public async Task RequiredEveryColumnTypeRoundTripsThroughBatchesAsync()
    {
        GeneratedTypeMatrixRecord[] expected = RequiredRows();
        using var source = new MemoryStream();
        await expected.WriteParquetBatchedAsync(
            source,
            new ParquetSerializerOptions { RowGroupSize = 2 }
        );
        byte[] bytes = source.ToArray();

        var rewritten = new List<byte[]>();
        await foreach (
            GeneratedTypeMatrixRecordBatch batch in GeneratedTypeMatrixRecordParquet
                .From(new ReadOnlyMemory<byte>(bytes))
                .AsBatches()
        )
        {
            using var output = new MemoryStream();
            await batch.WriteParquetAsync(output);
            rewritten.Add(output.ToArray());
        }

        rewritten.Count.ShouldBe(2);
        var actual = new List<GeneratedTypeMatrixRecord>();
        foreach (byte[] file in rewritten)
        {
            actual.AddRange(
                await GeneratedTypeMatrixRecordParquet.From(new MemoryStream(file)).ToArrayAsync()
            );
        }

        AssertEquivalent(expected, actual);
    }

    [Fact]
    public async Task NullableEveryColumnTypeRoundTripsValuesAndNullsThroughBatchesAsync()
    {
        // Row groups of 4: the first two have no value in DoubleValue at all (an all-null chunk,
        // which the read path answers without a page read), the rest mix nulls per column.
        NullableGeneratedTypeMatrixRecord[] expected = Enumerable
            .Range(0, 23)
            .Select(SparseRow)
            .ToArray();
        using var source = new MemoryStream();
        await expected.WriteParquetBatchedAsync(
            source,
            new ParquetSerializerOptions { RowGroupSize = 4 }
        );
        byte[] bytes = source.ToArray();

        // Stream source, and the in-memory source: both reach the same iterator.
        foreach (bool fromMemory in new[] { false, true })
        {
            var rewritten = new List<byte[]>();
            var batches = fromMemory
                ? NullableGeneratedTypeMatrixRecordParquet
                    .From(new ReadOnlyMemory<byte>(bytes))
                    .AsBatches()
                : NullableGeneratedTypeMatrixRecordParquet
                    .From(new MemoryStream(bytes))
                    .AsBatches();
            await foreach (NullableGeneratedTypeMatrixRecordBatch batch in batches)
            {
                using var output = new MemoryStream();
                await batch.WriteParquetAsync(output);
                rewritten.Add(output.ToArray());
            }

            rewritten.Count.ShouldBe(6);
            var actual = new List<NullableGeneratedTypeMatrixRecord>();
            foreach (byte[] file in rewritten)
            {
                actual.AddRange(
                    await NullableGeneratedTypeMatrixRecordParquet
                        .From(new MemoryStream(file))
                        .ToArrayAsync()
                );
            }

            AssertEquivalent(expected, actual);
            actual.Count(r => r.IntValue is null).ShouldBeGreaterThan(0);
            actual.Count(r => r.StringValue is null).ShouldBeGreaterThan(0);
            actual.Count(r => r.BytesValue is null).ShouldBeGreaterThan(0);
            actual.Count(r => r.DoubleValue is null).ShouldBeGreaterThan(8);
        }
    }

    [Fact]
    public async Task ReadBatchUsesThePackedPlusLevelsLayoutAsync()
    {
        NullableGeneratedTypeMatrixRecord[] rows = Enumerable
            .Range(0, 10)
            .Select(SparseRow)
            .ToArray();
        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 10 }
        );
        stream.Position = 0;

        int seen = 0;
        await foreach (
            NullableGeneratedTypeMatrixRecordBatch batch in NullableGeneratedTypeMatrixRecordParquet
                .From(stream)
                .AsBatches()
        )
        {
            seen++;
            batch.RowCount.ShouldBe(10);

            // Numerics: packed non-null values, plus one level per row.
            int expectedPresent = rows.Count(r => r.IntValue is not null);
            batch.IntValueDefinitionLevels.Length.ShouldBe(10);
            batch.IntValueDefinitionLevels.ToArray().Sum().ShouldBe(expectedPresent);
            batch.IntValue.Length.ShouldBe(expectedPresent);
            batch
                .IntValue.ToArray()
                .ShouldBe(rows.Where(r => r.IntValue is not null).Select(r => r.IntValue!.Value));

            // Strings and byte arrays: one inline-nullable memory per row, null where the row is null.
            batch.StringValue.Length.ShouldBe(10);
            for (int i = 0; i < 10; i++)
            {
                ReadOnlyMemory<char>? text = batch.StringValue.Span[i];
                if (rows[i].StringValue is null)
                {
                    text.ShouldBeNull();
                }
                else
                {
                    text!.Value.ToString().ShouldBe(rows[i].StringValue);
                }
            }
        }

        seen.ShouldBe(1);
    }

    [Fact]
    public async Task RequiredColumnReadAsNullableStillYieldsAllPresentRowsAsync()
    {
        using var stream = new MemoryStream();
        await new[]
        {
            new BatchRequiredInFile { Id = 1, Score = 1.5 },
            new BatchRequiredInFile { Id = 2, Score = 2.5 },
        }.WriteParquetAsync(stream);
        stream.Position = 0;

        int seen = 0;
        await foreach (
            BatchOptionalInModelBatch batch in BatchOptionalInModelParquet.From(stream).AsBatches()
        )
        {
            seen++;
            batch.Score.ToArray().ShouldBe(Pair);
            batch.ScoreDefinitionLevels.ToArray().ShouldBe(Ones2);
        }

        seen.ShouldBe(1);
    }

    // ── The opt-in T? fill ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FillNullableMatchesTheRowsForEveryNullableValueShapeAsync()
    {
        BatchFillModel[] rows = Enumerable
            .Range(0, 9)
            .Select(i => new BatchFillModel
            {
                Id = i,
                Score = i % 3 == 0 ? null : i * 0.5,
                Tag = i % 2 == 0 ? null : new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1),
                Day = i % 4 == 1 ? null : new DateOnly(2024, 1, i + 1),
                Kind = i < 5 ? null : TypeMatrixStatus.Closed,
            })
            .ToArray();
        using var stream = new MemoryStream();
        await rows.WriteParquetBatchedAsync(
            stream,
            new ParquetSerializerOptions { RowGroupSize = 9 }
        );
        stream.Position = 0;

        int seen = 0;
        await foreach (BatchFillModelBatch batch in BatchFillModelParquet.From(stream).AsBatches())
        {
            seen++;
            double?[] score = new double?[batch.RowCount];
            Guid?[] tag = new Guid?[batch.RowCount];
            DateTime?[] day = new DateTime?[batch.RowCount];
            int?[] kind = new int?[batch.RowCount];
            batch.FillScoreNullable(score);
            batch.FillTagNullable(tag);
            batch.FillDayNullable(day);
            batch.FillKindNullable(kind);

            score.ShouldBe(rows.Select(r => r.Score));
            tag.ShouldBe(rows.Select(r => r.Tag));
            day.ShouldBe(
                rows.Select(r => r.Day is { } d ? d.ToDateTime(TimeOnly.MinValue) : (DateTime?)null)
            );
            kind.ShouldBe(rows.Select(r => r.Kind is { } k ? (int)k : (int?)null));
        }

        seen.ShouldBe(1);
    }

    [Fact]
    public void FillNullableChecksItsDestinationAndTheLanes()
    {
        var batch = new BatchFillModelBatch(
            rowCount: 3,
            id: Ids3,
            score: Pair,
            scoreDefinitionLevels: Mask101,
            tag: default(ReadOnlyMemory<Guid>),
            tagDefinitionLevels: Zeros3,
            day: default(ReadOnlyMemory<DateTime>),
            dayDefinitionLevels: Zeros3,
            kind: One,
            kindDefinitionLevels: Ones3
        );

        var exact = new double?[3];
        batch.FillScoreNullable(exact);
        exact.ShouldBe(ExpectedFill);

        ArgumentException tooShort = Should.Throw<ArgumentException>(() =>
            batch.FillScoreNullable(new double?[2])
        );
        tooShort.ParamName.ShouldBe("destination");
        tooShort.Message.ShouldContain("Score");

        // Three rows marked present but one packed value: the lane lies, and the fill says so
        // instead of reading past it.
        Should
            .Throw<InvalidOperationException>(() => batch.FillKindNullable(new int?[3]))
            .Message.ShouldContain("Kind");
    }

    // ── Constructor validation, and the name handling of #384 ─────────────────────────

    [Fact]
    public void ConstructorRejectsBadRowCountShortLanesAndShortLevels()
    {
        var ok = new BatchFillModelBatch(
            rowCount: 2,
            id: Ids2,
            score: default(ReadOnlyMemory<double>),
            scoreDefinitionLevels: Zeros2,
            tag: default(ReadOnlyMemory<Guid>),
            tagDefinitionLevels: Zeros2,
            day: default(ReadOnlyMemory<DateTime>),
            dayDefinitionLevels: Zeros2,
            kind: default(ReadOnlyMemory<int>),
            kindDefinitionLevels: Zeros2
        );
        ok.RowCount.ShouldBe(2);

        Should
            .Throw<ArgumentOutOfRangeException>(() => Build(rowCount: -1))
            .ParamName.ShouldBe("rowCount");
        Should.Throw<ArgumentException>(() => Build(rowCount: 3)).ParamName.ShouldBe("id");
        ArgumentException shortLevels = Should.Throw<ArgumentException>(() =>
            Build(rowCount: 2, scoreLevels: Zeros1)
        );
        shortLevels.ParamName.ShouldBe("scoreDefinitionLevels");
        shortLevels.Message.ShouldContain("definition levels");

        static BatchFillModelBatch Build(int rowCount, int[]? scoreLevels = null) =>
            new(
                rowCount: rowCount,
                id: Ids2,
                score: default(ReadOnlyMemory<double>),
                scoreDefinitionLevels: scoreLevels ?? Zeros2,
                tag: default(ReadOnlyMemory<Guid>),
                tagDefinitionLevels: Zeros2,
                day: default(ReadOnlyMemory<DateTime>),
                dayDefinitionLevels: Zeros2,
                kind: default(ReadOnlyMemory<int>),
                kindDefinitionLevels: Zeros2
            );
    }

    [Fact]
    public async Task NullableValueNextToAValueDefinitionLevelsPropertyStillBuildsAndRoundTripsAsync()
    {
        // #384(a): the batch's derived levels member for Value is moved aside (ValueDefinitionLevels_)
        // rather than colliding with the model's own ValueDefinitionLevels column.
        var batch = new BatchNameClashModelBatch(3, Packed1030, Mask101, Seven89);

        batch.ValueDefinitionLevels.ToArray().ShouldBe(Seven89);
        batch.ValueDefinitionLevels_.ToArray().ShouldBe(Mask101);

        using var stream = new MemoryStream();
        await batch.WriteParquetAsync(stream);
        stream.Position = 0;
        BatchNameClashModel[] read = await BatchNameClashModelParquet.From(stream).ToArrayAsync();

        read.Select(r => r.Value).ToArray().ShouldBe(ExpectedValues);
        read.Select(r => r.ValueDefinitionLevels).ToArray().ShouldBe(Seven89);
    }

    // ── Lifetime (#369): a batch is valid until the next MoveNextAsync ────────────────

    [Fact]
    public async Task BatchIsUsableInsideTheLoopBodyAsync()
    {
        using MemoryStream stream = await WriteMetricsAsync(rows: 10, rowGroupSize: 5);

        double total = 0;
        await foreach (
            ColumnBatchMetricBatch batch in ColumnBatchMetricParquet.From(stream).AsBatches()
        )
        {
            foreach (double value in batch.Value.Span)
            {
                total += value;
            }

            batch.RowCount.ShouldBe(5);
        }

        total.ShouldBe(Enumerable.Range(1, 10).Sum(i => i * 1.5));
    }

    [Fact]
    public async Task BatchKeptPastTheLoopThrowsObjectDisposedFromEveryLaneAsync()
    {
        using MemoryStream stream = await WriteMetricsAsync(rows: 10, rowGroupSize: 5);

        ColumnBatchMetricBatch kept = default;
        await foreach (
            ColumnBatchMetricBatch batch in ColumnBatchMetricParquet.From(stream).AsBatches()
        )
        {
            kept = batch;
        }

        kept.RowCount.ShouldBe(5);
        Should.Throw<ObjectDisposedException>(() => kept.Timestamp);
        Should.Throw<ObjectDisposedException>(() => kept.Value);
        Should.Throw<ObjectDisposedException>(() => kept.Weight);

        using var output = new MemoryStream();
        await Should.ThrowAsync<ObjectDisposedException>(() => kept.WriteParquetAsync(output));
    }

    [Fact]
    public async Task EarlierBatchExpiresWhenTheEnumeratorAdvancesAsync()
    {
        using MemoryStream stream = await WriteMetricsAsync(rows: 12, rowGroupSize: 4);

        var batches = new List<ColumnBatchMetricBatch>();
        await foreach (
            ColumnBatchMetricBatch batch in ColumnBatchMetricParquet.From(stream).AsBatches()
        )
        {
            // The current batch is live; every earlier one has already been returned.
            batch.Value.Length.ShouldBe(4);
            foreach (ColumnBatchMetricBatch earlier in batches)
            {
                Should.Throw<ObjectDisposedException>(() => earlier.Value);
            }

            batches.Add(batch);
        }

        batches.Count.ShouldBe(3);
    }

    [Fact]
    public async Task BreakingOutOfTheLoopExpiresTheBatchAsync()
    {
        using MemoryStream stream = await WriteMetricsAsync(rows: 10, rowGroupSize: 5);

        ColumnBatchMetricBatch kept = default;
        await foreach (
            ColumnBatchMetricBatch batch in ColumnBatchMetricParquet.From(stream).AsBatches()
        )
        {
            kept = batch;
            kept.Value.Length.ShouldBe(5);
            break;
        }

        Should.Throw<ObjectDisposedException>(() => kept.Value);
    }

    [Fact]
    public async Task ExpiredBatchMessageSaysHowToFixItAsync()
    {
        using MemoryStream stream = await WriteMetricsAsync(rows: 4, rowGroupSize: 4);

        ColumnBatchMetricBatch kept = default;
        await foreach (
            ColumnBatchMetricBatch batch in ColumnBatchMetricParquet.From(stream).AsBatches()
        )
        {
            kept = batch;
        }

        ObjectDisposedException error = Should.Throw<ObjectDisposedException>(() => kept.Value);
        error.Message.ShouldContain("AsBatches()");
        error.Message.ShouldContain("loop body");
    }

    [Fact]
    public async Task EnumerationsDoNotExpireEachOthersBatchesAsync()
    {
        using MemoryStream first = await WriteMetricsAsync(rows: 6, rowGroupSize: 3);
        using MemoryStream second = await WriteMetricsAsync(rows: 6, rowGroupSize: 3);

        await using var firstEnumerator = ColumnBatchMetricParquet
            .From(first)
            .AsBatches()
            .GetAsyncEnumerator();
        await using var secondEnumerator = ColumnBatchMetricParquet
            .From(second)
            .AsBatches()
            .GetAsyncEnumerator();

        (await firstEnumerator.MoveNextAsync()).ShouldBeTrue();
        ColumnBatchMetricBatch fromFirst = firstEnumerator.Current;

        (await secondEnumerator.MoveNextAsync()).ShouldBeTrue();
        (await secondEnumerator.MoveNextAsync()).ShouldBeTrue();

        fromFirst.Value.Length.ShouldBe(3);
    }

    [Fact]
    public async Task ConstructedAndDefaultBatchesNeverExpireAsync()
    {
        double[] values = [1, 2, 3];
        long[] stamps = [10, 20, 30];
        var constructed = new ColumnBatchMetricBatch(3, stamps, values, values);
        ColumnBatchMetricBatch empty = default;

        // Nothing returns a constructed batch's buffers but its owner, so there is nothing to check.
        await Task.Yield();
        constructed.Value.ToArray().ShouldBe(values);
        constructed.Timestamp.ToArray().ShouldBe(stamps);
        empty.RowCount.ShouldBe(0);
        empty.Value.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task PooledBuffersReturnedAfterTheLoopAreNotVisibleThroughAKeptBatchAsync()
    {
        // The companion to the issue's reproduction: after the iterator is done the pool may give
        // the same array to anybody. The kept batch must refuse to hand it out, not read it.
        using MemoryStream stream = await WriteMetricsAsync(rows: 8, rowGroupSize: 8);

        ColumnBatchMetricBatch kept = default;
        await foreach (
            ColumnBatchMetricBatch batch in ColumnBatchMetricParquet.From(stream).AsBatches()
        )
        {
            kept = batch;
        }

        double[] other = System.Buffers.ArrayPool<double>.Shared.Rent(8);
        try
        {
            Array.Fill(other, -1d);
            Should.Throw<ObjectDisposedException>(() => kept.Value.Span[0].ShouldBe(-1d));
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(other);
        }
    }

    // ── Shape ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BatchIsAReadonlyStructWithGetOnlyPropertiesAndNoPublicFields()
    {
        foreach (
            Type type in new[]
            {
                typeof(ColumnBatchOrderBatch),
                typeof(ColumnBatchMetricBatch),
                typeof(NullableGeneratedTypeMatrixRecordBatch),
                typeof(BatchNameClashModelBatch),
            }
        )
        {
            type.IsValueType.ShouldBeTrue(type.Name);
            type.GetCustomAttributes(inherit: false)
                .Any(a =>
                    a.GetType().FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute"
                )
                .ShouldBeTrue($"{type.Name} must be a readonly struct");
            type.GetFields(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty(type.Name);
            type.GetFields(BindingFlags.Public | BindingFlags.Static).ShouldBeEmpty(type.Name);

            foreach (
                PropertyInfo property in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance
                )
            )
            {
                property.CanRead.ShouldBeTrue($"{type.Name}.{property.Name}");
                property.SetMethod.ShouldBeNull($"{type.Name}.{property.Name}");
            }

            // The lease is plumbing: nothing about it is public.
            type.GetNestedTypes(BindingFlags.Public).ShouldBeEmpty(type.Name);
        }
    }

    [Fact]
    public void AsBatchesReturnsTheTypeTheColumnarWriterTakes()
    {
        MethodInfo asBatches = typeof(ColumnBatchOrderParquetReader).GetMethod("AsBatches")!;
        asBatches.ReturnType.ShouldBe(typeof(IAsyncEnumerable<ColumnBatchOrderBatch>));
        typeof(ColumnBatchOrderParquetReader).GetMethod("Batches").ShouldBeNull();

        MethodInfo write = typeof(ColumnBatchOrderParquetExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m =>
                m.Name == "WriteParquetAsync"
                && m.GetParameters()[0].ParameterType == typeof(ColumnBatchOrderBatch)
            );
        write.GetParameters()[0].ParameterType.ShouldBe(typeof(ColumnBatchOrderBatch));

        // The two retired types are gone.
        typeof(ColumnBatchOrderParquetExtensions).GetNestedType("ColumnBatch").ShouldBeNull();
        typeof(ColumnBatchOrderBatch)
            .Assembly.GetType(
                typeof(ColumnBatchOrderBatch).Namespace + ".ColumnBatchOrderColumnarBatch"
            )
            .ShouldBeNull();
    }

    [Fact]
    public async Task ReadBatchWritesBackToTheSameLogicalFileAsync()
    {
        // The headline use from #508: `await foreach (var batch in reader.AsBatches())
        // await batch.WriteParquetAsync(output)`.
        using MemoryStream source = await WriteMetricsAsync(rows: 9, rowGroupSize: 9);

        using var output = new MemoryStream();
        await foreach (
            ColumnBatchMetricBatch batch in ColumnBatchMetricParquet.From(source).AsBatches()
        )
        {
            await batch.WriteParquetAsync(output);
        }

        output.Position = 0;
        ColumnBatchMetric[] read = await ColumnBatchMetricParquet.From(output).ToArrayAsync();
        read.Select(r => r.Timestamp).ShouldBe(Enumerable.Range(1, 9).Select(i => (long)i));
        read.Select(r => r.Value).ShouldBe(Enumerable.Range(1, 9).Select(i => i * 1.5));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static async Task<MemoryStream> WriteMetricsAsync(int rows, int rowGroupSize)
    {
        var stream = new MemoryStream();
        await Enumerable
            .Range(1, rows)
            .Select(i => new ColumnBatchMetric
            {
                Timestamp = i,
                Value = i * 1.5,
                Weight = i * 0.25,
            })
            .ToList()
            .WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = rowGroupSize }
            );
        stream.Position = 0;
        return stream;
    }

    private static GeneratedTypeMatrixRecord[] RequiredRows() =>
        [
            new()
            {
                BoolValue = true,
                ByteValue = byte.MaxValue,
                SByteValue = sbyte.MinValue,
                ShortValue = short.MinValue,
                UShortValue = ushort.MaxValue,
                IntValue = int.MinValue,
                UIntValue = uint.MaxValue,
                LongValue = long.MinValue,
                ULongValue = ulong.MaxValue,
                FloatValue = -123.5f,
                DoubleValue = double.MaxValue / 2,
                StringValue = string.Empty,
                BytesValue = Array.Empty<byte>(),
                DateOnlyValue = new DateOnly(2024, 1, 2),
                MemoryBytesValue = new byte[] { 1, 2, 3 },
                MemoryCharsValue = "first".AsMemory(),
                IntervalValue = new Interval(1, 2, 3),
                DecimalValue = 12345678901234.5678m,
                DateTimeValue = new DateTime(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc),
                TimeSpanValue = TimeSpan.FromMilliseconds(123456),
                TimeOnlyValue = new TimeOnly(3, 4, 5, 678),
                GuidValue = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                EnumValue = TypeMatrixStatus.Active,
            },
            new()
            {
                BoolValue = false,
                ByteValue = 0,
                SByteValue = sbyte.MaxValue,
                ShortValue = short.MaxValue,
                UShortValue = 0,
                IntValue = int.MaxValue,
                UIntValue = 0,
                LongValue = long.MaxValue,
                ULongValue = 0,
                FloatValue = float.MaxValue / 2,
                DoubleValue = double.MinValue / 2,
                StringValue = "unicode: café 日本",
                BytesValue = new byte[] { 0, 1, 255 },
                DateOnlyValue = DateOnly.MaxValue,
                MemoryBytesValue = new byte[] { 255, 254 },
                MemoryCharsValue = "second".AsMemory(),
                IntervalValue = new Interval(2, 3, 4),
                DecimalValue = -0.0001m,
                DateTimeValue = new DateTime(2025, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
                TimeSpanValue = TimeSpan.Zero,
                TimeOnlyValue = TimeOnly.MaxValue,
                GuidValue = Guid.Empty,
                EnumValue = TypeMatrixStatus.Closed,
            },
            new()
            {
                BoolValue = true,
                ByteValue = 1,
                SByteValue = -1,
                ShortValue = 1,
                UShortValue = 1,
                IntValue = 1,
                UIntValue = 1,
                LongValue = 1,
                ULongValue = 1,
                FloatValue = 0.5f,
                DoubleValue = -0.5,
                StringValue = "repeated",
                BytesValue = new byte[] { 42 },
                DateOnlyValue = new DateOnly(2000, 1, 1),
                MemoryBytesValue = ReadOnlyMemory<byte>.Empty,
                MemoryCharsValue = ReadOnlyMemory<char>.Empty,
                IntervalValue = new Interval(0, 0, 0),
                DecimalValue = 1.0000m,
                DateTimeValue = DateTime.UnixEpoch,
                TimeSpanValue = TimeSpan.FromMilliseconds(1),
                TimeOnlyValue = TimeOnly.MinValue,
                GuidValue = new Guid(7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1),
                EnumValue = TypeMatrixStatus.Pending,
            },
        ];

    /// <summary>
    /// A row with every column populated, then nulled by a per-column pattern so each column mixes
    /// present and null rows. <c>DoubleValue</c> is null for the first eight rows (two whole row groups).
    /// </summary>
    private static NullableGeneratedTypeMatrixRecord SparseRow(int i)
    {
        int seed = (i % 20) + 1;
        string text = "row_" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool Keep(int column) => (i + column) % 4 != 0;
        return new NullableGeneratedTypeMatrixRecord
        {
            BoolValue = Keep(0) ? seed % 2 == 0 : null,
            ByteValue = Keep(1) ? (byte)seed : null,
            SByteValue = Keep(2) ? (sbyte)-seed : null,
            ShortValue = Keep(3) ? (short)seed : null,
            UShortValue = Keep(4) ? (ushort)seed : null,
            IntValue = Keep(5) ? seed * 1000 : null,
            UIntValue = Keep(6) ? (uint)seed : null,
            LongValue = Keep(7) ? seed * 10_000_000_000L : null,
            ULongValue = Keep(8) ? (ulong)seed : null,
            FloatValue = Keep(9) ? seed + 0.5f : null,
            DoubleValue = i < 8 || !Keep(10) ? null : seed + 0.25,
            StringValue = Keep(11) ? text : null,
            BytesValue = Keep(12) ? new byte[] { (byte)seed, 0, 255 } : null,
            DateOnlyValue = Keep(13) ? new DateOnly(2024, 1, seed) : null,
            MemoryBytesValue = Keep(14) ? new byte[] { (byte)seed } : null,
            MemoryCharsValue = Keep(15) ? text.AsMemory() : null,
            IntervalValue = Keep(16) ? new Interval(seed, seed + 1, seed + 2) : null,
            DecimalValue = Keep(17) ? seed + 0.0001m : null,
            DateTimeValue = Keep(18)
                ? new DateTime(2024, 1, seed, 0, 0, 0, DateTimeKind.Utc)
                : null,
            TimeSpanValue = Keep(19) ? TimeSpan.FromSeconds(seed) : null,
            TimeOnlyValue = Keep(20) ? new TimeOnly(seed % 24, 0) : null,
            GuidValue = Keep(21) ? new Guid(seed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1) : null,
            EnumValue = Keep(22) ? TypeMatrixStatus.Closed : null,
        };
    }

    private static void AssertEquivalent<T>(IEnumerable<T> expected, IEnumerable<T> actual) =>
        ParquetCompatibilityOracle.AssertEquivalent(
            expected,
            actual,
            new CompatibilityComparisonOptions
            {
                TimestampPrecision = TimeSpan.FromMicroseconds(1),
                FloatingPointTolerance = 0,
            }
        );
}
