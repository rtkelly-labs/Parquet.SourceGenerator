---
description: "Install the package, annotate a model, and write and read your first file."
order: 10
---

# Getting Started

## Install

```bash
dotnet add package Parquet.SourceGenerator
dotnet add package Parquet.Net
```

`Parquet.SourceGenerator.Attributes` comes in automatically. You reference `Parquet.Net` yourself
because the generated code calls its columnar APIs directly. On Parquet.Net 4.x/5.x or .NET Framework,
use `Parquet.SourceGenerator.V5` instead. It supports fewer features, listed in
[Known Limitations](./limitations.md).

## Annotate a model

The type must be `partial`:

```csharp
using Parquet.SourceGenerator;

[ParquetSerializable]
public partial record UserEvent
{
    [ParquetColumn("event_id", Order = 1)]
    public Guid Id { get; init; }

    [ParquetColumn("username", Order = 2)]
    public string Username { get; init; } = string.Empty;

    [ParquetColumn("timestamp", Order = 3)]
    [ParquetTimestamp(ParquetTimestampUnit.Microseconds)]
    public DateTime Timestamp { get; init; }
}
```

## Write and read

```csharp
await using (var output = File.Create("events.parquet"))
{
    await events.WriteParquetAsync(output);
}

await using var input = File.OpenRead("events.parquet");
List<UserEvent> read = await UserEventParquet.From(input).ToListAsync();
```

If the model uses a shape the generator can't handle, the build fails with a `PARQ` diagnostic that
names the fix. The diagnostics are listed in [Compiler Diagnostics](../reference/diagnostics.md).

## Next

- [Attributes & Configuration](./attributes.md) covers column names, order, decimals, timestamps,
  sort keys and feature levels.
- [Reading & Writing](./reading-and-writing.md) covers batched and async writes, parallel and
  streaming reads, columnar batches, row-group pruning and options.
- [Native AOT](./native-aot.md) covers publishing with `PublishAot`.
