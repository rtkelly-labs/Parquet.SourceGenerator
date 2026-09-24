---
description: "Attributes, nullability rules and feature levels."
order: 20
---

# Attributes & Configuration

Decorate a partial class, record or struct with `[ParquetSerializable]`; the generator discovers the
schema and emits the column readers and writers. The attribute source in
`src/Parquet.SourceGenerator.Attributes/` is authoritative — this page summarises it.

## 1. Attributes

| Attribute | Target | Effect |
|:---|:---|:---|
| `[ParquetSerializable]` | class, record, struct | Triggers generation. The type must be `partial` (`PARQ001`). |
| `[ParquetColumn]` | property, field | `Name` (defaults to the member name), `Order` (default `-1` = declaration order), `Deduplicate` (share identical strings within a row group), `Encoding` hint (`Default`, `Dictionary`, `DeltaBinaryPacked`, `ByteSplitStream`). `[ParquetColumn(Order = 2)]` reorders without renaming. |
| `[ParquetIgnore]` | property, field | Excludes the member from the schema. |
| `[ParquetDecimal(precision, scale)]` | `decimal` | Explicit precision and scale (`PARQ005` if invalid). |
| `[ParquetTimestamp(unit)]` | `DateTime` | `Milliseconds` or `Microseconds`. Parquet.Net has no nanosecond format. |
| `[ParquetSortKey]` | property, field | Opts a flat, non-nullable, totally ordered root column into sorted row-group pruning. Other types report `PARQ014`; `string` is excluded because Parquet orders it bytewise. |
| `[assembly: ParquetGeneratorOptions]` | assembly | Sets the feature level — see §4. |

Nullability follows nullable reference annotations where nullable analysis is enabled: `string` is a
required column, `string?` optional. In an oblivious context reference types stay optional.

Which member types and shapes are accepted: [Compatibility Matrix](../reference/compatibility.md)
and [Coverage envelope map (#259)](../reference/coverage-map.md). What is rejected, and why:
[Compiler Diagnostics](../reference/diagnostics.md).

## 2. Example

```csharp
using Parquet.SourceGenerator;

[ParquetSerializable]
public partial record TransactionLog
{
    [ParquetColumn("tx_id", Order = 1)]
    public Guid Id { get; init; }

    [ParquetColumn("user_id", Order = 2)]
    public string UserId { get; init; } = string.Empty;

    [ParquetColumn("amount", Order = 3)]
    [ParquetDecimal(18, 2)]
    public decimal Amount { get; init; }

    [ParquetColumn("timestamp", Order = 4)]
    public DateTime Timestamp { get; init; }

    [ParquetIgnore]
    public string LocalSessionCache { get; init; } = string.Empty;
}

await logs.WriteParquetAsync(stream);                                   // write
List<TransactionLog> read = await TransactionLogParquet.From(stream).ToListAsync(); // read builder
```

How to use the generated methods is covered in [Reading & Writing](./reading-and-writing.md). The
design of the full surface is in [Public API Surface](../reference/api-surface.md).

## 3. What gets generated

Don't rely on a hand-written sample here; read the golden files, which CI keeps exact:

- `test/Parquet.SourceGenerator.Tests/GoldenFiles/*.g.cs` — emitted source per model
  (`OrderEventParquetExtensions.g.cs` is the simplest flat case,
  `LegacyRecordParquetLegacyExtensions.g.cs` the classic backend).
- `*.api.txt` beside each — the signature-only public surface
  ([Generated API Baselines](../quality/api-baselines.md)).

## 4. Feature levels

A small named compatibility policy rather than independent boolean switches:

| Level | Meaning |
|:---|:---|
| `Level1Flat` | Flat models and the compatibility-safe surface. |
| `Level2CompoundPreview` | **Default.** Enables the supported compound preview shapes. |
| `Level3ModernCSharp` | Opts into the latest modern generator shapes. |

```xml
<PropertyGroup>
  <ParquetGeneratorFeatureLevel>Level2CompoundPreview</ParquetGeneratorFeatureLevel>
</PropertyGroup>
```

```csharp
// When MSBuild cannot be changed:
[assembly: ParquetGeneratorOptions(FeatureLevel = ParquetGeneratorFeatureLevel.Level3ModernCSharp)]
```

MSBuild takes precedence over the assembly attribute. An invalid value reports `PARQ015`. Every
generated file records the selected level and generator version in its header, so output can be
audited without reflection. Named profiles and per-type overrides are deferred
([DECISIONS #225](../design/decisions.md#225--feature-profiles-and-per-type-overrides)).
