using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public sealed partial class MissingLeafItemFull
{
    public int A { get; init; }

    public int? B { get; init; }
}

[ParquetSerializable]
public sealed partial record MissingLeafRowFull
{
    public int Id { get; init; }

    public List<MissingLeafItemFull> Items { get; init; } = new();
}

[ParquetSerializable]
public sealed partial class MissingLeafItemNarrow
{
    public int A { get; init; }
}

[ParquetSerializable]
public sealed partial record MissingLeafRowNarrow
{
    public int Id { get; init; }

    public List<MissingLeafItemNarrow> Items { get; init; } = new();
}

/// <summary>
/// A file may legally omit an optional column the reading model declares. Flat nullable columns
/// materialise nulls for it; a leaf under a list is outside that envelope (#367), and must fail with
/// the documented <see cref="InvalidDataException"/> rather than dereference chunk metadata for a
/// field the file does not contain.
/// </summary>
public sealed class MissingListLeafTests
{
    [Fact]
    public async Task OmittedOptionalLeafUnderAListIsRejectedWithInvalidDataAsync()
    {
        var narrow = new List<MissingLeafRowNarrow>
        {
            new()
            {
                Id = 1,
                Items = [new MissingLeafItemNarrow { A = 1 }, new MissingLeafItemNarrow { A = 2 }],
            },
        };
        using var stream = new MemoryStream();
        await narrow.WriteParquetAsync(stream);

        await Should.ThrowAsync<InvalidDataException>(() =>
            MissingLeafRowFullParquet.From(new MemoryStream(stream.ToArray())).ToArrayAsync()
        );
    }
}
