using System;

namespace Parquet.SourceGenerator;

/// <summary>
/// Marks a property or field as a sorted key column for row-group pruning (issue #151, #584).
/// Key lookups and range queries are expressed through <c>Where(m =&gt; m.&lt;Key&gt;.MayContain(...))</c>.
/// </summary>
/// <remarks>
/// <para>
/// A marked member must be a flat, non-nullable root column of a totally ordered type whose
/// Parquet statistics order matches <c>Comparer&lt;T&gt;.Default</c>: the integral types,
/// <c>float</c>, <c>double</c>, or <see cref="DateTime"/>. Marking anything else is a compile-time
/// error (PARQ014) rather than a silent no-op. <c>string</c> is deliberately excluded — Parquet
/// orders <c>BYTE_ARRAY</c> statistics bytewise while <c>string.CompareTo</c> is culture-sensitive,
/// so the two orders can disagree and pruning could drop matching rows.
/// </para>
/// <para>
/// Marking a member changes nothing about the file that is written; pruning is a read-time
/// optimisation that reads the footer statistics the writer already emits. It pays off only when
/// the data is actually written in that column's order.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true
)]
public sealed class ParquetSortKeyAttribute : Attribute { }
