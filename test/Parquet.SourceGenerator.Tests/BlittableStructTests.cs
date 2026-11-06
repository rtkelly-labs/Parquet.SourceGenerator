using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public readonly partial record struct SingleLongStruct
{
    [ParquetColumn("id")]
    public long Value { get; init; }
}

[ParquetSerializable]
public partial struct SingleDoubleStruct
{
    [ParquetColumn("val")]
    public double Value { get; set; }
}

[ParquetSerializable]
public partial struct Point3DStruct
{
    [ParquetColumn("x")]
    public double X { get; set; }

    [ParquetColumn("y")]
    public double Y { get; set; }

    [ParquetColumn("z")]
    public double Z { get; set; }
}

public sealed class BlittableStructTests
{
    [Fact]
    public async Task SingleLongStructListAndArrayRoundtrip()
    {
        const int count = 10_000;
        var items = new List<SingleLongStruct>(count);
        for (int i = 0; i < count; i++)
        {
            items.Add(new SingleLongStruct { Value = i * 42L });
        }

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        // Read into List
        ms.Position = 0;
        var readList = await SingleLongStructParquetExtensions.ReadParquetAsync(ms);
        readList.Count.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            readList[i].Value.ShouldBe(i * 42L);
        }

        // Read into Array
        ms.Position = 0;
        var readArray = await SingleLongStructParquetExtensions.ReadParquetArrayAsync(ms);
        readArray.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            readArray[i].Value.ShouldBe(i * 42L);
        }

        // Write from Array and Parallel Read
        using var ms2 = new MemoryStream();
        await readArray.WriteParquetAsync(ms2);
        var bytes = ms2.ToArray();

        var parallelArray = await SingleLongStructParquetExtensions.ReadParquetParallelArrayAsync(
            bytes
        );
        parallelArray.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            parallelArray[i].Value.ShouldBe(i * 42L);
        }

        var parallelList = await SingleLongStructParquetExtensions.ReadParquetParallelAsync(bytes);
        parallelList.Count.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            parallelList[i].Value.ShouldBe(i * 42L);
        }
    }

    [Fact]
    public async Task SingleDoubleStructRoundtrip()
    {
        const int count = 5_000;
        var array = new SingleDoubleStruct[count];
        for (int i = 0; i < count; i++)
        {
            array[i] = new SingleDoubleStruct { Value = i * 3.14159 };
        }

        using var ms = new MemoryStream();
        await array.WriteParquetAsync(ms);

        ms.Position = 0;
        var readArray = await SingleDoubleStructParquetExtensions.ReadParquetArrayAsync(ms);
        readArray.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            readArray[i].Value.ShouldBe(i * 3.14159, 0.00001);
        }

        // Parallel read
        var parallelArray = await SingleDoubleStructParquetExtensions.ReadParquetParallelArrayAsync(
            ms.ToArray()
        );
        parallelArray.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            parallelArray[i].Value.ShouldBe(i * 3.14159, 0.00001);
        }
    }

    [Fact]
    public async Task MultiFieldStructPoint3DRoundtrip()
    {
        const int count = 1_000;
        var items = new List<Point3DStruct>(count);
        for (int i = 0; i < count; i++)
        {
            items.Add(
                new Point3DStruct
                {
                    X = i * 1.1,
                    Y = i * 2.2,
                    Z = i * 3.3,
                }
            );
        }

        using var ms = new MemoryStream();
        await items.WriteParquetAsync(ms);

        ms.Position = 0;
        var readArray = await Point3DStructParquetExtensions.ReadParquetArrayAsync(ms);
        readArray.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            readArray[i].X.ShouldBe(i * 1.1, 0.00001);
            readArray[i].Y.ShouldBe(i * 2.2, 0.00001);
            readArray[i].Z.ShouldBe(i * 3.3, 0.00001);
        }

        var bytes = ms.ToArray();
        var parallelRead = await Point3DStructParquetExtensions.ReadParquetParallelArrayAsync(
            bytes
        );
        parallelRead.Length.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            parallelRead[i].X.ShouldBe(i * 1.1, 0.00001);
            parallelRead[i].Y.ShouldBe(i * 2.2, 0.00001);
            parallelRead[i].Z.ShouldBe(i * 3.3, 0.00001);
        }
    }
}
