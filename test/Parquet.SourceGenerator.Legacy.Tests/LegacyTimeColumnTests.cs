using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.SourceGenerator.Behaviour;
using Shouldly;
using Xunit;
using PhysicalType = Parquet.Meta.Type;

namespace Parquet.SourceGenerator.Legacy.Tests;

/// <summary>
/// The physical annotations the legacy backend writes for its time columns (#597). Until #597 the
/// legacy emitter emitted the Parquet.Net 6 time fields (<c>TimeDataField</c>,
/// <c>TimeUnitPrecision</c>) and, on every build but net8.0, <c>DateAndTimeMicros</c>, none of
/// which exist in Parquet.Net 4.25: a model with a <c>TimeSpan</c>, <c>TimeOnly</c> or
/// microsecond <c>DateTime</c> member did not compile. Microsecond timestamps stay net8.0-only
/// (a deliberate <c>#error</c> below it): 4.25's other builds cannot write them. The type matrix proves the values round-trip;
/// this pins the annotations another reader relies on.
/// </summary>
public sealed class LegacyTimeColumnTests
{
    [Fact]
    public async Task TimeColumnsCarryTheTimeAnnotationsOtherReadersExpect()
    {
        using var stream = new MemoryStream();
        await TypeMatrixData.BuildRows().WriteParquetAsync(stream);
        stream.Position = 0;

        using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        SchemaElement Column(string name) => reader.Metadata!.Schema.Single(e => e.Name == name);

        SchemaElement timespan = Column("timespan");
        timespan.Type.ShouldBe(PhysicalType.INT32);
        timespan.ConvertedType.ShouldBe(ConvertedType.TIME_MILLIS);

#if NET8_0_OR_GREATER
        SchemaElement micros = Column("datetime_micros");
        micros.Type.ShouldBe(PhysicalType.INT64);
        micros.ConvertedType.ShouldBe(ConvertedType.TIMESTAMP_MICROS);
        SchemaElement timeonly = Column("timeonly");
        timeonly.Type.ShouldBe(PhysicalType.INT64);
        timeonly.ConvertedType.ShouldBe(ConvertedType.TIME_MICROS);
#endif
    }
}
