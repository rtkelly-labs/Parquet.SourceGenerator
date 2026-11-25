namespace Parquet.SourceGenerator.Legacy.Tests.Hostile;

[ParquetSerializable]
public sealed partial class HostileRow
{
    [ParquetColumn("id")]
    public int Id { get; set; }

    [ParquetColumn("name")]
    public string? Name { get; set; }
}

/// <summary>The columns of <see cref="HostileRow"/> plus a required one no such file carries.</summary>
[ParquetSerializable]
public sealed partial class RequiredExtraRow
{
    [ParquetColumn("id")]
    public int Id { get; set; }

    [ParquetColumn("name")]
    public string? Name { get; set; }

    [ParquetColumn("extra")]
    public long Extra { get; set; }
}

[ParquetSerializable]
public sealed partial class LabelRow
{
    [ParquetColumn("label")]
    public string? Label { get; set; }
}

[ParquetSerializable]
public sealed partial class WideNumericRow
{
    [ParquetColumn("a")]
    public long A { get; set; }

    [ParquetColumn("b")]
    public long B { get; set; }

    [ParquetColumn("c")]
    public double? C { get; set; }
}
