using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Parquet.SourceGenerator.Emitter;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The emitted <c>DecompressionGuardStream</c> is a private nested type, so these tests reach it by
/// reflection. They pin the parts of the <see cref="Stream"/> contract the analyzers flagged:
/// disposal, Flush, and the Memory/Span overloads.
/// </summary>
#pragma warning disable CA1835 // the array overload is the reference behaviour under test
public sealed class DecompressionGuardStreamContractTests
{
    private static readonly Type GuardType = typeof(BufferModel)
        .Assembly.GetTypes()
        .First(t => t.Name == "DecompressionGuardStream");

    private static Stream CreateGuard(Stream inner) =>
        (Stream)
            Activator.CreateInstance(
                GuardType,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                args: new object[] { inner, int.MaxValue, int.MaxValue },
                culture: null
            )!;

    private static byte[] Sample() => Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    [Fact]
    public void DisposingTheGuardLeavesTheInnerStreamUsable()
    {
        using var inner = new MemoryStream(Sample());
        CreateGuard(inner).Dispose();

        inner.CanRead.ShouldBeTrue();
        inner.Position = 0;
        inner.ReadByte().ShouldBe(0);
    }

    [Fact]
    public void FlushIsANoOp()
    {
        using var inner = new MemoryStream(Sample());
        using Stream guard = CreateGuard(inner);

        Should.NotThrow(() => guard.Flush());
    }

    [Fact]
    public async Task ReadAsyncMemoryMatchesTheArrayOverload()
    {
        byte[] data = Sample();
        using Stream guardA = CreateGuard(new MemoryStream(data));
        using Stream guardB = CreateGuard(new MemoryStream(data));

        var viaArray = new byte[32];
        var viaMemory = new byte[32];
        int a = await guardA.ReadAsync(viaArray, 0, viaArray.Length, CancellationToken.None);
        int b = await guardB.ReadAsync(viaMemory.AsMemory(), CancellationToken.None);

        b.ShouldBe(a);
        viaMemory.ShouldBe(viaArray);
    }

    [Fact]
    public void ReadSpanMatchesTheArrayOverload()
    {
        byte[] data = Sample();
        using Stream guardA = CreateGuard(new MemoryStream(data));
        using Stream guardB = CreateGuard(new MemoryStream(data));

        var viaArray = new byte[32];
        var viaSpan = new byte[32];
        int a = guardA.Read(viaArray, 0, viaArray.Length);
        int b = guardB.Read(viaSpan.AsSpan());

        b.ShouldBe(a);
        viaSpan.ShouldBe(viaArray);
    }

    [Fact]
    public void MemoryOverloadsAreGatedForNet472()
    {
        var model = new TargetClassModel(
            Namespace: "TestNamespace",
            ClassName: "TestEntity",
            Properties: new EquatableArray<PropertyModel>(Array.Empty<PropertyModel>())
        );

        string source = CodeEmitter.EmitSource(model);
        int gate = source.IndexOf(
            "#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER",
            StringComparison.Ordinal
        );
        int overload = source.IndexOf(
            "ReadAsync(global::System.Memory<byte>",
            StringComparison.Ordinal
        );
        int end = source.IndexOf("#endif", gate, StringComparison.Ordinal);

        gate.ShouldBeGreaterThanOrEqualTo(0);
        overload.ShouldBeInRange(gate, end);
    }
}
