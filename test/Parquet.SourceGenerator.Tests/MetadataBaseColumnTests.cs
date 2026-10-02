using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Parser;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Issue #420: the base-type walk stopped at a base with no declaring syntax, which is every base
/// read from metadata, so moving a shared base class into its own project silently dropped its
/// columns from every derived model. Framework bases must still contribute nothing.
/// </summary>
public sealed class MetadataBaseColumnTests
{
    private const string ContractsSource = """
        using Parquet.SourceGenerator;

        namespace Contracts;

        public abstract class Audited
        {
            [ParquetColumn("id")]
            public int Id { get; set; }

            [ParquetColumn("created_by")]
            public string CreatedBy { get; set; } = string.Empty;
        }

        [ParquetSerializable]
        public abstract partial class Projection
        {
            public int Amount { get; set; }
        }

        public abstract class Unannotated
        {
            public int Hidden { get; set; }
        }
        """;

    private const string ModelSource = """
        using Parquet.SourceGenerator;

        namespace Demo;

        [ParquetSerializable]
        public partial class FromAnnotatedMember : Contracts.Audited
        {
            [ParquetColumn("amount")]
            public decimal Amount { get; set; }
        }

        [ParquetSerializable]
        public partial class FromAnnotatedType : Contracts.Projection
        {
            public string Name { get; set; } = string.Empty;
        }

        [ParquetSerializable]
        public partial class FromFramework : System.Exception
        {
            public int Code { get; set; }
        }

        [ParquetSerializable]
        public partial class FromUnannotated : Contracts.Unannotated
        {
            public int Own { get; set; }
        }
        """;

    private static MetadataReference[] FrameworkReferences() =>
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(ParquetSerializableAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        ];

    private static string[] Columns(string typeName)
    {
        // The contracts assembly is emitted and referenced as metadata, as a separate project's
        // output would be, so its types have no declaring syntax in the model compilation.
        CSharpCompilation contracts = CSharpCompilation.Create(
            "Contracts",
            [CSharpSyntaxTree.ParseText(ContractsSource)],
            FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        contracts.Emit(stream).Success.ShouldBeTrue();
        stream.Position = 0;

        CSharpCompilation models = CSharpCompilation.Create(
            "Models",
            [CSharpSyntaxTree.ParseText(ModelSource)],
            [.. FrameworkReferences(), MetadataReference.CreateFromStream(stream)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        INamedTypeSymbol symbol = models.GetTypeByMetadataName("Demo." + typeName)!;
        TargetParserResult result = TargetParser.GetTargetModel(
            symbol,
            ParquetApiLevel.V6,
            allowCompoundTypes: false
        );

        result.Model.ShouldNotBeNull(typeName);
        return result.Model.Properties.Select(p => p.ParquetColumnName).ToArray();
    }

    [Fact]
    public void ABaseFromAnotherAssemblyWithAnnotatedMembersContributesItsColumnsFirst() =>
        Columns("FromAnnotatedMember").ShouldBe(["id", "created_by", "amount"]);

    [Fact]
    public void ABaseFromAnotherAssemblyMarkedSerializableContributesItsColumns() =>
        Columns("FromAnnotatedType").ShouldBe(["Amount", "Name"]);

    [Fact]
    public void AFrameworkBaseStillContributesNothing() =>
        Columns("FromFramework").ShouldBe(["Code"]);

    [Fact]
    public void AnUnannotatedBaseFromAnotherAssemblyIsStillSkipped() =>
        // Documents the rule's limit: a metadata base with no Parquet attribute anywhere is
        // indistinguishable from a framework type, so it contributes nothing.
        Columns("FromUnannotated").ShouldBe(["Own"]);
}
