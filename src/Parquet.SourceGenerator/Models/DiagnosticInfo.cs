using System;
using Microsoft.CodeAnalysis;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// Value-equatable model representing a Roslyn diagnostic for incremental pipeline caching.
/// </summary>
public readonly record struct DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    Location Location,
    string[] MessageArgs
) : IEquatable<DiagnosticInfo>
{
    /// <summary>
    /// Checks value equality against another <see cref="DiagnosticInfo"/>.
    /// </summary>
    public bool Equals(DiagnosticInfo other)
    {
        if (Descriptor.Id != other.Descriptor.Id)
            return false;
        if (!Equals(Location, other.Location))
            return false;
        if (MessageArgs.Length != other.MessageArgs.Length)
            return false;
        for (int i = 0; i < MessageArgs.Length; i++)
        {
            if (MessageArgs[i] != other.MessageArgs[i])
                return false;
        }
        return true;
    }

    /// <summary>
    /// Computes hash code for value caching.
    /// </summary>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = StringComparer.Ordinal.GetHashCode(Descriptor.Id);
            hash = (hash * 397) ^ (Location?.GetHashCode() ?? 0);
            hash = (hash * 397) ^ MessageArgs.Length;
            return hash;
        }
    }

    /// <summary>
    /// Creates a Roslyn <see cref="Diagnostic"/> instance for reporting.
    /// </summary>
    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location, MessageArgs);
}
