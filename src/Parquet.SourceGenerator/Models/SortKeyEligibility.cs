using System;
using System.Collections.Generic;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// Decides whether a root member can drive sorted row-group pruning (issue #151), and says why
/// not when it cannot.
/// <para>
/// The rules live here, beside the model, rather than in the emitter: both the parser — which
/// reports PARQ014 for a <c>[ParquetSortKey]</c> the rules reject — and the emitter — which only
/// emits lookups for members the rules accept — have to agree on them, and the parser is shared
/// with the classic (v4/v5) backend, which does not compile the pruning emitter at all.
/// </para>
/// </summary>
public static class SortKeyEligibility
{
    /// <summary>
    /// Primitive key types whose Parquet statistics round-trip to the identical CLR type and
    /// whose <c>Comparer&lt;T&gt;.Default</c> order matches the Parquet column order. String is
    /// deliberately absent: Parquet orders <c>BYTE_ARRAY</c> statistics by unsigned byte value
    /// while <c>string.CompareTo</c> is culture-sensitive, so the two orders can disagree.
    /// </summary>
    private static readonly HashSet<string> OrderedPrimitives = new(StringComparer.Ordinal)
    {
        "byte",
        "sbyte",
        "short",
        "ushort",
        "int",
        "uint",
        "long",
        "ulong",
        "float",
        "double",
    };

    /// <summary>
    /// True when <paramref name="property"/> is a flat, non-nullable root column of a totally
    /// ordered type. <paramref name="reason"/> carries the human-readable explanation otherwise,
    /// phrased to complete "cannot drive row-group pruning: ...".
    /// </summary>
    public static bool IsEligible(PropertyModel property, out string reason)
    {
        if (property is null)
            throw new ArgumentNullException(nameof(property));

        if (property.Kind is PropertyKind.Struct or PropertyKind.List or PropertyKind.Map)
        {
            reason =
                "it is a compound member (struct, list or map), and only a flat root column has row-group [Min, Max] statistics of its own";
            return false;
        }

        if (IsStringLike(property))
        {
            reason =
                "string columns are ordered bytewise by Parquet (BYTE_ARRAY statistics) while string.CompareTo is culture-sensitive, so the two orders can disagree and pruning could drop matching rows";
            return false;
        }

        if (property.IsNullable)
        {
            reason =
                "it is nullable, and Parquet statistics say nothing about where the nulls sit, so a binary search over [Min, Max] could skip rows that match";
            return false;
        }

        bool ordered =
            (
                property.Kind == PropertyKind.Primitive
                && OrderedPrimitives.Contains(property.TypeName)
            ) || (property.Kind == PropertyKind.DateTime && IsSystemDateTime(property.TypeName));

        if (!ordered)
        {
            reason =
                $"type '{property.TypeName}' has no Parquet statistics order that is guaranteed to match Comparer<T>.Default. Supported key types are the integral types, float, double and System.DateTime";
            return false;
        }

        reason = "";
        return true;
    }

    private static bool IsStringLike(PropertyModel property) =>
        property.Kind == PropertyKind.Primitive
        && (
            string.Equals(property.TypeName, "string", StringComparison.Ordinal)
            || string.Equals(property.TypeName, "global::System.String", StringComparison.Ordinal)
            || string.Equals(property.TypeName, "System.String", StringComparison.Ordinal)
        );

    private static bool IsSystemDateTime(string typeName) =>
        string.Equals(typeName, "global::System.DateTime", StringComparison.Ordinal)
        || string.Equals(typeName, "System.DateTime", StringComparison.Ordinal);
}
