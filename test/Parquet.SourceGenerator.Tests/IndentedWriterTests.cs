using Parquet.SourceGenerator.Emitter.Components;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public sealed class IndentedWriterTests
{
    [Fact]
    public void IndentedWriterIndentsAndNestsBlocks()
    {
        var writer = new IndentedWriter();
        writer.Line("public class Example");
        using (writer.Block())
        {
            writer.Line("public void Method()");
            using (writer.Block())
            {
                writer.Line("var x = 1;");
            }
        }

        string source = writer.ToSource().Replace("\r\n", "\n");
        string expected =
            "public class Example\n"
            + "{\n"
            + "    public void Method()\n"
            + "    {\n"
            + "        var x = 1;\n"
            + "    }\n"
            + "}\n";

        source.ShouldBe(expected);
    }

    [Fact]
    public void IndentedWriterBlockWithHeader()
    {
        var writer = new IndentedWriter();
        using (writer.Block("namespace MyNamespace"))
        {
            writer.Line("public class Foo;");
        }

        string source = writer.ToSource().Replace("\r\n", "\n");
        string expected = "namespace MyNamespace\n" + "{\n" + "    public class Foo;\n" + "}\n";

        source.ShouldBe(expected);
    }

    [Fact]
    public void IndentedWriterMultilineStringIndentsEachLine()
    {
        var writer = new IndentedWriter();
        using (writer.Indent())
        {
            writer.Line("line1\nline2\nline3");
        }

        string source = writer.ToSource().Replace("\r\n", "\n");
        string expected = "    line1\n    line2\n    line3\n";

        source.ShouldBe(expected);
    }
}
