using System.Text;

namespace Parquet.SourceGenerator.Emitter.Components;

/// <summary>
/// Emits the private stream guard used to stop Parquet.Net before it allocates a decompression
/// buffer for a hostile page. The guard is generated into the consumer assembly because the
/// Parquet.Net compact-protocol reader is internal in both supported API generations.
/// </summary>
/// <remarks>
/// The guard fails closed. A seek that lands outside the page it last validated is treated as a
/// page header, and a header it cannot parse as one is rejected with
/// <see cref="System.IO.InvalidDataException"/> rather than waved through. The header reader is
/// deliberately stricter than Parquet.Net's: it accepts fields in any order and the long field
/// form, but it refuses a field whose compact type differs from the one Parquet.Net decodes that
/// field id as, because a header the two readers decode differently is the way to hide a size from
/// the guard. Emitted code must compile on net472 (C# 7.3), so no newer language features and no
/// Span-based stream overloads outside the existing preprocessor gate.
/// </remarks>
internal static class DecompressionGuardComponent
{
    public static void Emit(StringBuilder builder)
    {
        builder.AppendLine(
            "    private static DecompressionGuardStream CreateGuardedReadStream(global::System.IO.Stream stream, global::Parquet.SourceGenerator.ParquetSerializerOptions options) => new(stream, options.MaxDecompressedPageSize, options.MaxDecompressionExpansionRatio, options.MaxRowGroupCount);"
        );
        builder.AppendLine();
        AppendBlock(builder, GuardStreamSource);
        AppendBlock(builder, PageHeaderReaderSource);
        AppendBlock(builder, FooterScannerSource);
        builder.AppendLine("    }");
    }

    private static void AppendBlock(StringBuilder builder, string block)
    {
        foreach (string line in block.Replace("\r\n", "\n").Split('\n'))
        {
            builder.AppendLine(line.Length == 0 ? line : "    " + line);
        }
    }

    // The guard class itself. The class body is left open: Emit closes it after the nested reader.
    private const string GuardStreamSource = """
        /// <summary>
        /// Seekable read wrapper that validates each page header before Parquet.Net reads or decompresses its payload.
        /// </summary>
        private sealed class DecompressionGuardStream : global::System.IO.Stream
        {
            private readonly global::System.IO.Stream _inner;
            private readonly int _maxPageSize;
            private readonly int _maxExpansionRatio;
            private readonly int _maxRowGroupCount;
            private bool _active;

            // The page last validated: its header start (inclusive) to the end of its payload
            // (exclusive). A seek landing inside it is Parquet.Net repositioning within a page the
            // guard already allowed it to read (it moves the base stream on every payload read, and
            // skips fields inside the header), so it is not a new page header.
            private long _knownStart = -1;
            private long _knownEnd = -1;

            public DecompressionGuardStream(global::System.IO.Stream inner, int maxPageSize, int maxExpansionRatio)
                : this(inner, maxPageSize, maxExpansionRatio, int.MaxValue)
            {
            }

            public DecompressionGuardStream(global::System.IO.Stream inner, int maxPageSize, int maxExpansionRatio, int maxRowGroupCount)
            {
                _inner = inner ?? throw new global::System.ArgumentNullException(nameof(inner));
                _maxPageSize = maxPageSize;
                _maxExpansionRatio = maxExpansionRatio;
                _maxRowGroupCount = maxRowGroupCount;
            }

            public void Activate()
            {
                _active = true;
                _knownStart = -1;
                _knownEnd = -1;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => Seek(value, global::System.IO.SeekOrigin.Begin);
            }

            public override void Flush() { /* Read-only stream: there is nothing buffered to flush. */ }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, ReachNextHeader(count));
            public override int ReadByte()
            {
                ReachNextHeader(1);
                return _inner.ReadByte();
            }
            public override global::System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, global::System.Threading.CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, ReachNextHeader(count), cancellationToken);
        #if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            public override int Read(global::System.Span<byte> buffer) => _inner.Read(buffer.Slice(0, ReachNextHeader(buffer.Length)));
            public override global::System.Threading.Tasks.ValueTask<int> ReadAsync(global::System.Memory<byte> buffer, global::System.Threading.CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer.Slice(0, ReachNextHeader(buffer.Length)), cancellationToken);
        #endif
            public override long Seek(long offset, global::System.IO.SeekOrigin origin)
            {
                // Parquet.Net finds the footer with a seek to 8 bytes before the end of the stream, to
                // read its length, before this guard is active. Everything it then reads is parsed
                // into objects sized by counts inside the footer, so the footer is checked here,
                // before that read happens.
                if (origin == global::System.IO.SeekOrigin.End && offset == -8) ValidateFooter();
                long position = _inner.Seek(offset, origin);
                if (_active && (position < _knownStart || position >= _knownEnd)) ValidatePageAt(position);
                return position;
            }
            public override void SetLength(long value) => throw new global::System.NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new global::System.NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                // The inner stream is owned by the caller and is deliberately not disposed.
                _active = false;
                base.Dispose(disposing);
            }

            private void ValidateFooter()
            {
                long length = _inner.Length;
                if (length < 12) return;
                long savedPosition = _inner.Position;
                try
                {
                    // The stored footer length is a signed 32-bit integer, and Parquet.Net computes the
                    // footer's start from it in 32-bit arithmetic (wrapping for a value near int.MaxValue),
                    // so it is read and checked here rather than inferred from the seek that follows it.
                    // Single-byte reads are used, as for page headers, so this adds no block read to
                    // the footer-length region.
                    _inner.Position = length - 8;
                    int stored = 0;
                    for (int shift = 0; shift < 32; shift += 8)
                    {
                        int next = _inner.ReadByte();
                        if (next < 0) throw new global::System.IO.InvalidDataException("Parquet footer length could not be read.");
                        stored |= next << shift;
                    }
                    if (stored <= 0 || stored > length - 12)
                    {
                        throw new global::System.IO.InvalidDataException($"Parquet footer length {stored} does not fit in a stream of {length} bytes.");
                    }
                    _inner.Position = length - 8 - stored;
                    new FooterScanner(_inner, length - 8, _maxRowGroupCount).Scan();
                }
                finally
                {
                    _inner.Position = savedPosition;
                }
            }

            // Runs before every read. Parquet.Net 4.x seeks once to the start of a column chunk and then
            // reads its pages one after another, so a page header can be reached with no seek at all.
            // A read that starts where the last validated page ends is about to consume the next
            // header, so that header is validated first. A read that would run across that point is
            // shortened to stop at it (a short read is always legal), so the next read starts on it.
            private int ReachNextHeader(int count)
            {
                if (!_active || count <= 0 || _knownEnd < 0) return count;
                long position = _inner.Position;
                if (position == _knownEnd) ValidatePageAt(position);
                if (position >= _knownStart && position < _knownEnd && count > _knownEnd - position)
                {
                    return (int)(_knownEnd - position);
                }
                return count;
            }

            private void ValidatePageAt(long offset)
            {
                long length = _inner.Length;
                if (offset < 0 || offset >= length) return;
                long savedPosition = _inner.Position;
                try
                {
                    _inner.Position = offset;
                    var reader = new PageHeaderReader(_inner, offset);
                    reader.Read();
                    int pageType = reader.PageType;
                    int compressedSize = reader.CompressedSize;
                    int uncompressedSize = reader.UncompressedSize;
                    long payloadStart = _inner.Position;
                    if (pageType < 0 || pageType > 3)
                    {
                        throw new global::System.IO.InvalidDataException($"Parquet page at offset {offset} has unsupported page type {pageType}.");
                    }
                    if (compressedSize <= 0 || uncompressedSize < 0)
                    {
                        throw new global::System.IO.InvalidDataException($"Parquet page at offset {offset} has invalid compressed/uncompressed sizes ({compressedSize}/{uncompressedSize}).");
                    }
                    if (uncompressedSize > _maxPageSize)
                    {
                        throw new global::System.IO.InvalidDataException($"Parquet page at offset {offset} has uncompressed size {uncompressedSize} bytes, exceeding maximum allowed {_maxPageSize}.");
                    }
                    if ((long)uncompressedSize > (long)compressedSize * _maxExpansionRatio)
                    {
                        throw new global::System.IO.InvalidDataException($"Parquet page at offset {offset} expands from {compressedSize} to {uncompressedSize} bytes, exceeding maximum expansion ratio {_maxExpansionRatio}.");
                    }
                    if (compressedSize > length - payloadStart)
                    {
                        throw new global::System.IO.InvalidDataException($"Parquet page at offset {offset} declares {compressedSize} compressed bytes, extending beyond the end of the stream.");
                    }
                    _knownStart = offset;
                    _knownEnd = payloadStart + compressedSize;
                }
                finally
                {
                    _inner.Position = savedPosition;
                }
            }
        """;

    // The nested page header reader. It reads one Thrift compact-protocol PageHeader struct from the
    // current position, keyed on field id rather than position, and throws InvalidDataException for
    // anything it cannot account for.
    private const string PageHeaderReaderSource = """

        /// <summary>
        /// Reads the sizes out of a Thrift compact-protocol <c>PageHeader</c> with the same field-id,
        /// long-form and last-field-id rules as Parquet.Net, refusing anything it would decode differently.
        /// </summary>
        private sealed class PageHeaderReader
        {
            private const int MaxItems = 1024;
            private const int MaxSkipDepth = 4;

            // Field id -> expected compact type, indexed by id (index 0 is unused). i is i32, l is
            // i64, y is binary, f is bool, s is struct and ? is an id this reader does not know.
            // Kind 0 is PageHeader; kinds 5 to 8 are the page header structs at those field ids and
            // kind 9 is Statistics. A known id with a different compact type is refused.
            private const string PageHeaderFields = "?iiiissss";
            private const string DataPageHeaderFields = "?iiiis";
            private const string DictionaryPageHeaderFields = "?iif";
            private const string DataPageHeaderV2Fields = "?iiiiiifs";
            private const string StatisticsFields = "?yyllyyff";

            private readonly global::System.IO.Stream _stream;
            private readonly long _offset;
            private int _budget = MaxItems;
            private int _seen;

            public PageHeaderReader(global::System.IO.Stream stream, long offset)
            {
                _stream = stream;
                _offset = offset;
            }

            public int PageType { get; private set; }
            public int UncompressedSize { get; private set; }
            public int CompressedSize { get; private set; }

            public void Read()
            {
                ReadStruct(0);
                if (_seen != 7)
                {
                    throw Malformed("is missing the page type or one of its sizes");
                }
            }

            private void ReadStruct(int kind)
            {
                short fieldId = 0;
                while (true)
                {
                    int header = ReadByte();
                    if (header == 0) return;
                    if (--_budget < 0) throw Malformed("has too many fields");
                    int delta = header >> 4;
                    fieldId = delta == 0 ? unchecked((short)ReadInt32()) : unchecked((short)(fieldId + delta));
                    ReadField(kind, fieldId, header & 0x0F);
                }
            }

            private void ReadField(int kind, int id, int type)
            {
                char expected = ExpectedType(kind, id);
                if (expected == '?')
                {
                    SkipValue(type, 0);
                    return;
                }
                if (!TypeMatches(expected, type))
                {
                    throw Malformed($"has field {id} with compact type {type}, which Parquet.Net would decode differently");
                }
                switch (expected)
                {
                    case 'i':
                        RecordInt32(kind, id, ReadInt32());
                        break;
                    case 'l':
                        ReadVarint64();
                        break;
                    case 'y':
                        SkipBinary();
                        break;
                    case 's':
                        ReadStruct(kind == 0 ? id : 9);
                        break;
                    default:
                        // A boolean carries its value in the field header and has no payload.
                        break;
                }
            }

            private void RecordInt32(int kind, int id, int value)
            {
                if (kind != 0 || id > 3) return;
                int bit = 1 << (id - 1);
                if ((_seen & bit) != 0) throw Malformed($"repeats field {id}");
                _seen |= bit;
                if (id == 1) PageType = value;
                else if (id == 2) UncompressedSize = value;
                else CompressedSize = value;
            }

            private static char ExpectedType(int kind, int id)
            {
                string map;
                switch (kind)
                {
                    case 0: map = PageHeaderFields; break;
                    case 5: map = DataPageHeaderFields; break;
                    case 7: map = DictionaryPageHeaderFields; break;
                    case 8: map = DataPageHeaderV2Fields; break;
                    case 9: map = StatisticsFields; break;
                    default: return '?';
                }
                return id > 0 && id < map.Length ? map[id] : '?';
            }

            private static bool TypeMatches(char expected, int type)
            {
                switch (expected)
                {
                    case 'i': return type == 5;
                    case 'l': return type == 6;
                    case 'y': return type == 8;
                    case 's': return type == 12;
                    default: return type == 1 || type == 2;
                }
            }

            // Skips a field this reader has no schema for. Only the types Parquet.Net skips the same
            // way the Thrift specification does are accepted; anything else is refused.
            private void SkipValue(int type, int depth)
            {
                if (--_budget < 0 || depth > MaxSkipDepth) throw Malformed("is too complex to validate");
                switch (type)
                {
                    case 1:
                    case 2:
                        return;
                    case 3:
                        ReadByte();
                        return;
                    case 4:
                    case 5:
                        ReadVarint32();
                        return;
                    case 6:
                        ReadVarint64();
                        return;
                    case 8:
                        SkipBinary();
                        return;
                    case 9:
                        SkipList(depth);
                        return;
                    default:
                        throw Malformed($"has a field of unsupported compact type {type}");
                }
            }

            private void SkipList(int depth)
            {
                int header = ReadByte();
                long count = header >> 4;
                if (count == 15) count = ReadVarint32();
                int elementType = header & 0x0F;
                if (count > _budget) throw Malformed("has a list that is too long to validate");
                for (long i = 0; i < count; i++)
                {
                    if (elementType == 1 || elementType == 2)
                    {
                        // A boolean element is one byte and is not charged by SkipValue.
                        if (--_budget < 0) throw Malformed("is too complex to validate");
                        ReadByte();
                    }
                    else SkipValue(elementType, depth + 1);
                }
            }

            private void SkipBinary()
            {
                uint length = ReadVarint32();
                if (length > _stream.Length - _stream.Position) throw Malformed("is truncated");
                _stream.Position += length;
            }

            private int ReadByte()
            {
                int next = _stream.ReadByte();
                if (next < 0) throw Malformed("is truncated");
                return next;
            }

            private int ReadInt32()
            {
                uint raw = ReadVarint32();
                // The consumer's project may compile with overflow checking on, and this wraps by design.
                return unchecked((int)((raw >> 1) ^ (uint)-(int)(raw & 1)));
            }

            private uint ReadVarint32()
            {
                uint raw = 0;
                for (int shift = 0; shift < 35; shift += 7)
                {
                    int next = ReadByte();
                    raw |= (uint)(next & 0x7F) << shift;
                    if ((next & 0x80) == 0) return raw;
                }
                throw Malformed("has an overlong integer");
            }

            private void ReadVarint64()
            {
                for (int i = 0; i < 10; i++)
                {
                    if ((ReadByte() & 0x80) == 0) return;
                }
                throw Malformed("has an overlong integer");
            }

            private global::System.IO.InvalidDataException Malformed(string reason) =>
                new global::System.IO.InvalidDataException($"Parquet page header at offset {_offset} {reason}.");
        }
        """;

    // Walks the Thrift compact-protocol FileMetaData footer once, from its first byte to the end of
    // the struct, without building anything. Parquet.Net sizes the lists and byte arrays it
    // allocates from counts and lengths read out of the footer, before it reads the elements, so a
    // footer a few bytes long can ask for gigabytes. A list or binary cannot hold more elements than
    // there are bytes left in the footer, so any that claims to is refused here, and so is a row
    // group count above the configured limit.
    private const string FooterScannerSource = """

        /// <summary>
        /// Checks every list count and byte length in the footer against the bytes that remain,
        /// and the row group count against the configured maximum, before Parquet.Net parses it.
        /// </summary>
        /// <remarks>
        /// The walk follows the declared compact types. For the list fields of FileMetaData,
        /// RowGroup and ColumnMetaData, which Parquet.Net reads as lists whatever the footer
        /// declares, a different declared type is refused. A footer that mis-declares the type of
        /// some other known field could still make Parquet.Net read bytes this walk skipped; closing
        /// that needs the bounds checked inside Parquet.Net's own reader.
        /// </remarks>
        private sealed class FooterScanner
        {
            private const int MaxDepth = 32;

            // Struct kinds this walk follows by field id: FileMetaData, RowGroup, ColumnChunk and
            // ColumnMetaData. Anything else is skipped by its declared type.
            private const int FileMetaData = 0;
            private const int RowGroup = 1;
            private const int ColumnChunk = 2;
            private const int ColumnMetaData = 3;

            private readonly global::System.IO.Stream _stream;
            private readonly long _end;
            private readonly int _maxRowGroupCount;
            private long _position;

            public FooterScanner(global::System.IO.Stream stream, long end, int maxRowGroupCount)
            {
                _stream = stream;
                _end = end;
                _maxRowGroupCount = maxRowGroupCount;
                _position = stream.Position;
            }

            public void Scan() => ReadStruct(FileMetaData, 0);

            private void ReadStruct(int kind, int depth)
            {
                if (depth > MaxDepth) throw Malformed("is nested too deeply");
                short fieldId = 0;
                while (true)
                {
                    int header = ReadByte();
                    if (header == 0) return;
                    int type = header & 0x0F;
                    int delta = header >> 4;
                    fieldId = delta == 0 ? unchecked((short)ReadInt32()) : unchecked((short)(fieldId + delta));
                    ReadField(kind, fieldId, type, depth);
                }
            }

            private void ReadField(int kind, int id, int type, int depth)
            {
                int child = ChildKind(kind, id);
                int elementType = ListElementType(kind, id);
                if (elementType >= 0)
                {
                    if (type != 9) throw Malformed($"declares field {id} with compact type {type}, but it is a list");
                    ReadKnownList(kind, id, elementType, child, depth);
                }
                else if (kind == ColumnChunk && id == 3 && type != 12)
                {
                    throw Malformed($"declares field {id} with compact type {type}, but it is a struct");
                }
                else if (type == 12 && child >= 0) ReadStruct(child, depth + 1);
                else SkipValue(type, depth + 1, false);
            }

            private void ReadKnownList(int kind, int id, int expectedElementType, int child, int depth)
            {
                long count = ReadListHeader(out int elementType);
                if (kind == FileMetaData && id == 4 && count > _maxRowGroupCount)
                {
                    throw new global::System.IO.InvalidDataException($"Row group count {count} is invalid or exceeds maximum allowed {_maxRowGroupCount}.");
                }
                if (elementType != expectedElementType)
                {
                    throw Malformed($"declares a list in field {id} whose elements are not of compact type {expectedElementType}");
                }
                for (long i = 0; i < count; i++)
                {
                    if (child >= 0) ReadStruct(child, depth + 1);
                    else SkipValue(elementType, depth + 1, true);
                }
            }

            // The compact element type Parquet.Net reads a known list field's elements as, or -1 when
            // the field is not a list this walk knows. Parquet.Net decodes the elements as that type
            // whatever the list header declares, so a different declared type is refused.
            private static int ListElementType(int kind, int id)
            {
                switch (kind)
                {
                    case FileMetaData: return id == 2 || id == 4 || id == 5 || id == 7 ? 12 : -1;
                    case RowGroup: return id == 1 || id == 4 ? 12 : -1;
                    case ColumnMetaData:
                        if (id == 2) return 5;
                        if (id == 3) return 8;
                        return id == 8 || id == 13 ? 12 : -1;
                    default: return -1;
                }
            }

            // The struct kind of a field's value (or of a list's elements), or -1 when the walk has no schema for it.
            private static int ChildKind(int kind, int id)
            {
                if (kind == FileMetaData && id == 4) return RowGroup;
                if (kind == RowGroup && id == 1) return ColumnChunk;
                if (kind == ColumnChunk && id == 3) return ColumnMetaData;
                return -1;
            }

            // In a list or map a boolean is a byte of its own; in a struct field it is in the header.
            private void SkipValue(int type, int depth, bool inCollection)
            {
                if (depth > MaxDepth) throw Malformed("is nested too deeply");
                switch (type)
                {
                    case 1:
                    case 2:
                        if (inCollection) ReadByte();
                        return;
                    case 3:
                        ReadByte();
                        return;
                    case 4:
                    case 5:
                    case 6:
                        ReadVarint(10);
                        return;
                    case 7:
                        Skip(8);
                        return;
                    case 8:
                        Skip(ReadVarint(5));
                        return;
                    case 9:
                    case 10:
                        SkipList(depth);
                        return;
                    case 11:
                        SkipMap(depth);
                        return;
                    case 12:
                        ReadStruct(-1, depth + 1);
                        return;
                    default:
                        throw Malformed($"has a field of unsupported compact type {type}");
                }
            }

            private void SkipList(int depth)
            {
                long count = ReadListHeader(out int elementType);
                for (long i = 0; i < count; i++) SkipValue(elementType, depth + 1, true);
            }

            private void SkipMap(int depth)
            {
                long count = ReadVarint(5);
                if (count == 0) return;
                if (count > _end - _position) throw Malformed("declares a map longer than the footer");
                int types = ReadByte();
                for (long i = 0; i < count; i++)
                {
                    SkipValue(types >> 4, depth + 1, true);
                    SkipValue(types & 0x0F, depth + 1, true);
                }
            }

            // Every element takes at least one byte, so a count above the bytes left cannot be true.
            private long ReadListHeader(out int elementType)
            {
                int header = ReadByte();
                elementType = header & 0x0F;
                long count = header >> 4;
                if (count == 15) count = ReadVarint(5);
                if (count > _end - _position) throw Malformed($"declares a list of {count} elements in the {_end - _position} bytes that remain");
                return count;
            }

            private void Skip(long count)
            {
                if (count > _end - _position) throw Malformed($"declares {count} bytes in the {_end - _position} that remain");
                _position += count;
                _stream.Position = _position;
            }

            private int ReadByte()
            {
                if (_position >= _end) throw Malformed("is truncated");
                int next = _stream.ReadByte();
                if (next < 0) throw Malformed("is truncated");
                _position++;
                return next;
            }

            private int ReadInt32()
            {
                long raw = ReadVarint(5);
                return unchecked((int)(uint)((raw >> 1) ^ -(raw & 1)));
            }

            private long ReadVarint(int maxBytes)
            {
                long value = 0;
                for (int i = 0; i < maxBytes; i++)
                {
                    int next = ReadByte();
                    if (i < 9) value |= (long)(next & 0x7F) << (7 * i);
                    if ((next & 0x80) == 0) return value;
                }
                throw Malformed("has an overlong integer");
            }

            private static global::System.IO.InvalidDataException Malformed(string reason) =>
                new global::System.IO.InvalidDataException($"Parquet footer {reason}.");
        }
        """;
}
