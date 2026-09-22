using System.Linq;
using System.Text;
using Parquet.SourceGenerator.Emitter.Components;
using Parquet.SourceGenerator.Models;

namespace Parquet.SourceGenerator.Emitter.Compound;

/// <summary>
/// Compound write extraction and read reconstruction for the v6 emitter (issue #176 M2).
/// The definition-ladder conventions implemented here are the ones verified against
/// Parquet.Net 6.1.0 and PyArrow in docs/15 §1-2. v6-only by design; the classic backend's
/// equivalent lands in M5 on the 4.x conventions (docs/15 §3).
/// </summary>
internal static class CompoundMapping
{
    /// <summary>
    /// Write conversion for a compound-path leaf whose value local is known non-null after
    /// the ladder's null test: the non-nullable conversion forms, since nulls are carried
    /// by definition levels rather than the buffer.
    /// </summary>
    public static string GetCompoundLeafWriteExpression(PropertyModel leaf, string valueExpr)
    {
        string baseExpr =
            leaf.IsNullable && ValueNullableWrapper(leaf) ? $"{valueExpr}.Value" : valueExpr;
        return PropertyMappingComponent.GetWriteExpression(
            leaf with
            {
                IsNullable = false,
            },
            baseExpr
        );
    }

    /// <summary>
    /// Read conversion for a compound-path leaf: <paramref name="valueExpr"/> is a packed,
    /// definitely-present value read from the lane buffer.
    /// </summary>
    public static string GetCompoundLeafReadExpression(PropertyModel leaf, string valueExpr)
    {
        if (leaf.Kind == PropertyKind.Primitive && leaf.TypeName.Contains("string"))
            return $"{valueExpr}.ToString()";

        if (leaf.Kind == PropertyKind.ByteArray)
            return $"{valueExpr}.ToArray()";

        return PropertyMappingComponent.GetReadExpression(
            leaf with
            {
                IsNullable = false,
            },
            valueExpr
        );
    }

    /// <summary>
    /// Emits the ladder walk for one compound leaf inside an extraction loop: each ancestor is
    /// read into a local and null-tested before anything below it is dereferenced.
    /// </summary>
    public static void EmitCompoundExtraction(
        StringBuilder builder,
        LeafColumn col,
        string itemExpr,
        string indexVar,
        string prefix
    ) => EmitLadder(builder, col, itemExpr, level: 0, indexVar, prefix);

    private static void EmitLadder(
        StringBuilder builder,
        LeafColumn col,
        string curExpr,
        int level,
        string indexVar,
        string prefix
    )
    {
        int ancestors = col.MemberChain.Length - 1;

        if (level == ancestors)
        {
            // Leaf rung.
            string valueLocal = $"cv_{col.Slot}";
            string leafMember = col.MemberChain[col.MemberChain.Length - 1];
            PropertyModel leaf = col.Leaf;
            builder.AppendLine(
                $"{prefix}var {valueLocal} = {ReadConverted(leaf, $"{curExpr}.{leafMember}", $"ad_{col.Slot}")};"
            );

            string defWrite = $"defLevels_{col.Slot}[{indexVar}]";
            string writeExpr = GetCompoundLeafWriteExpression(leaf, valueLocal);

            if (leaf.IsNullable)
            {
                builder.AppendLine($"{prefix}if ({valueLocal} is not null)");
                builder.AppendLine($"{prefix}{{");
                builder.AppendLine(
                    $"{prefix}    buffer_{col.Slot}[nonNullCount_{col.Slot}++] = {writeExpr};"
                );
                builder.AppendLine($"{prefix}    {defWrite} = {col.MaxDef};");
                builder.AppendLine($"{prefix}}}");
                builder.AppendLine($"{prefix}else {{");
                builder.AppendLine($"{prefix}    {defWrite} = {col.MaxDef - 1};");
                builder.AppendLine($"{prefix}}}");
            }
            else
            {
                builder.AppendLine(
                    $"{prefix}buffer_{col.Slot}[nonNullCount_{col.Slot}++] = {writeExpr};"
                );
                builder.AppendLine($"{prefix}{defWrite} = {col.MaxDef};");
            }
            return;
        }

        string local = $"ca_{col.Slot}_{level}";
        string member = col.MemberChain[level];
        string access = $"{curExpr}.{member}";
        if (level < col.StepAdapters.Length && col.StepAdapters[level] is { } stepAdapter)
        {
            // An adapted group member: the ladder walks its surrogate (docs/44 §A.4c).
            access = stepAdapter.ToStorageFrom(
                access,
                col.StepDomainNullable[level],
                $"ad_{col.Slot}_{level}"
            );
        }
        builder.AppendLine($"{prefix}var {local} = {access};");

        if (!col.StepHasNullTest[level])
        {
            // A required C# struct member can never be null: its rung is always present.
            EmitLadder(builder, col, local, level + 1, indexVar, prefix);
            return;
        }

        string defWriteOuter = $"defLevels_{col.Slot}[{indexVar}]";
        builder.AppendLine($"{prefix}if ({local} is not null)");
        builder.AppendLine($"{prefix}{{");
        // A Nullable<T> local never flow-sugars into member access; unwrap explicitly.
        string bodyExpr = col.StepValueUnwrap[level] ? $"{local}.Value" : local;
        EmitLadder(builder, col, bodyExpr, level + 1, indexVar, prefix + "    ");
        builder.AppendLine($"{prefix}}}");
        builder.AppendLine($"{prefix}else {{");
        builder.AppendLine($"{prefix}    {defWriteOuter} = {col.StepDefBase[level]};");
        builder.AppendLine($"{prefix}}}");
    }

    private static void EmitListElementField(
        StringBuilder builder,
        LeafColumn col,
        string element,
        string p
    )
    {
        int n = col.Slot;
        string cv = $"cv_{n}";
        builder.AppendLine(
            $"{p}var {cv} = {ReadConverted(col.Leaf, $"{element}.{col.Leaf.Name}", $"ad_{n}")};"
        );
        string conv = GetCompoundLeafWriteExpression(col.Leaf, cv);
        if (col.Leaf.IsNullable)
        {
            builder.AppendLine($"{p}if ({cv} is not null) {{");
            GrowValues(builder, col, p + "    ");
            builder.AppendLine($"{p}    buffer_{n}[nonNullCount_{n}++] = {conv};");
            builder.AppendLine($"{p}    defLevels_{n}[posCount_{n}] = {col.MaxDef};");
            CloseWithAbsentLevel(builder, p, n, col.MaxDef - 1);
        }
        else
        {
            GrowValues(builder, col, p);
            builder.AppendLine($"{p}buffer_{n}[nonNullCount_{n}++] = {conv};");
            builder.AppendLine($"{p}defLevels_{n}[posCount_{n}] = {col.MaxDef};");
        }
    }

    /// <summary>
    /// List&lt;POCO&gt; element write: the element slot gets its own rung (def ==
    /// ListElementRung means a null element); below it the field ladder walks with bases offset
    /// by two. A non-nullable value-type element has no null state, so it skips the test; an
    /// adapted element is converted to its group surrogate first (docs/44 §A.4).
    /// </summary>
    private static void EmitStructElementExtraction(StringBuilder builder, LeafColumn col, string p)
    {
        int n = col.Slot;
        PropertyModel element = col.ListElementStruct!;
        InlineAdapterModel? adapter = element.InlineAdapter;
        bool domainIsValueType = adapter?.DomainIsValueType ?? element.CompoundIsValueType;
        bool nullTest = !domainIsValueType || element.IsNullable;
        string value = domainIsValueType && element.IsNullable ? $"el_{n}.Value" : $"el_{n}";
        string storage = adapter is null ? value : adapter.ToStorage(value);

        // An unadapted reference element is read in place; anything converted or unwrapped gets
        // one local so the conversion runs once per element for this column.
        bool alias = storage != $"el_{n}";
        string source = alias ? $"ev_{n}" : $"el_{n}";

        if (!nullTest)
        {
            if (alias)
                builder.AppendLine($"{p}var ev_{n} = {storage};");
            EmitListElementColumn(builder, col, source, p);
            return;
        }

        builder.AppendLine($"{p}if (el_{n} is not null) {{");
        if (alias)
            builder.AppendLine($"{p}    var ev_{n} = {storage};");
        EmitListElementColumn(builder, col, source, p + "    ");
        CloseWithAbsentLevel(builder, p, n, col.ListElementRung);
    }

    /// <summary>
    /// One element column: a direct element member, or a leaf of a group one level inside the
    /// element, reached through that group's null test (and inline adapter, docs/44 §A.4c).
    /// </summary>
    private static void EmitListElementColumn(
        StringBuilder builder,
        LeafColumn col,
        string element,
        string p
    )
    {
        if (col.SchemaPath.Length == 1)
        {
            EmitListElementField(builder, col, element, p);
            return;
        }

        int n = col.Slot;
        string access = $"{element}.{col.MemberChain[1]}";
        if (col.StepAdapters[2] is { } groupAdapter)
            access = groupAdapter.ToStorageFrom(access, col.StepDomainNullable[2], $"ag_{n}");
        builder.AppendLine($"{p}var ga_{n} = {access};");

        if (!col.StepHasNullTest[2])
        {
            EmitListElementField(builder, col, $"ga_{n}", p);
            return;
        }

        builder.AppendLine($"{p}if (ga_{n} is not null) {{");
        string group = col.StepValueUnwrap[2] ? $"ga_{n}.Value" : $"ga_{n}";
        EmitListElementField(builder, col, group, p + "    ");
        CloseWithAbsentLevel(builder, p, n, col.StepDefBase[2]);
    }

    /// <summary>
    /// Closes an open <c>if (… is not null) {</c> presence test with the branch that records the
    /// absent definition level instead.
    /// </summary>
    private static void CloseWithAbsentLevel(StringBuilder builder, string p, int n, int level)
    {
        builder.AppendLine($"{p}}}");
        builder.AppendLine($"{p}else {{");
        builder.AppendLine($"{p}    defLevels_{n}[posCount_{n}] = {level};");
        builder.AppendLine($"{p}}}");
    }

    private static void GrowLevels(StringBuilder b, int n, string prefix)
    {
        string P = prefix;
        b.AppendLine($"{P}if (posCount_{n} == defLevels_{n}.Length)");
        b.AppendLine($"{P}{{");
        b.AppendLine(
            $"{P}    var gd_{n} = global::System.Buffers.ArrayPool<int>.Shared.Rent(defLevels_{n}.Length * 2);"
        );
        b.AppendLine(
            $"{P}    global::System.Array.Copy(defLevels_{n}, gd_{n}, defLevels_{n}.Length);"
        );
        b.AppendLine(
            $"{P}    global::System.Buffers.ArrayPool<int>.Shared.Return(defLevels_{n}, clearArray: false);"
        );
        b.AppendLine($"{P}    defLevels_{n} = gd_{n};");
        b.AppendLine(
            $"{P}    var gr_{n} = global::System.Buffers.ArrayPool<int>.Shared.Rent(repLevels_{n}.Length * 2);"
        );
        b.AppendLine(
            $"{P}    global::System.Array.Copy(repLevels_{n}, gr_{n}, repLevels_{n}.Length);"
        );
        b.AppendLine(
            $"{P}    global::System.Buffers.ArrayPool<int>.Shared.Return(repLevels_{n}, clearArray: false);"
        );
        b.AppendLine($"{P}    repLevels_{n} = gr_{n};");
        b.AppendLine($"{P}}}");
    }

    private static void GrowValues(StringBuilder b, LeafColumn col, string prefix)
    {
        int n = col.Slot;
        b.AppendLine($"{prefix}if (nonNullCount_{n} == buffer_{n}.Length)");
        b.AppendLine($"{prefix}{{");
        b.AppendLine(
            $"{prefix}    var gv_{n} = global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Rent(buffer_{n}.Length * 2);"
        );
        b.AppendLine(
            $"{prefix}    global::System.Array.Copy(buffer_{n}, gv_{n}, nonNullCount_{n});"
        );
        b.AppendLine(
            $"{prefix}    global::System.Buffers.ArrayPool<{col.PackedType}>.Shared.Return(buffer_{n}, clearArray: true);"
        );
        b.AppendLine($"{prefix}    buffer_{n} = gv_{n};");
        b.AppendLine($"{prefix}}}");
    }

    /// <summary>
    /// Row-level list/array extraction (M3a): foreach keeps every collection shape uniform —
    /// the empty-list marker is emitted when the loop body never ran (docs/15 §1.2: one entry
    /// per row at minimum, markers are not phantom slots).
    /// </summary>
    public static void EmitListExtraction(
        StringBuilder builder,
        LeafColumn col,
        string itemExpr,
        string prefix
    )
    {
        int n = col.Slot;
        PropertyModel el = col.Leaf;
        InlineAdapterModel? adapter = el.InlineAdapter;
        string conv = adapter is null
            ? GetCompoundLeafWriteExpression(el, $"el_{n}")
            : GetCompoundLeafWriteExpression(
                el with
                {
                    IsNullable = false,
                },
                adapter.ToStorage(
                    el.IsNullable && adapter.DomainIsValueType ? $"el_{n}.Value" : $"el_{n}"
                )
            );
        builder.AppendLine($"{prefix}var lm_{n} = {itemExpr}.{col.ListMemberName};");
        builder.AppendLine($"{prefix}if (lm_{n} is null)");
        builder.AppendLine($"{prefix}{{");
        GrowLevels(builder, n, prefix + "    ");
        builder.AppendLine($"{prefix}    defLevels_{n}[posCount_{n}] = {col.StepDefBase[0]};");
        builder.AppendLine($"{prefix}    repLevels_{n}[posCount_{n}] = 0;");
        builder.AppendLine($"{prefix}    posCount_{n}++;");
        builder.AppendLine($"{prefix}}}");
        builder.AppendLine($"{prefix}else {{");
        builder.AppendLine($"{prefix}    bool any_{n} = false;");
        builder.AppendLine($"{prefix}    int ei_{n} = 0;");
        builder.AppendLine($"{prefix}    foreach (var el_{n} in lm_{n})");
        builder.AppendLine($"{prefix}    {{");
        builder.AppendLine($"{prefix}        any_{n} = true;");
        GrowLevels(builder, n, prefix + "        ");
        if (col.ListElementStruct is not null)
        {
            EmitStructElementExtraction(builder, col, prefix + "        ");
        }
        else if (el.IsNullable)
        {
            builder.AppendLine($"{prefix}        if (el_{n} is not null) {{");
            GrowValues(builder, col, prefix + "            ");
            builder.AppendLine($"{prefix}            buffer_{n}[nonNullCount_{n}++] = {conv};");
            builder.AppendLine($"{prefix}            defLevels_{n}[posCount_{n}] = {col.MaxDef};");
            builder.AppendLine($"{prefix}        }}");
            builder.AppendLine($"{prefix}        else {{");
            builder.AppendLine(
                $"{prefix}            defLevels_{n}[posCount_{n}] = {col.ListElementRung};"
            );
            builder.AppendLine($"{prefix}        }}");
        }
        else
        {
            GrowValues(builder, col, prefix + "        ");
            builder.AppendLine($"{prefix}        buffer_{n}[nonNullCount_{n}++] = {conv};");
            builder.AppendLine($"{prefix}        defLevels_{n}[posCount_{n}] = {col.MaxDef};");
        }
        builder.AppendLine($"{prefix}        repLevels_{n}[posCount_{n}] = ei_{n} == 0 ? 0 : 1;");
        builder.AppendLine($"{prefix}        posCount_{n}++;");
        builder.AppendLine($"{prefix}        ei_{n}++;");
        builder.AppendLine($"{prefix}    }}");
        builder.AppendLine($"{prefix}    if (!any_{n}) {{");
        GrowLevels(builder, n, prefix + "        ");
        builder.AppendLine(
            $"{prefix}        defLevels_{n}[posCount_{n}] = {col.ListPresenceRung};"
        );
        builder.AppendLine($"{prefix}        repLevels_{n}[posCount_{n}] = 0;");
        builder.AppendLine($"{prefix}        posCount_{n}++;");
        builder.AppendLine($"{prefix}    }}");
        builder.AppendLine($"{prefix}}}");
    }

    /// <summary>
    /// Rebuild row-level list lanes from the def/rep entry stream: rep==0 opens a row,
    /// the presence rung yields an empty list, element rungs append (nulls included) in
    /// order, consuming the packed value lane at the value rung.
    /// </summary>
    private static void EmitListLanes(
        StringBuilder builder,
        EmissionPlan plan,
        string rowCountVar,
        string indent
    )
    {
        var seenRoots = new HashSet<int>();
        foreach (LeafColumn col in plan.Columns)
        {
            if (!col.IsListLeaf || !seenRoots.Add(col.RootPropertyIndex))
                continue;
            if (col.ListElementStruct is null)
            {
                EmitLeafListLane(builder, col, rowCountVar, indent);
                continue;
            }
            List<LeafColumn> group =
            [
                .. plan.Columns.Where(c =>
                    c.IsListLeaf && c.RootPropertyIndex == col.RootPropertyIndex
                ),
            ];
            EmitStructListLane(builder, col, group, rowCountVar, indent);
        }
    }

    private static void EmitLeafListLane(
        StringBuilder builder,
        LeafColumn col,
        string rowCountVar,
        string indent
    )
    {
        int n = col.Slot;
        string listType = ListTypeOf(col, col.Leaf);
        builder.AppendLine($"{indent}var lane_{n} = new {listType}?[{rowCountVar}];");
        builder.AppendLine(
            $"{indent}{listType}? bk_{n} = null; int rc_{n} = 0; int vc_{n} = 0; bool st_{n} = false;"
        );
        builder.AppendLine($"{indent}for (int p_{n} = 0; p_{n} < entries_{n}; p_{n}++)");
        builder.AppendLine($"{indent}{{");
        builder.AppendLine(
            $"{indent}    if (repLevels_{n}[p_{n}] == 0) {{ if (st_{n}) {{ if (rc_{n} >= lane_{n}.Length) throw new global::System.IO.InvalidDataException(\"Repetition levels in column '{col.Leaf.Name}' produced more rows than row group capacity (\" + lane_{n}.Length + \").\"); lane_{n}[rc_{n}++] = bk_{n}; }} st_{n} = true; bk_{n} = null; }}"
        );
        builder.AppendLine($"{indent}    int dv_{n} = defLevels_{n}[p_{n}];");
        builder.AppendLine(
            $"{indent}    if (dv_{n} < 0 || dv_{n} > {col.MaxDef}) throw new global::System.IO.InvalidDataException(\"Illegal definition level \" + dv_{n} + \" in column '{col.Leaf.Name}' (max: {col.MaxDef}).\");"
        );
        builder.AppendLine(
            $"{indent}    if (dv_{n} == {col.ListPresenceRung}) bk_{n} = new {listType}();"
        );
        builder.AppendLine($"{indent}    else if (dv_{n} >= {col.ListElementRung}) {{");
        builder.AppendLine($"{indent}        bk_{n} ??= new {listType}();");
        builder.AppendLine(
            $"{indent}        if (dv_{n} == {col.MaxDef}) {{ if (vc_{n} >= entries_{n}) throw new global::System.IO.InvalidDataException(\"Definition levels in column '{col.Leaf.Name}' exceeded values count (\" + entries_{n} + \").\"); bk_{n}.Add({ElementRead(col.Leaf, GetCompoundLeafReadExpression(col.Leaf, $"buffer_{n}[vc_{n}++]"))}); }}"
        );
        if (col.Leaf.IsNullable)
            builder.AppendLine($"{indent}        else bk_{n}.Add(null!);");
        builder.AppendLine($"{indent}    }}");
        builder.AppendLine($"{indent}}}");
        builder.AppendLine(
            $"{indent}if (st_{n}) {{ if (rc_{n} >= lane_{n}.Length) throw new global::System.IO.InvalidDataException(\"Repetition levels in column '{col.Leaf.Name}' produced more rows than row group capacity (\" + lane_{n}.Length + \").\"); lane_{n}[rc_{n}++] = bk_{n}; }}"
        );
        builder.AppendLine($"{indent}_ = rc_{n};");
    }

    /// <summary>
    /// List&lt;POCO&gt; lane: the element's child columns share one entry stream position (every
    /// element contributes exactly one def slot per column, nulls included), so a single walk
    /// on the anchor column's rep/def drives aligned per-column value cursors. def==element
    /// rung is a null element; above it the child fields read at the same position.
    /// </summary>
    private static void EmitStructListLane(
        StringBuilder builder,
        LeafColumn anchor,
        List<LeafColumn> group,
        string rowCountVar,
        string indent
    )
    {
        int a = anchor.Slot;
        PropertyModel element = anchor.ListElementStruct!;
        InlineAdapterModel? adapter = element.InlineAdapter;
        string elemType = element.TypeName.TrimEnd('?');
        bool domainIsValueType = adapter?.DomainIsValueType ?? element.CompoundIsValueType;
        string annotated = (adapter?.DomainTypeName ?? elemType) + (element.IsNullable ? "?" : "");
        string listType = $"global::System.Collections.Generic.List<{annotated}>";
        builder.AppendLine($"{indent}var lane_{a} = new {listType}?[{rowCountVar}];");
        builder.AppendLine(
            $"{indent}{listType}? bk_{a} = null; int rc_{a} = 0; bool st_{a} = false;"
        );
        foreach (LeafColumn g in group)
            builder.AppendLine($"{indent}int vc_{g.Slot} = 0;");
        builder.AppendLine($"{indent}for (int p_{a} = 0; p_{a} < entries_{a}; p_{a}++)");
        builder.AppendLine($"{indent}{{");
        builder.AppendLine(
            $"{indent}    if (repLevels_{a}[p_{a}] == 0) {{ if (st_{a}) {{ if (rc_{a} >= lane_{a}.Length) throw new global::System.IO.InvalidDataException(\"Repetition levels in column '{anchor.Leaf.Name}' produced more rows than row group capacity (\" + lane_{a}.Length + \").\"); lane_{a}[rc_{a}++] = bk_{a}; }} st_{a} = true; bk_{a} = null; }}"
        );
        builder.AppendLine($"{indent}    int dv_{a} = defLevels_{a}[p_{a}];");
        builder.AppendLine(
            $"{indent}    if (dv_{a} < 0 || dv_{a} > {anchor.MaxDef}) throw new global::System.IO.InvalidDataException(\"Illegal definition level \" + dv_{a} + \" in column '{anchor.Leaf.Name}' (max: {anchor.MaxDef}).\");"
        );
        builder.AppendLine(
            $"{indent}    if (dv_{a} == {anchor.ListPresenceRung}) bk_{a} = new {listType}();"
        );
        builder.AppendLine($"{indent}    else if (dv_{a} >= {anchor.ListElementRung}) {{");
        builder.AppendLine($"{indent}        bk_{a} ??= new {listType}();");
        // A null element needs a null state: a non-nullable value-type element has none, so
        // the rung can only come from a malformed file.
        string nullElement =
            domainIsValueType && !element.IsNullable
                ? $"throw new global::System.IO.InvalidDataException(\"Column '{anchor.Leaf.Name}' holds a null list element, but the element type is a non-nullable value type.\");"
                : $"bk_{a}.Add(null!);";
        builder.AppendLine(
            $"{indent}        if (dv_{a} == {anchor.ListElementRung}) {nullElement}"
        );
        builder.AppendLine($"{indent}        else {{");
        foreach (LeafColumn g in group)
        {
            builder.AppendLine(
                $"{indent}            if (defLevels_{g.Slot}[p_{a}] >= {g.MaxDef} && vc_{g.Slot} >= entries_{g.Slot}) throw new global::System.IO.InvalidDataException(\"Definition levels in column '{g.Leaf.Name}' exceeded values count (\" + entries_{g.Slot} + \").\");"
            );
        }
        builder.AppendLine(
            adapter is null
                ? $"{indent}            bk_{a}.Add(new {elemType}"
                : $"{indent}            bk_{a}.Add({adapter.AdapterTypeName}.{adapter.FromStorageMethod}(new {elemType}"
        );
        builder.AppendLine($"{indent}            {{");
        for (int c = 0; c < element.Children.Length; c++)
        {
            PropertyModel child = element.Children[c];
            List<LeafColumn> columns = [.. group.Where(g => g.SchemaPath[0] == c)];
            string value =
                child.Kind == PropertyKind.Struct
                    ? ElementGroupRead(child, columns, a)
                    : ElementLeafRead(columns[0], a);
            builder.AppendLine($"{indent}                {child.Name} = {value},");
        }
        builder.AppendLine(
            adapter is null ? $"{indent}            }});" : $"{indent}            }}));"
        );
        builder.AppendLine($"{indent}        }}");
        builder.AppendLine($"{indent}    }}");
        builder.AppendLine($"{indent}}}");
        builder.AppendLine(
            $"{indent}if (st_{a}) {{ if (rc_{a} >= lane_{a}.Length) throw new global::System.IO.InvalidDataException(\"Repetition levels in column '{anchor.Leaf.Name}' produced more rows than row group capacity (\" + lane_{a}.Length + \").\"); lane_{a}[rc_{a}++] = bk_{a}; }}"
        );
        builder.AppendLine($"{indent}_ = rc_{a};");
    }

    private static string ListTypeOf(LeafColumn col, PropertyModel element)
    {
        // The parser renders element types without nullability suffixes; the lane must
        // carry the element's annotation or assigning a List<string> into a
        // List<string?> property fails the compiler's nullability check.
        string elemType = element.InlineAdapter?.DomainTypeName ?? element.TypeName;
        if (
            element.IsNullable
            && !elemType.EndsWith("?", StringComparison.Ordinal)
            && !elemType.EndsWith("[]", StringComparison.Ordinal)
        )
            elemType += "?";
        return $"global::System.Collections.Generic.List<{elemType}>";
    }

    /// <summary>
    /// Emits the read-path reconstruction of every struct node in the model, children before
    /// parents: one object array per node, filled by walking the leaves' definition ladders
    /// in row order against packed-lane cursors.
    /// </summary>
    public static void EmitCompoundReconstruction(
        StringBuilder builder,
        TargetClassModel model,
        string rowCountVar,
        string indent
    )
    {
        EmissionPlan plan = EmissionPlan.For(model);
        if (!plan.HasCompound)
            return;

        EmitListLanes(builder, plan, rowCountVar, indent);

        foreach (LeafColumn col in plan.Columns)
        {
            // List lanes consume their packed values through the per-walk vc_ cursors;
            // cursor_ is for the node reconstruction walk only.
            if (col.IsCompound && !(col.IsListLeaf && col.ListElementStruct is not null))
                builder.AppendLine($"{indent}int cursor_{col.Slot} = 0;");
        }

        foreach (StructNode node in plan.NodesInReconstructionOrder())
        {
            builder.AppendLine(
                $"{indent}var {node.ArrayVar} = new {node.ArrayElementType}[{rowCountVar}];"
            );
            builder.AppendLine(
                $"{indent}for (int ri_{node.Id} = 0; ri_{node.Id} < {rowCountVar}; ri_{node.Id}++)"
            );
            builder.AppendLine($"{indent}{{");
            LeafColumn presence = node.PresenceLeaf!;
            builder.AppendLine(
                $"{indent}    if (defLevels_{presence.Slot}[ri_{node.Id}] >= {node.PresenceThreshold})"
            );
            builder.AppendLine($"{indent}    {{");
            string p = indent + "        ";
            builder.AppendLine($"{p}var o_{node.Id} = new {node.ClrType}");
            builder.AppendLine($"{p}{{");
            foreach (int childId in node.ChildIds)
            {
                StructNode child = plan.Nodes[childId];
                string bang = child.IsValueType || child.MemberAnnotatedNullable ? "" : "!";
                if (child.Adapter is { } childAdapter)
                {
                    // Rebuilt as its surrogate; converted back as it is attached to the parent.
                    // Presence gates a nullable member so absent means null, never a converted
                    // default surrogate.
                    string stored = child.IsValueType
                        ? $"{child.ArrayVar}[ri_{node.Id}]"
                        : $"{child.ArrayVar}[ri_{node.Id}]!";
                    LeafColumn cp = child.PresenceLeaf!;
                    builder.AppendLine(
                        child.MemberAnnotatedNullable
                            ? $"{p}    {child.MemberName} = defLevels_{cp.Slot}[ri_{node.Id}] >= {child.PresenceThreshold} ? {childAdapter.FromStorage(stored)} : null,"
                            : $"{p}    {child.MemberName} = {childAdapter.FromStorage(stored)},"
                    );
                }
                else if (child.IsValueType && child.MemberAnnotatedNullable)
                {
                    // Absent must mean null, not default(T): gate on the child's presence rung.
                    LeafColumn cp = child.PresenceLeaf!;
                    builder.AppendLine(
                        $"{p}    {child.MemberName} = defLevels_{cp.Slot}[ri_{node.Id}] >= {child.PresenceThreshold} ? {child.ArrayVar}[ri_{node.Id}] : null,"
                    );
                }
                else
                {
                    builder.AppendLine(
                        $"{p}    {child.MemberName} = {child.ArrayVar}[ri_{node.Id}]{bang},"
                    );
                }
            }
            foreach (LeafColumn leafCol in node.Leaves)
            {
                string lane = $"buffer_{leafCol.Slot}[cursor_{leafCol.Slot}++]";
                string expr = AssignConverted(
                    leafCol.Leaf,
                    GetCompoundLeafReadExpression(leafCol.Leaf, lane)
                );
                bool nullableish =
                    leafCol.Leaf.IsNullable
                    || (
                        leafCol.Leaf.Kind == PropertyKind.Primitive
                        && leafCol.Leaf.TypeName.Contains("string")
                    )
                    || leafCol.Leaf.Kind == PropertyKind.ByteArray;
                string absent =
                    leafCol.Leaf.InlineAdapter is not null
                        ? (leafCol.Leaf.IsNullable ? "null" : "default!")
                    : nullableish ? "null!"
                    : "default";
                builder.AppendLine(
                    $"{p}    {leafCol.Leaf.Name} = defLevels_{leafCol.Slot}[ri_{node.Id}] >= {leafCol.MaxDef} ? {expr} : {absent},"
                );
            }
            builder.AppendLine($"{p}}};");
            builder.AppendLine($"{p}{node.ArrayVar}[ri_{node.Id}] = o_{node.Id};");
            builder.AppendLine($"{indent}    }}");
            builder.AppendLine($"{indent}}}");
        }
    }

    /// <summary>
    /// Per-row member assignments for the root object initializer, mapping each root property
    /// to its leaf buffer lane or reconstructed struct array.
    /// </summary>
    public static void EmitRootMemberAssignments(
        StringBuilder builder,
        TargetClassModel model,
        string indexVar,
        string prefix,
        string bufferPrefix = "buffer_"
    )
    {
        EmissionPlan plan = EmissionPlan.For(model);
        for (int p = 0; p < model.Properties.Length; p++)
        {
            PropertyModel prop = model.Properties[p];
            LeafColumn? listCol = plan.Columns.FirstOrDefault(c =>
                c.IsListLeaf && c.RootPropertyIndex == p
            );
            if (listCol is not null)
            {
                string lb = listCol.MemberAnnotatedNullable ? "" : "!";
                string tail = listCol.ListMemberIsArray
                    ? $" is {{ }} ln_{listCol.Slot} ? ln_{listCol.Slot}.ToArray() : null{lb}"
                    : lb;
                builder.AppendLine($"{prefix}{prop.Name} = lane_{listCol.Slot}[{indexVar}]{tail},");
                continue;
            }
            int nodeId = plan.RootNodeByProperty[p];
            if (nodeId >= 0)
            {
                StructNode node = plan.Nodes[nodeId];
                string bang = node.IsValueType || prop.IsNullable ? "" : "!";
                if (node.IsValueType && prop.IsNullable)
                {
                    LeafColumn pp = node.PresenceLeaf!;
                    builder.AppendLine(
                        $"{prefix}{prop.Name} = defLevels_{pp.Slot}[{indexVar}] >= {node.PresenceThreshold} ? {node.ArrayVar}[{indexVar}] : null,"
                    );
                }
                else
                {
                    builder.AppendLine($"{prefix}{prop.Name} = {node.ArrayVar}[{indexVar}]{bang},");
                }
                continue;
            }

            int slot = plan.ColumnSlotByProperty[p];
            string readExpr = PropertyMappingComponent.GetReadExpression(
                prop,
                $"{bufferPrefix}{slot}[{indexVar}]"
            );
            builder.AppendLine($"{prefix}{prop.Name} = {readExpr},");
        }
    }

    /// <summary>
    /// Row-group materialization for compound models: reconstruction preamble then the row
    /// loop building each root object from the arrays. Mirrors the shape of
    /// PropertyMappingComponent.EmitArrayMaterialization's standard branch.
    /// </summary>
    public static void EmitArrayMaterialization(
        StringBuilder builder,
        TargetClassModel model,
        string targetArrayVar,
        string startOffsetVar,
        string rowCountVar = "rowCount",
        string indexVar = "i",
        string bufferPrefix = "buffer_",
        string indent = "                "
    )
    {
        EmitCompoundReconstruction(builder, model, rowCountVar, indent);
        builder.AppendLine(
            $"{indent}for (int {indexVar} = 0; {indexVar} < {rowCountVar}; {indexVar}++)"
        );
        builder.AppendLine($"{indent}{{");
        string targetSlot = string.IsNullOrEmpty(startOffsetVar)
            ? $"{targetArrayVar}[{indexVar}]"
            : $"{targetArrayVar}[{startOffsetVar} + {indexVar}]";
        builder.AppendLine($"{indent}    {targetSlot} = new {model.ClassName}");
        builder.AppendLine($"{indent}    {{");
        EmitRootMemberAssignments(builder, model, indexVar, indent + "        ", bufferPrefix);
        builder.AppendLine($"{indent}    }};");
        builder.AppendLine($"{indent}}}");
    }

    /// <summary>A direct element member read from its lane at the element's entry position.</summary>
    private static string ElementLeafRead(LeafColumn g, int a)
    {
        string read = AssignConverted(
            g.Leaf,
            GetCompoundLeafReadExpression(g.Leaf, $"buffer_{g.Slot}[vc_{g.Slot}++]")
        );
        string absent = g.Leaf.IsNullable ? "null!" : "default";
        return $"defLevels_{g.Slot}[p_{a}] >= {g.MaxDef} ? {read} : {absent}";
    }

    /// <summary>
    /// A group one level inside an element: present when its first leaf's def reaches the
    /// group rung, rebuilt from its leaves, converted back through its inline adapter if any.
    /// </summary>
    private static string ElementGroupRead(PropertyModel child, List<LeafColumn> columns, int a)
    {
        string type = child.TypeName.TrimEnd('?');
        string fields = string.Join(
            ", ",
            columns.Select(g => $"{g.Leaf.Name} = {ElementLeafRead(g, a)}")
        );
        string rebuilt = AssignConverted(child, $"new {type} {{ {fields} }}");
        string absent =
            child.IsNullable ? "null"
            : child.CompoundIsValueType && child.InlineAdapter is null ? "default"
            : "default!";
        LeafColumn presence = columns[0];
        return $"defLevels_{presence.Slot}[p_{a}] > {presence.StepDefBase[2]} ? {rebuilt} : {absent}";
    }

    /// <summary>
    /// A nested member's value as the write ladder reads it: the member itself, or — for an
    /// inline-adapted member — its storage form, nullable when the member is.
    /// </summary>
    private static string ReadConverted(
        PropertyModel leaf,
        string access,
        string patternVariable
    ) =>
        leaf.InlineAdapter is { } adapter
            ? adapter.ToStorageFrom(access, leaf.IsNullable, patternVariable)
            : access;

    /// <summary>A present nested value as assigned to its parent: converted back if adapted.</summary>
    private static string AssignConverted(PropertyModel leaf, string storage) =>
        leaf.InlineAdapter is { } adapter ? adapter.FromStorage(storage) : storage;

    /// <summary>A leaf element's read expression, converted back to its domain type if adapted.</summary>
    private static string ElementRead(PropertyModel element, string storage) =>
        element.InlineAdapter is { } adapter ? adapter.FromStorage(storage) : storage;

    private static bool ValueNullableWrapper(PropertyModel leaf) =>
        !(leaf.Kind == PropertyKind.Primitive && leaf.TypeName.Contains("string"))
        && leaf.Kind != PropertyKind.ByteArray;
}
