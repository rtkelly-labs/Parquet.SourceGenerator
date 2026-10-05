# Installation & Quickstart

`Parquet.SourceGenerator` provides compile-time, zero-reflection serialization and deserialization for [Parquet.Net](https://github.com/aloneguid/parquet-dotnet).

---

## 1. Installation

Install the package via the .NET CLI or Package Manager:

### Modern .NET (.NET 8.0, .NET 9.0+)
For projects targeting modern .NET with Parquet.Net 6+:

```bash
dotnet add package Parquet.SourceGenerator
```

### Legacy & .NET Framework (.NET Framework 4.7.2+, .NET Standard 2.0)
For projects targeting older frameworks or Parquet.Net 4.x/5.x:

```bash
dotnet add package Parquet.SourceGenerator.Legacy
```

---

## 2. Quickstart

### Step 1: Define and Annotate Your Model

Decorate your C# class, record, or struct with `[ParquetSerializable]`:

```csharp
using System;
using Parquet.SourceGenerator;

[ParquetSerializable]
public sealed record Person(
    Guid Id,
    string Name,
    int Age,
    decimal Salary,
    DateTime CreatedAtUtc
);
```

> 💡 **Tip:** Properties are mapped automatically to Parquet columns matching their names. Use `[ParquetColumn("custom_name")]` to customize column names or physical storage encodings.

### Step 2: Write Parquet Data

The source generator emits strongly-typed extension methods on `IEnumerable<T>` and collections:

```csharp
using System.IO;

var people = new List<Person>
{
    new(Guid.NewGuid(), "Alice", 30, 95000.50m, DateTime.UtcNow),
    new(Guid.NewGuid(), "Bob", 38, 110000.00m, DateTime.UtcNow),
};

using var stream = File.Create("people.parquet");
await people.WriteParquetAsync(stream);
```

### Step 3: Read Parquet Data

Read back strongly-typed rows using the generated fluent reader:

```csharp
using var stream = File.OpenRead("people.parquet");

// Read entire file into an array
Person[] rows = await PersonParquet.From(stream).ToArrayAsync();

// Or stream asynchronously row by row (memory-bounded)
await foreach (Person person in PersonParquet.From(stream).AsAsyncEnumerable())
{
    Console.WriteLine($"{person.Name} ({person.Age})");
}
```

---

## 3. Next Steps

- **[Model Attributes Guide](../guides/model-attributes.md)**: Annotate custom decimals, timestamps, nullability, and ignored fields.
- **[Reading Parquet Guide](../guides/reading-parquet.md)**: Explore pushdown filtering, column selection, and borrowed batch iteration (`AsBatches()`).
- **[Configuration](../getting-started/configuration.md)**: Configure generator feature levels and MSBuild settings.
- **[Native AOT Guide](../guides/native-aot.md)**: Compile ahead-of-time with zero reflection and trim warnings.
