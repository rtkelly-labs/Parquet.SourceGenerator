using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Emitter.Compound;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter;

/// <summary>
/// Sizes what one row of a model costs the read path's pooled buffers, for the
/// <c>MaxAllocationBytes</c> check emitted before each rental (#361). Kept apart from
/// <see cref="AllocationBudgetComponent"/> because it needs the emission plan, which the classic
/// backend does not link.
/// </summary>
internal static class ReadBudget
{
    /// <summary>
    /// The nominal bytes the buffers rented for one row occupy: each leaf's element, its definition
    /// level where it has one, a repetition level for a list leaf, and a second lane for text and
    /// binary columns (the batch reader wraps decoded values in a lane).
    /// </summary>
    public static int BytesPerRow(EmissionPlan plan)
    {
        int total = 0;
        foreach (LeafColumn col in plan.Columns)
        {
            total += AllocationBudgetComponent.ElementBytes(col.Leaf);
            if (col.Leaf.IsNullable || col.IsListLeaf || col.IsCompound)
            {
                total += 4;
            }

            if (col.IsListLeaf)
            {
                total += 4;
            }

            if (col.Leaf.Kind == PropertyKind.ByteArray || col.Leaf.TypeName.Contains("string"))
            {
                total += 16;
            }
        }

        return total;
    }
}
