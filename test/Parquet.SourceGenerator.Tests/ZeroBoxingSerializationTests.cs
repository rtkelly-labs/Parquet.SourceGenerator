using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Parquet.SourceGenerator;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

[ParquetSerializable]
public partial record ZeroBoxingRecord
{
    [ParquetColumn("id")]
    public int Id { get; init; }

    [ParquetColumn("name")]
    public string Name { get; init; } = string.Empty;

    [ParquetColumn("description")]
    public string? Description { get; init; }

    [ParquetColumn("data")]
    public byte[] Data { get; init; } = Array.Empty<byte>();

    [ParquetColumn("optional_data")]
    public byte[]? OptionalData { get; init; }
}

public class ZeroBoxingSerializationTests
{
    private static readonly OpCode[] SingleByteOpCodes = new OpCode[256];
    private static readonly OpCode[] MultiByteOpCodes = new OpCode[256];

    static ZeroBoxingSerializationTests()
    {
        foreach (
            FieldInfo fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        )
        {
            if (fi.GetValue(null) is OpCode op)
            {
                if (op.OpCodeType == OpCodeType.Nternal)
                {
                    continue;
                }

                if ((ushort)op.Value < 0x100)
                {
                    SingleByteOpCodes[(byte)op.Value] = op;
                }
                else if (((ushort)op.Value >> 8) == 0xFE)
                {
                    MultiByteOpCodes[(byte)op.Value] = op;
                }
            }
        }
    }

    public static int CountBoxInstructions(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            return 0;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();

        int count = 0;
        int pos = 0;
        while (pos < il.Length)
        {
            byte b = il[pos++];
            OpCode opcode;
            if (b == 0xFE)
            {
                if (pos >= il.Length)
                {
                    break;
                }

                opcode = MultiByteOpCodes[il[pos++]];
            }
            else
            {
                opcode = SingleByteOpCodes[b];
            }

            if (opcode == OpCodes.Box)
            {
                count++;
            }

            switch (opcode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineVar:
                    pos += 1;
                    break;
                case OperandType.InlineVar:
                    pos += 2;
                    break;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                case OperandType.ShortInlineR:
                    pos += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    pos += 8;
                    break;
                case OperandType.InlineSwitch:
                    if (pos + 4 <= il.Length)
                    {
                        int switchCount = BitConverter.ToInt32(il, pos);
                        pos += 4 + (switchCount * 4);
                    }
                    break;
                default:
                    break;
            }
        }

        return count;
    }

    public static int CountBoxInstructionsInMethodAndStateMachine(MethodInfo method)
    {
        int boxes = CountBoxInstructions(method);
        var attr =
            method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
        if (attr != null)
        {
            MethodInfo? moveNext = attr.StateMachineType.GetMethod(
                "MoveNext",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );
            if (moveNext != null)
            {
                boxes += CountBoxInstructions(moveNext);
            }
        }

        return boxes;
    }

    /// <summary>
    /// Every <c>WriteParquetRowGroupAsync</c> overload on a generated extension type. Since #137 a
    /// flat model gets two — the row-oriented one and the columnar hand-off — and both have to be
    /// boxing-free, so the lookup enumerates rather than resolving a single method (which now
    /// throws <see cref="System.Reflection.AmbiguousMatchException"/>).
    /// </summary>
    private static MethodInfo[] WriteRowGroupOverloads(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "WriteParquetRowGroupAsync")
            .ToArray();

    private static int CountBoxInstructionsInAllWriteRowGroupOverloads(Type type)
    {
        int boxes = 0;
        foreach (MethodInfo overload in WriteRowGroupOverloads(type))
        {
            boxes += CountBoxInstructionsInMethodAndStateMachine(overload);
        }

        return boxes;
    }

    [Fact]
    public void WriteParquetRowGroupAsyncEmitsZeroBoxingOpcodes()
    {
        Type type = typeof(ZeroBoxingRecordParquetExtensions);
        WriteRowGroupOverloads(type).ShouldNotBeEmpty();
        CountBoxInstructionsInAllWriteRowGroupOverloads(type).ShouldBe(0);
    }

    [Fact]
    public async Task RoundTripZeroBoxingStringAndBinaryDataPreservesAllValues()
    {
        var items = new List<ZeroBoxingRecord>
        {
            new ZeroBoxingRecord
            {
                Id = 1,
                Name = "Alpha",
                Description = "Standard string",
                Data = new byte[] { 1, 2, 3, 4 },
                OptionalData = new byte[] { 5, 6, 7, 8 },
            },
            new ZeroBoxingRecord
            {
                Id = 2,
                Name = "Beta",
                Description = null, // Nullable string = null
                Data = Array.Empty<byte>(),
                OptionalData = null, // Nullable byte array = null
            },
            new ZeroBoxingRecord
            {
                Id = 3,
                Name = "Gamma 🚀 Unicode & 汉语",
                Description = "Complex Unicode with emojis ⚡️🔥",
                Data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                OptionalData = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE },
            },
            new ZeroBoxingRecord
            {
                Id = 4,
                Name = string.Empty,
                Description = string.Empty,
                Data = new byte[] { 0 },
                OptionalData = new byte[] { 0 },
            },
        };

        using var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);

        stream.Position = 0;
        List<ZeroBoxingRecord> results = await ZeroBoxingRecordParquet.From(stream).ToListAsync();

        results.Count.ShouldBe(items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            results[i].Id.ShouldBe(items[i].Id);
            results[i].Name.ShouldBe(items[i].Name);
            results[i].Description.ShouldBe(items[i].Description);
            results[i].Data.ShouldBe(items[i].Data);
            results[i].OptionalData.ShouldBe(items[i].OptionalData);
        }
    }

    [Fact]
    public async Task RoundTripLargeBatchRecyclesBuffersWithoutCorruption()
    {
        const int count = 2_000;
        var items = Enumerable
            .Range(0, count)
            .Select(i => new ZeroBoxingRecord
            {
                Id = i,
                Name = $"Name_{i % 50}",
                Description = i % 3 == 0 ? null : $"Description_{i}",
                Data = new byte[] { (byte)(i & 0xFF), (byte)((i >> 8) & 0xFF) },
                OptionalData = i % 2 == 0 ? null : new byte[] { (byte)(i % 100) },
            })
            .ToList();

        using var stream = new MemoryStream();
        await items.WriteParquetAsync(stream);

        stream.Position = 0;
        List<ZeroBoxingRecord> results = await ZeroBoxingRecordParquet.From(stream).ToListAsync();

        results.Count.ShouldBe(count);

        for (int i = 0; i < count; i++)
        {
            results[i].Id.ShouldBe(items[i].Id);
            results[i].Name.ShouldBe(items[i].Name);
            results[i].Description.ShouldBe(items[i].Description);
            results[i].Data.ShouldBe(items[i].Data);
            results[i].OptionalData.ShouldBe(items[i].OptionalData);
        }
    }

    [Fact]
    public void AllGeneratedExtensionTypesInAssemblyWriteParquetRowGroupAsyncEmitZeroBoxingOpcodes()
    {
        var extensionTypes = typeof(ZeroBoxingSerializationTests)
            .Assembly.GetTypes()
            .Where(t => t.IsClass && t.Name.EndsWith("ParquetExtensions", StringComparison.Ordinal))
            .ToList();

        extensionTypes.ShouldNotBeEmpty();
        (extensionTypes.Count >= 20).ShouldBeTrue(
            $"Expected at least 20 generated extension types in assembly, found {extensionTypes.Count}"
        );

        var violations = new List<string>();

        foreach (Type type in extensionTypes)
        {
            if (WriteRowGroupOverloads(type).Length == 0)
            {
                continue;
            }

            int boxCount = CountBoxInstructionsInAllWriteRowGroupOverloads(type);
            if (boxCount > 0)
            {
                violations.Add($"{type.FullName}: found {boxCount} box opcode(s)");
            }
        }

        violations.Count.ShouldBe(
            0,
            $"Found boxing opcodes in WriteParquetRowGroupAsync:\n{string.Join("\n", violations)}"
        );
    }

    [Theory]
    [InlineData("public string? Field { get; init; }")]
    [InlineData("public string Field { get; init; } = string.Empty;")]
    [InlineData("public byte[]? Field { get; init; }")]
    [InlineData("public byte[] Field { get; init; } = global::System.Array.Empty<byte>();")]
    [InlineData("public int Field { get; init; }")]
    [InlineData("public decimal Field { get; init; }")]
    [InlineData("public global::System.Guid Field { get; init; }")]
    [InlineData("public global::System.Guid? Field { get; init; }")]
    [InlineData("public global::System.DateTime Field { get; init; }")]
    [InlineData("public global::System.TimeSpan Field { get; init; }")]
    [InlineData("public global::System.TimeOnly Field { get; init; }")]
    public void DynamicModelPropertiesEmitZeroBoxingOpcodes(string propertySnippet)
    {
        string source = $$"""
            namespace DynamicBoxingTest;

            using System;
            using Parquet.SourceGenerator;

            [ParquetSerializable]
            public partial record TestModel
            {
                [ParquetColumn("test_col")]
                {{propertySnippet}}
            }
            """;

        Assembly compiledAssembly = CompileSourceToAssembly(source);
        Type? extType = compiledAssembly.GetType("DynamicBoxingTest.TestModelParquetExtensions");
        extType.ShouldNotBeNull();

        WriteRowGroupOverloads(extType).ShouldNotBeEmpty();
        CountBoxInstructionsInAllWriteRowGroupOverloads(extType).ShouldBe(0);
    }

    [Fact]
    public void DynamicMultiPropertyComprehensiveModelEmitsZeroBoxingOpcodes()
    {
        string source = """
            namespace DynamicBoxingTest;

            using System;
            using Parquet.SourceGenerator;

            [ParquetSerializable]
            public partial record ComprehensiveModel
            {
                [ParquetColumn("id")]
                public int Id { get; init; }

                [ParquetColumn("str_null")]
                public string? StrNull { get; init; }

                [ParquetColumn("str_non_null")]
                public string StrNonNull { get; init; } = string.Empty;

                [ParquetColumn("bytes_null")]
                public byte[]? BytesNull { get; init; }

                [ParquetColumn("bytes_non_null")]
                public byte[] BytesNonNull { get; init; } = Array.Empty<byte>();

                [ParquetColumn("dec")]
                public decimal Dec { get; init; }

                [ParquetColumn("guid")]
                public Guid GuidVal { get; init; }

                [ParquetColumn("guid_null")]
                public Guid? GuidNull { get; init; }

                [ParquetColumn("created_at")]
                public DateTime CreatedAt { get; init; }

                [ParquetColumn("span")]
                public TimeSpan Span { get; init; }

                [ParquetColumn("time_only")]
                public TimeOnly TimeOnly { get; init; }
            }
            """;

        Assembly compiledAssembly = CompileSourceToAssembly(source);
        Type? extType = compiledAssembly.GetType(
            "DynamicBoxingTest.ComprehensiveModelParquetExtensions"
        );
        extType.ShouldNotBeNull();

        WriteRowGroupOverloads(extType).ShouldNotBeEmpty();
        CountBoxInstructionsInAllWriteRowGroupOverloads(extType).ShouldBe(0);
    }

    private static Assembly CompileSourceToAssembly(string source)
    {
        global::Microsoft.CodeAnalysis.SyntaxTree syntaxTree =
            global::Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var references = new global::Microsoft.CodeAnalysis.MetadataReference[]
        {
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                typeof(object).Assembly.Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                typeof(Attribute).Assembly.Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                typeof(ParquetSerializableAttribute).Assembly.Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                typeof(Parquet.ParquetReader).Assembly.Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                Assembly.Load("System.Runtime").Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                Assembly.Load("System.Collections").Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                Assembly.Load("System.Threading").Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                Assembly.Load("System.Threading.Tasks").Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                Assembly.Load("System.Linq").Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                Assembly.Load("System.Memory").Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                typeof(System.Buffers.ArrayPool<>).Assembly.Location
            ),
            global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                typeof(System.IO.Compression.CompressionLevel).Assembly.Location
            ),
        };

        var compilation = global::Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            $"DynamicTest_{Guid.NewGuid():N}",
            new[] { syntaxTree },
            references,
            new global::Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                global::Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: global::Microsoft.CodeAnalysis.NullableContextOptions.Enable
            )
        );

        var generator = new ParquetIncrementalGenerator();
        global::Microsoft.CodeAnalysis.GeneratorDriver driver =
            global::Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out global::Microsoft.CodeAnalysis.Compilation outputCompilation,
            out var diagnostics
        );

        diagnostics.ShouldNotContain(d =>
            d.Severity == global::Microsoft.CodeAnalysis.DiagnosticSeverity.Error
        );

        using var peStream = new MemoryStream();
        global::Microsoft.CodeAnalysis.Emit.EmitResult emitResult = outputCompilation.Emit(
            peStream
        );
        emitResult.Success.ShouldBeTrue(
            string.Join(
                "\n",
                emitResult
                    .Diagnostics.Where(d =>
                        d.Severity == global::Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                    )
                    .Select(d =>
                        d.GetMessage(global::System.Globalization.CultureInfo.InvariantCulture)
                    )
            )
        );

        return Assembly.Load(peStream.ToArray());
    }
}
