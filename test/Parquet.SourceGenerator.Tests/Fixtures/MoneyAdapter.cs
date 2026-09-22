using Parquet.SourceGenerator;
using Parquet.SourceGenerator.Tests;

// A project-level registration: the consuming assembly's own default adapter, which takes
// precedence over any referenced package's default for the same type (docs/44 §11).
[assembly: ParquetTypeAdapter(typeof(MoneyAdapter))]

namespace Parquet.SourceGenerator.Tests;

/// <summary>An application value object with no built-in Parquet mapping.</summary>
public sealed record Money(decimal Amount, string Currency);

/// <summary>Structural surrogate for <see cref="Money"/>: needs no [ParquetSerializable].</summary>
public readonly record struct MoneyStorage(decimal Amount, string Currency);

[ParquetTypeAdapter(typeof(Money), typeof(MoneyStorage))]
public static class MoneyAdapter
{
    public static MoneyStorage ToStorage(this Money value) => new(value.Amount, value.Currency);

    public static Money FromStorage(this MoneyStorage value) => new(value.Amount, value.Currency);
}

[ParquetSerializable]
public sealed partial record Invoice
{
    public Money Total { get; init; } = new(0m, "GBP");
    public Money? Refund { get; init; }
}
