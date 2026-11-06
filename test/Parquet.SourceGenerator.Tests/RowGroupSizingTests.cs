using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record SizingModel
{
    [ParquetColumn("id")]
    public int Id { get; init; }
}

/// <summary>
/// Covers how a row group size is chosen. <c>ParquetSerializerOptions.RowGroupSize</c> is the only
/// place it can be set — the duplicate <c>rowGroupSize</c> parameter was removed in #218 — so these
/// tests pin that the options value is honoured exactly as given, including the default value,
/// which an earlier resolution treated as a sentinel meaning "unset".
/// </summary>
public sealed class RowGroupSizingTests
{
    private static List<SizingModel> Rows(int count) =>
        Enumerable.Range(1, count).Select(i => new SizingModel { Id = i }).ToList();

    private static async Task<int> RowGroupCountAsync(
        ParquetSerializerOptions? options,
        int rowCount
    )
    {
        using var stream = new MemoryStream();
        await Rows(rowCount).WriteParquetBatchedAsync(stream, options);
        stream.Position = 0;

        // Parquet.Net v6's ParquetReader exposes DisposeAsync only — there is no sync Dispose.
        await using var reader = await global::Parquet.ParquetReader.CreateAsync(stream);
        return reader.RowGroupCount;
    }

    [Fact]
    public async Task OptionsSupplyTheRowGroupSize()
    {
        int groups = await RowGroupCountAsync(
            new ParquetSerializerOptions { RowGroupSize = 2 },
            rowCount: 6
        );

        groups.ShouldBe(3);
    }

    [Fact]
    public async Task OmittedOptionsFallBackToTheDefaultSize()
    {
        int groups = await RowGroupCountAsync(options: null, rowCount: 6);

        groups.ShouldBe(1);
    }

    [Fact]
    public async Task ExplicitDefaultSizedRowGroupIsHonouredRatherThanTreatedAsUnset()
    {
        // 50,000 used to double as the "unset" sentinel, so asking for it explicitly while another
        // source carried a different value quietly produced that other value instead.
        int groups = await RowGroupCountAsync(
            new ParquetSerializerOptions { RowGroupSize = 50_000 },
            rowCount: 6
        );

        groups.ShouldBe(1);
    }

    [Fact]
    public async Task NonPositiveRowGroupSizeIsRejected()
    {
        using var stream = new MemoryStream();

        ArgumentOutOfRangeException zero = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () =>
                Rows(2)
                    .WriteParquetBatchedAsync(
                        stream,
                        new ParquetSerializerOptions { RowGroupSize = 0 }
                    )
        );
        zero.ParamName.ShouldBe("options");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            Rows(2)
                .WriteParquetBatchedAsync(
                    stream,
                    new ParquetSerializerOptions { RowGroupSize = -10 }
                )
        );
    }

    [Fact]
    public void DefaultOptionsCannotBeMutatedForTheWholeProcess()
    {
        // Default was a shared singleton with settable properties, so one caller could rewrite the
        // defaults every other serializer in the process would pick up.
        ParquetSerializerOptions first = ParquetSerializerOptions.Default;
        first.RowGroupSize = 7;

        ParquetSerializerOptions.Default.RowGroupSize.ShouldBe(50_000);
        ParquetSerializerOptions.Default.ShouldNotBeSameAs(first);
    }
}
