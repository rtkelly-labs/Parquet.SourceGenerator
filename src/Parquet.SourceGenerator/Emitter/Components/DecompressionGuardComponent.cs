using System.Text;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Emits the private stream guard used to stop Parquet.Net before it allocates a decompression
/// buffer for a hostile page. The guard is generated into the consumer assembly because the
/// Parquet.Net compact-protocol reader is internal in both supported API generations.
/// </summary>
internal static class DecompressionGuardComponent
{
    public static void Emit(StringBuilder builder)
    {
        builder.AppendLine(
            "    private static DecompressionGuardStream CreateGuardedReadStream(global::System.IO.Stream stream, global::Parquet.SourceGenerator.ParquetSerializerOptions options) => new(stream, options.MaxDecompressedPageSize, options.MaxDecompressionExpansionRatio);"
        );
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Seekable read wrapper that validates each page header before Parquet.Net reads or decompresses its payload."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    private sealed class DecompressionGuardStream : global::System.IO.Stream"
        );
        builder.AppendLine("    {");
        builder.AppendLine("        private readonly global::System.IO.Stream _inner;");
        builder.AppendLine("        private readonly int _maxPageSize;");
        builder.AppendLine("        private readonly int _maxExpansionRatio;");
        builder.AppendLine("        private bool _active;");
        builder.AppendLine();
        builder.AppendLine(
            "        public DecompressionGuardStream(global::System.IO.Stream inner, int maxPageSize, int maxExpansionRatio)"
        );
        builder.AppendLine("        {");
        builder.AppendLine(
            "            _inner = inner ?? throw new global::System.ArgumentNullException(nameof(inner));"
        );
        builder.AppendLine("            _maxPageSize = maxPageSize;");
        builder.AppendLine("            _maxExpansionRatio = maxExpansionRatio;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public void Activate() => _active = true;");
        builder.AppendLine();
        builder.AppendLine("        public override bool CanRead => _inner.CanRead;");
        builder.AppendLine("        public override bool CanSeek => _inner.CanSeek;");
        builder.AppendLine("        public override bool CanWrite => false;");
        builder.AppendLine("        public override long Length => _inner.Length;");
        builder.AppendLine("        public override long Position");
        builder.AppendLine("        {");
        builder.AppendLine("            get => _inner.Position;");
        builder.AppendLine("            set => Seek(value, global::System.IO.SeekOrigin.Begin);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine(
            "        public override void Flush() { /* Read-only stream: there is nothing buffered to flush. */ }"
        );
        builder.AppendLine(
            "        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);"
        );
        builder.AppendLine("        public override int ReadByte() => _inner.ReadByte();");
        builder.AppendLine(
            "        public override global::System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, global::System.Threading.CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);"
        );
        builder.AppendLine("#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER");
        builder.AppendLine(
            "        public override int Read(global::System.Span<byte> buffer) => _inner.Read(buffer);"
        );
        builder.AppendLine(
            "        public override global::System.Threading.Tasks.ValueTask<int> ReadAsync(global::System.Memory<byte> buffer, global::System.Threading.CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);"
        );
        builder.AppendLine("#endif");
        builder.AppendLine(
            "        public override long Seek(long offset, global::System.IO.SeekOrigin origin)"
        );
        builder.AppendLine("        {");
        builder.AppendLine("            long position = _inner.Seek(offset, origin);");
        builder.AppendLine("            if (_active) ValidatePageAt(position);");
        builder.AppendLine("            return position;");
        builder.AppendLine("        }");
        builder.AppendLine(
            "        public override void SetLength(long value) => throw new global::System.NotSupportedException();"
        );
        builder.AppendLine(
            "        public override void Write(byte[] buffer, int offset, int count) => throw new global::System.NotSupportedException();"
        );
        builder.AppendLine("        protected override void Dispose(bool disposing)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            // The inner stream is owned by the caller and is deliberately not disposed."
        );
        builder.AppendLine("            base.Dispose(disposing);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private void ValidatePageAt(long offset)");
        builder.AppendLine("        {");
        builder.AppendLine("            long savedPosition = _inner.Position;");
        builder.AppendLine("            try");
        builder.AppendLine("            {");
        builder.AppendLine("                if (offset < 0 || offset >= _inner.Length) return;");
        builder.AppendLine("                _inner.Position = offset;");
        builder.AppendLine("                var reader = new CompactProtocolReader(_inner);");
        builder.AppendLine(
            "                if (!reader.TryReadPageHeader(out int pageType, out int compressedSize, out int uncompressedSize)) return;"
        );
        builder.AppendLine("                if (pageType < 0 || pageType > 3) return;");
        builder.AppendLine("                if (compressedSize <= 0 || uncompressedSize < 0)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException($\"Parquet page at offset {offset} has invalid compressed/uncompressed sizes ({compressedSize}/{uncompressedSize}).\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine("                if (uncompressedSize > _maxPageSize)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException($\"Parquet page at offset {offset} has uncompressed size {uncompressedSize} bytes, exceeding maximum allowed {_maxPageSize}.\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine(
            "                if ((long)uncompressedSize > (long)compressedSize * _maxExpansionRatio)"
        );
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new global::System.IO.InvalidDataException($\"Parquet page at offset {offset} expands from {compressedSize} to {uncompressedSize} bytes, exceeding maximum expansion ratio {_maxExpansionRatio}.\");"
        );
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine("            catch (global::System.IO.IOException)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                // A page header that cannot be read is left for the caller's read to report."
        );
        builder.AppendLine("            }");
        builder.AppendLine("            finally");
        builder.AppendLine("            {");
        builder.AppendLine("                _inner.Position = savedPosition;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private struct CompactProtocolReader");
        builder.AppendLine("        {");
        builder.AppendLine("            private readonly global::System.IO.Stream _stream;");
        builder.AppendLine();
        builder.AppendLine(
            "            public CompactProtocolReader(global::System.IO.Stream stream) { _stream = stream; }"
        );
        builder.AppendLine();
        builder.AppendLine(
            "            public bool TryReadPageHeader(out int pageType, out int compressedSize, out int uncompressedSize)"
        );
        builder.AppendLine("            {");
        builder.AppendLine("                pageType = 0;");
        builder.AppendLine("                compressedSize = 0;");
        builder.AppendLine("                uncompressedSize = 0;");
        builder.AppendLine(
            "                return TryReadRequiredField(out pageType) && TryReadRequiredField(out uncompressedSize) && TryReadRequiredField(out compressedSize);"
        );
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            private bool TryReadRequiredField(out int value)");
        builder.AppendLine("            {");
        builder.AppendLine("                value = 0;");
        builder.AppendLine("                int header = _stream.ReadByte();");
        builder.AppendLine(
            "                if (header < 0 || (header & 0x0F) != 5 || (header >> 4) != 1) return false;"
        );
        builder.AppendLine("                return TryReadZigZagInt32(out value);");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            private bool TryReadZigZagInt32(out int value)");
        builder.AppendLine("            {");
        builder.AppendLine("                value = 0;");
        builder.AppendLine("                uint raw = 0;");
        builder.AppendLine("                for (int shift = 0; shift < 35; shift += 7)");
        builder.AppendLine("                {");
        builder.AppendLine("                    int next = _stream.ReadByte();");
        builder.AppendLine("                    if (next < 0) return false;");
        builder.AppendLine("                    raw |= (uint)(next & 0x7F) << shift;");
        builder.AppendLine("                    if ((next & 0x80) == 0)");
        builder.AppendLine("                    {");
        builder.AppendLine(
            "                        value = (int)((raw >> 1) ^ (uint)-(int)(raw & 1));"
        );
        builder.AppendLine("                        return true;");
        builder.AppendLine("                    }");
        builder.AppendLine("                }");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }
}
