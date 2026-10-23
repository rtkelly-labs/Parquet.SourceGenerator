using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Behaviour;

/// <summary>
/// The type matrix: every supported flat type, required and optional, through the cells
/// {multi-row-group, empty, all-null row group, missing optional column}. Runs against whichever
/// backend the compiling project's <see cref="BehaviourDriver"/> targets.
/// </summary>
public sealed class TypeMatrixBehaviourTests
{
    private static readonly Lazy<Task<RoundTrip<TypeMatrixRow>>> MultiRowGroup = new(() =>
        RoundTripAsync(TypeMatrixData.BuildRows())
    );

    private static readonly Lazy<Task<TypeMatrixRow[]>> MissingOptionalColumns = new(
        ReadRequiredOnlyFileAsWideModelAsync
    );

    public static TheoryData<string> AllColumns => ColumnNames(_ => true);

    public static TheoryData<string> RequiredColumns => ColumnNames(c => !c.Optional);

    public static TheoryData<string> OptionalColumns => ColumnNames(c => c.Optional);

    [Theory]
    [MemberData(nameof(AllColumns))]
    public async Task ColumnRoundTripsAcrossRowGroups(string column)
    {
        RoundTrip<TypeMatrixRow> roundTrip = await MultiRowGroup.Value;

        AssertColumn(column, roundTrip.Written, roundTrip.Read, "multi-row-group");
    }

    [Theory]
    [MemberData(nameof(OptionalColumns))]
    public async Task OptionalColumnReadsNullThroughAnAllNullRowGroup(string column)
    {
        RoundTrip<TypeMatrixRow> roundTrip = await MultiRowGroup.Value;
        MatrixColumn matrixColumn = TypeMatrixData.Column(column);

        // The first row group holds nothing but nulls in every optional column; the groups after
        // it mix nulls and values, so a reader that carried state across groups would show here.
        for (int i = 0; i < TypeMatrixData.RowGroupSize; i++)
        {
            matrixColumn
                .FromRow(roundTrip.Read[i])
                .ShouldBeNull($"{BehaviourDriver.Backend()}: '{column}' row {i}");
        }

        roundTrip
            .Read.Skip(TypeMatrixData.RowGroupSize)
            .Count(r => matrixColumn.FromRow(r) is not null)
            .ShouldBeGreaterThan(0, $"'{column}' must carry values after the all-null group");
    }

    [Fact]
    public async Task TheMatrixFileHasSeveralRowGroups()
    {
        RoundTrip<TypeMatrixRow> roundTrip = await MultiRowGroup.Value;
        FooterSummary footer = await FooterSummary.ReadAsync(roundTrip.Bytes);

        footer.RowGroupRows.ShouldBe(
            new long[] { 4, 4, 4, 4, 4, 2 },
            $"{BehaviourDriver.Backend()}: RowGroupSize {TypeMatrixData.RowGroupSize}"
        );
        roundTrip.Read.Length.ShouldBe(TypeMatrixData.RowCount);
    }

    [Fact]
    public async Task EmptyInputRoundTripsToNoRows()
    {
        RoundTrip<TypeMatrixRow> roundTrip = await RoundTripAsync(new List<TypeMatrixRow>());

        roundTrip.Read.ShouldBeEmpty(BehaviourDriver.Backend());
        FooterSummary footer = await FooterSummary.ReadAsync(roundTrip.Bytes);
        footer.RowGroupRows.Sum().ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(OptionalColumns))]
    public async Task MissingOptionalColumnReadsAsNull(string column)
    {
        TypeMatrixRow[] read = await MissingOptionalColumns.Value;
        MatrixColumn matrixColumn = TypeMatrixData.Column(column);

        read.Length.ShouldBe(TypeMatrixData.RowCount);
        for (int i = 0; i < read.Length; i++)
        {
            matrixColumn
                .FromRow(read[i])
                .ShouldBeNull($"{BehaviourDriver.Backend()}: missing '{column}' row {i}");
        }
    }

    [Theory]
    [MemberData(nameof(RequiredColumns))]
    public async Task RequiredColumnReadsBesideMissingOptionalColumns(string column)
    {
        TypeMatrixRow[] read = await MissingOptionalColumns.Value;

        AssertColumn(column, TypeMatrixData.BuildRows(), read, "missing optional columns");
    }

    [Theory]
    [MemberData(nameof(RequiredColumns))]
    public async Task NarrowerModelReadsItsColumnsOutOfTheWideFile(string column)
    {
        RoundTrip<TypeMatrixRow> roundTrip = await MultiRowGroup.Value;
        MatrixColumn matrixColumn = TypeMatrixData.Column(column);

        using var stream = new MemoryStream(roundTrip.Bytes, writable: false);
        TypeMatrixRequiredOnly[] narrow = await BehaviourDriver.ReadTypeMatrixRequiredOnlyAsync(
            stream,
            null
        );

        narrow.Length.ShouldBe(roundTrip.Written.Count);
        for (int i = 0; i < narrow.Length; i++)
        {
            object? expected = matrixColumn.FromRow(roundTrip.Written[i]);
            object? actual = matrixColumn.FromRequiredOnly!(narrow[i]);
            TypeMatrixData
                .ValuesEqual(expected, actual)
                .ShouldBeTrue(Mismatch(column, i, expected, actual, "projection"));
        }
    }

    /// <summary>
    /// A zero-length binary value, required and optional, beside a null and a non-empty value.
    /// Listed as a known failure on the legacy backend: Parquet.Net 4.25 writes the file but its
    /// reader throws <c>IndexOutOfRangeException</c> decoding it.
    /// </summary>
    [Fact]
    public async Task EmptyBinaryValueRoundTrips()
    {
        List<TypeMatrixRow> rows = TypeMatrixData.BuildRows();
        rows[1].Bytes = [];
        rows[TypeMatrixData.RowGroupSize + 1].BytesOpt = [];

        Task<RoundTrip<TypeMatrixRow>> roundTrip = RoundTripAsync(rows);

        if (BehaviourDriver.IsKnownFailure(KnownFailures.EmptyBinaryValue))
        {
            await Should.ThrowAsync<Exception>(
                roundTrip,
                $"{BehaviourDriver.Backend()} lists '{KnownFailures.EmptyBinaryValue}' as a known "
                    + "failure but the round trip now succeeds: remove it from the driver's list."
            );
            return;
        }

        RoundTrip<TypeMatrixRow> result = await roundTrip;
        AssertColumn("bytes", rows, result.Read, "empty binary value");
        AssertColumn("bytes_opt", rows, result.Read, "empty binary value");
    }

    internal static void AssertColumn(
        string column,
        IReadOnlyList<TypeMatrixRow> expected,
        IReadOnlyList<TypeMatrixRow> actual,
        string cell
    )
    {
        MatrixColumn matrixColumn = TypeMatrixData.Column(column);
        actual.Count.ShouldBe(expected.Count, $"{BehaviourDriver.Backend()}: {cell} row count");
        for (int i = 0; i < expected.Count; i++)
        {
            object? want = matrixColumn.FromRow(expected[i]);
            object? got = matrixColumn.FromRow(actual[i]);
            TypeMatrixData
                .ValuesEqual(want, got)
                .ShouldBeTrue(Mismatch(column, i, want, got, cell));
        }
    }

    private static string Mismatch(
        string column,
        int row,
        object? expected,
        object? actual,
        string cell
    ) =>
        $"{BehaviourDriver.Backend()} ({cell}): column '{column}' row {row} read back as "
        + $"{TypeMatrixData.Describe(actual)}, wrote {TypeMatrixData.Describe(expected)}.";

    private static TheoryData<string> ColumnNames(Func<MatrixColumn, bool> predicate)
    {
        var data = new TheoryData<string>();
        foreach (MatrixColumn column in TypeMatrixData.Columns.Where(predicate))
        {
            data.Add(column.Name);
        }

        return data;
    }

    private static async Task<RoundTrip<TypeMatrixRow>> RoundTripAsync(List<TypeMatrixRow> rows)
    {
        var options = new ParquetSerializerOptions { RowGroupSize = TypeMatrixData.RowGroupSize };
        using var stream = new MemoryStream();
        await BehaviourDriver.WriteAsync(rows, stream, options);
        byte[] bytes = stream.ToArray();

        using var input = new MemoryStream(bytes, writable: false);
        TypeMatrixRow[] read = await BehaviourDriver.ReadTypeMatrixAsync(input, null);
        return new RoundTrip<TypeMatrixRow>(rows, bytes, read);
    }

    private static async Task<TypeMatrixRow[]> ReadRequiredOnlyFileAsWideModelAsync()
    {
        using var stream = new MemoryStream();
        await BehaviourDriver.WriteAsync(
            TypeMatrixData.BuildRequiredOnlyRows(),
            stream,
            new ParquetSerializerOptions { RowGroupSize = TypeMatrixData.RowGroupSize }
        );

        using var input = new MemoryStream(stream.ToArray(), writable: false);
        return await BehaviourDriver.ReadTypeMatrixAsync(input, null);
    }
}

/// <summary>What was written, the file it produced and what read back.</summary>
public sealed class RoundTrip<T>
{
    public RoundTrip(IReadOnlyList<T> written, byte[] bytes, T[] read)
    {
        Written = written;
        Bytes = bytes;
        Read = read;
    }

    public IReadOnlyList<T> Written { get; }

    public byte[] Bytes { get; }

    public T[] Read { get; }
}
