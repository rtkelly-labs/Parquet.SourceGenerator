using System;
using System.Text;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Indentation-aware writer abstraction for Roslyn code emission (issues #439, #719).
/// Replaces manual indentation literal prefixes ("    ", {indent}) and string-arithmetic indentation threading.
/// </summary>
internal sealed class IndentedWriter
{
    private readonly StringBuilder _builder;
    private int _indentLevel;

    public IndentedWriter(StringBuilder? builder = null)
    {
        _builder = builder ?? new StringBuilder();
    }

    public int IndentLevel
    {
        get => _indentLevel;
        set => _indentLevel = Math.Max(0, value);
    }

    public StringBuilder InnerBuilder => _builder;

    public IndentedWriter Write(string text)
    {
        _builder.Append(text);
        return this;
    }

    public IndentedWriter Line()
    {
        _builder.AppendLine();
        return this;
    }

    public IndentedWriter Line(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _builder.AppendLine();
            return this;
        }

        if (text!.IndexOf('\n') >= 0)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > 0)
                {
                    WriteIndent();
                    _builder.AppendLine(line);
                }
                else
                {
                    _builder.AppendLine();
                }
            }
            return this;
        }

        WriteIndent();
        _builder.AppendLine(text);
        return this;
    }

    private void WriteIndent()
    {
        for (int i = 0; i < _indentLevel; i++)
        {
            _builder.Append("    ");
        }
    }

    public BlockScope Block(string? header = null)
    {
        if (!string.IsNullOrEmpty(header))
        {
            Line(header);
        }
        Line("{");
        _indentLevel++;
        return new BlockScope(this);
    }

    public IndentScope Indent()
    {
        _indentLevel++;
        return new IndentScope(this);
    }

    public string ToSource() => _builder.ToString();

    public readonly struct BlockScope : IDisposable
    {
        private readonly IndentedWriter _writer;

        public BlockScope(IndentedWriter writer)
        {
            _writer = writer;
        }

        public void Dispose()
        {
            _writer._indentLevel = Math.Max(0, _writer._indentLevel - 1);
            _writer.Line("}");
        }
    }

    public readonly struct IndentScope : IDisposable
    {
        private readonly IndentedWriter _writer;

        public IndentScope(IndentedWriter writer)
        {
            _writer = writer;
        }

        public void Dispose()
        {
            _writer._indentLevel = Math.Max(0, _writer._indentLevel - 1);
        }
    }
}
