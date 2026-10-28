using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Triggers compile-time Parquet schema discovery, column serializer, and deserializer generation.
/// </summary>
/// <remarks>
/// There is deliberately no <c>SchemaName</c> here. One existed, was parsed, was carried on the
/// target model — and was never read by the emitter, because Parquet.Net's <c>ParquetSchema</c> has
/// no name to set: both its constructors take fields and nothing else. It was removed rather than
/// left as a public property that silently does nothing.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = false,
    Inherited = true
)]
public sealed class ParquetSerializableAttribute : Attribute { }
