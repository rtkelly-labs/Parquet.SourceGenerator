using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static Parquet.SourceGenerator.Tests.Security.DecompressionGuardFooterTests;
using static Parquet.SourceGenerator.Tests.Security.DecompressionGuardTests;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// End-to-end pins for #374: a file whose footer lies about its own size is rejected on every read
/// path before Parquet.Net allocates for it. Parquet.Net's footer reader sizes a <c>List</c> from
/// the count in a list header before it reads an element, so a footer a few bytes long that claims
/// twenty million row groups used to cost 160 MB before any generated limit ran.
/// </summary>
[Collection(AllocationMeasurementSuite.Name)]
public sealed class DecompressionGuardFooterReadPathTests
{
    private const int ClaimedRowGroups = 20_000_000;

    /// <summary>A footer that is a handful of bytes and claims <see cref="ClaimedRowGroups"/> row groups.</summary>
    private static byte[] LyingFooterFile() =>
        FileWithFooter(Footer(ListHeader(ClaimedRowGroups, 12)));

    [Fact]
    public async Task ARowGroupCountTheFooterCannotHoldIsRejectedOnEveryReadPathAsync()
    {
        byte[] file = LyingFooterFile();

        var viaStream = await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new MemoryStream(file, writable: false)).ToArrayAsync()
        );
        viaStream.Message.ShouldContain("Parquet footer");

        await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new ReadOnlyMemory<byte>(file)).ToArrayAsync()
        );

        await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new ReadOnlyMemory<byte>(file)).Parallel().ToArrayAsync()
        );

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (
                var _ in MultiRowGroupModelParquet
                    .From(new MemoryStream(file, writable: false))
                    .AsAsyncEnumerable()
            ) { }
        });
    }

    [Fact]
    public async Task TheLyingFooterIsRejectedBeforeParquetNetAllocatesForItAsync()
    {
        byte[] file = LyingFooterFile();

        // Warm the path once so one-off costs are not counted.
        await RejectedAsync(file);
        AllocationMeasurementResult<bool> measured = await AllocationMeasurement.MeasureAsync(() =>
            RejectedAsync(file)
        );

        measured.Result.ShouldBeTrue();
        // Parquet.Net would have asked for a 160 MB list.
        measured.AllocatedBytes.ShouldBeLessThan(2 * 1024 * 1024);
    }

    private static async Task<bool> RejectedAsync(byte[] file)
    {
        try
        {
            await MultiRowGroupModelParquet
                .From(new MemoryStream(file, writable: false))
                .ToArrayAsync();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    [Fact]
    public async Task AFooterLengthTheFileCannotHoldIsRejectedAsInvalidDataAsync()
    {
        using var written = new MemoryStream();
        await new[]
        {
            new MultiRowGroupModel { Id = 1, Name = "one" },
        }.WriteParquetAsync(written);
        byte[] file = written.ToArray();
        BitConverter.GetBytes(int.MaxValue).CopyTo(file, file.Length - 8);

        var ex = await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet.From(new MemoryStream(file, writable: false)).ToArrayAsync()
        );
        ex.Message.ShouldContain("does not fit");
    }

    [Fact]
    public async Task MoreRowGroupsThanTheMaximumAreRejectedWhileTheFooterIsLocatedAsync()
    {
        var items = Enumerable
            .Range(0, 6)
            .Select(i => new MultiRowGroupModel { Id = i, Name = "item_" + i })
            .ToList();
        using var written = new MemoryStream();
        await items.WriteParquetBatchedAsync(
            written,
            new ParquetSerializerOptions { RowGroupSize = 2 }
        );

        var ex = await Should.ThrowAsync<InvalidDataException>(() =>
            MultiRowGroupModelParquet
                .From(new MemoryStream(written.ToArray(), writable: false))
                .WithOptions(new ParquetSerializerOptions { MaxRowGroupCount = 2 })
                .ToArrayAsync()
        );
        ex.Message.ShouldContain("Row group count 3 is invalid or exceeds maximum allowed 2");
    }
}
