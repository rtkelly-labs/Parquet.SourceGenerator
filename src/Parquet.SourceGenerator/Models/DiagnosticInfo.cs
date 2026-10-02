using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// A source position held as plain data, so a diagnostic can sit in a cached pipeline value
/// without holding a <see cref="Location"/> (and through it the <see cref="SyntaxTree"/>).
/// </summary>
internal readonly record struct SourcePosition(
    string FilePath,
    TextSpan Span,
    LinePositionSpan LineSpan
)
{
    /// <summary>
    /// Captures a source location, or <c>null</c> for <see cref="Location.None"/> and for
    /// locations that are not in source.
    /// </summary>
    public static SourcePosition? From(Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return null;
        }

        return new SourcePosition(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan,
            location.GetLineSpan().Span
        );
    }

    /// <summary>
    /// Rebuilds the Roslyn <see cref="Location"/> at report time.
    /// </summary>
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}

/// <summary>
/// Value-equatable model representing a Roslyn diagnostic for incremental pipeline caching.
/// </summary>
/// <remarks>
/// The position is data, not a <see cref="Location"/>: a source <c>Location</c> references its
/// <c>SyntaxTree</c>, so every model that produced a diagnostic pinned a syntax tree in the
/// generator cache for the lifetime of the driver (#398). The position still takes part in
/// equality, so an edit that moves a diagnosed member re-reports the diagnostic at its new line
/// instead of leaving a stale squiggle behind; models without a diagnostic are unaffected.
/// </remarks>
internal readonly record struct DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    SourcePosition? Position,
    EquatableArray<string> MessageArgs
)
{
    /// <summary>
    /// Creates a diagnostic from a Roslyn <see cref="Location"/>, which is reduced to plain data
    /// immediately and not retained.
    /// </summary>
    public DiagnosticInfo(DiagnosticDescriptor descriptor, Location? location, string[] messageArgs)
        : this(
            descriptor,
            SourcePosition.From(location),
            new EquatableArray<string>(messageArgs ?? Array.Empty<string>())
        ) { }

    /// <summary>
    /// Creates a Roslyn <see cref="Diagnostic"/> instance for reporting.
    /// </summary>
    public Diagnostic ToDiagnostic()
    {
        var args = new object?[MessageArgs.Length];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = MessageArgs[i];
        }

        return Diagnostic.Create(Descriptor, Position?.ToLocation() ?? Location.None, args);
    }
}
