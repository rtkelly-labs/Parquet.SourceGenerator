using System;

namespace Parquet.SourceGenerator.Parser;

/// <summary>
/// Which compound member kinds the consuming emitter can express today.
/// </summary>
/// <remarks>
/// The parser learns whole trees at once (a struct child may hold a list, which may hold a
/// map), but emission lands kind by kind across the milestones of #176: structs in M2,
/// lists/arrays in M3, maps in M4, and the classic backend last in M5. Until the pipeline
/// supports a kind, members of that kind are rejected at compile time by PARQ006 exactly as
/// they were before #176 — this enum is the single dial the two generators pass to say how
/// far their emitter has got, and each milestone widens it. Unsupported-on-purpose rather
/// than unsupported-by-accident: a model the emitter cannot render must fail the build,
/// never emit code that compiles and writes the wrong bytes.
/// </remarks>
[Flags]
public enum CompoundKinds
{
    /// <summary>No compound members. Every compound member is rejected with PARQ006.</summary>
    None = 0,

    /// <summary><c>StructField</c> emission is available (nested <c>[ParquetSerializable]</c> members).</summary>
    Struct = 1,

    /// <summary><c>ListField</c> emission is available (<c>List&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>-shaped, arrays).</summary>
    List = 2,

    /// <summary><c>MapField</c> emission is available (<c>Dictionary&lt;string, T&gt;</c>).</summary>
    Map = 4,
}
