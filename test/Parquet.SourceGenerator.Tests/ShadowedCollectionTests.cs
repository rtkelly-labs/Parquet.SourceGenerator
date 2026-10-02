using System;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parquet.SourceGenerator.Diagnostics;
using Parquet.SourceGenerator.Models;
using Parquet.SourceGenerator.Parser;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Issue #417: compound classification matched a framework collection by display name alone, so a
/// user type declared into <c>System.Collections.Generic</c> with a different arity classified as a
/// list or map and the parser then indexed past its type arguments. An exception escaping the
/// generator is CS8785 and removes every generated type in the compilation.
/// </summary>
public sealed class ShadowedCollectionTests
{
    private const string Source = """
        using Parquet.SourceGenerator;

        namespace System.Collections.Generic
        {
            public class Dictionary<T> { }

            public class List<TFirst, TSecond> { }

            public class IList<T> { }
        }

        namespace Demo
        {
            [ParquetSerializable]
            public partial class ShadowedMap
            {
                public int Id { get; set; }

                public global::System.Collections.Generic.Dictionary<int> Bad { get; set; } = new();
            }

            [ParquetSerializable]
            public partial class ShadowedList
            {
                public int Id { get; set; }

                public global::System.Collections.Generic.List<int, int> Bad { get; set; } = new();
            }

            [ParquetSerializable]
            public partial class ShadowedSameArity
            {
                public int Id { get; set; }

                public global::System.Collections.Generic.IList<int> Bad { get; set; } = new();
            }

            [ParquetSerializable]
            public partial class RealMap
            {
                public int Id { get; set; }

                public global::System.Collections.Generic.Dictionary<string, int> Good { get; set; } = new();
            }
        }
        """;

    private static TargetParserResult Parse(string name)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ShadowAssembly",
            [CSharpSyntaxTree.ParseText(Source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
                MetadataReference.CreateFromFile(
                    typeof(ParquetSerializableAttribute).Assembly.Location
                ),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        INamedTypeSymbol symbol = compilation.GetTypeByMetadataName("Demo." + name)!;
        return TargetParser.GetTargetModel(symbol, ParquetApiLevel.V6, allowCompoundTypes: true);
    }

    [Theory]
    [InlineData("ShadowedMap")]
    [InlineData("ShadowedList")]
    [InlineData("ShadowedSameArity")]
    public void AShadowedCollectionNameWithAnotherArityIsRejectedNotIndexed(string name)
    {
        TargetParserResult result = Parse(name);

        result.Model.ShouldBeNull();
        result.Diagnostics.ShouldContain(d =>
            d.Descriptor.Id == DiagnosticDescriptors.UnsupportedPropertyType.Id
        );
    }

    [Fact]
    public void TheRealFrameworkDictionaryStillClassifiesAsAMap()
    {
        TargetParserResult result = Parse("RealMap");

        result.Model.ShouldNotBeNull();
        result.Model.Properties.Single(p => p.Name == "Good").Kind.ShouldBe(PropertyKind.Map);
    }
}
