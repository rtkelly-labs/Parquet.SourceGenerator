using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Parquet.Schema;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The emitted schema validation walks every field to enforce <c>MaxNestingDepth</c>. It used to
/// compute the full depth and compare afterwards, so the walk itself was bounded only by the stack
/// and a footer declaring a few hundred thousand nested lists exhausted it first (#366), which
/// terminates the process. The limit is now enforced during the walk.
/// </summary>
public sealed class SchemaDepthBoundTests
{
    private const int HostileDepth = 300_000;

    private static MethodInfo CheckFieldDepth() =>
        typeof(BoundsCheckRecordParquetExtensions).GetMethod(
            "CheckFieldDepth",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(Field), typeof(int), typeof(int)],
            modifiers: null
        )
        ?? throw new InvalidOperationException(
            "The emitted CheckFieldDepth(field, depth, maxDepth) was not found."
        );

    // Parquet.Net's own ListField constructor re-roots the whole subtree (a recursive path rewrite), which
    // is quadratic and overflows the stack long before a hostile depth. The walk under test needs only
    // the Item links, so the chain is linked directly.
    private static Field NestedLists(int depth)
    {
        PropertyInfo item = typeof(ListField).GetProperty(nameof(ListField.Item))!;
        Field field = new DataField<int>("v");
        for (int i = 0; i < depth; i++)
        {
            var list = (ListField)RuntimeHelpers.GetUninitializedObject(typeof(ListField));
            item.SetValue(list, field);
            field = list;
        }

        return field;
    }

    [Fact]
    public void AHostileNestingDepthIsRejectedDuringTheWalkNotAfterIt()
    {
        MethodInfo check = CheckFieldDepth();
        Field hostile = NestedLists(HostileDepth);

        TargetInvocationException error = Should.Throw<TargetInvocationException>(() =>
            check.Invoke(null, [hostile, 1, 64])
        );

        error.InnerException.ShouldBeOfType<InvalidDataException>();
        error.InnerException!.Message.ShouldContain("Schema nesting depth");
    }

    [Fact]
    public void ADepthWithinTheLimitPasses()
    {
        MethodInfo check = CheckFieldDepth();

        check.Invoke(null, [NestedLists(10), 1, 64]);
    }
}
