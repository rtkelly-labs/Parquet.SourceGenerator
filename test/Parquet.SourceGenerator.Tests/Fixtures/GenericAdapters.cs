using System;
using Parquet.SourceGenerator;
using Parquet.SourceGenerator.Tests;

// Generic adapters (docs/44 §A.3): one registration serves every construction of the source
// definition — through generic conversion methods (IdAdapter) or a generic adapter class
// (TaggedAdapter<T>) — and an exact closed registration specialises it (SpecialIdAdapter).
[assembly: ParquetTypeAdapter(typeof(IdAdapter))]
[assembly: ParquetTypeAdapter(typeof(TaggedAdapter<>))]
[assembly: ParquetTypeAdapter(typeof(SpecialIdAdapter))]

namespace Parquet.SourceGenerator.Tests;

/// <summary>A strongly-typed identifier: one domain type per entity, one adapter for all of them.</summary>
public readonly record struct Id<TEntity>(Guid Value);

[ParquetTypeAdapter(typeof(Id<>), typeof(Guid))]
public static class IdAdapter
{
    public static Guid ToStorage<TEntity>(this Id<TEntity> value) => value.Value;

    public static Id<TEntity> FromStorage<TEntity>(this Guid value) => new(value);
}

/// <summary>A generic wrapper whose surrogate is the same-arity generic group.</summary>
public sealed record Tagged<T>(T Value, string Tag);

public readonly record struct TaggedStorage<T>(T Value, string Tag);

[ParquetTypeAdapter(typeof(Tagged<>), typeof(TaggedStorage<>))]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "A generic adapter class is one of the two shapes the adapter contract accepts."
)]
public static class TaggedAdapter<T>
{
    public static TaggedStorage<T> ToStorage(Tagged<T> value) => new(value.Value, value.Tag);

    public static Tagged<T> FromStorage(TaggedStorage<T> value) => new(value.Value, value.Tag);
}

public sealed class SpecialEntity;

/// <summary>An exact registration for one construction wins over the generic default.</summary>
[ParquetTypeAdapter(typeof(Id<SpecialEntity>), typeof(string))]
public static class SpecialIdAdapter
{
    public static string ToStorage(Id<SpecialEntity> value) => value.Value.ToString("N");

    public static Id<SpecialEntity> FromStorage(string value) => new(Guid.ParseExact(value, "N"));
}
