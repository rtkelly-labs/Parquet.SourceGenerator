#pragma warning disable CA1844 // Stream targets netstandard2.0 without Span/Memory ReadAsync overrides across uniform PublicAPI

using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Parquet.SourceGenerator;

/// <summary>
/// Seekable read wrapper that validates each page header before Parquet.Net reads or decompresses its payload.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ParquetDecompressionGuardStream : Stream
{
    private readonly Stream _inner;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="ParquetDecompressionGuardStream"/> class.
    /// </summary>
    /// <param name="inner">The underlying readable and seekable stream.</param>
    /// <param name="maxPageSize">Maximum allowed uncompressed page size in bytes.</param>
    /// <param name="maxExpansionRatio">Maximum allowed decompression expansion ratio.</param>
    public ParquetDecompressionGuardStream(Stream inner, int maxPageSize, int maxExpansionRatio)
        : this(inner, maxPageSize, maxExpansionRatio, int.MaxValue) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ParquetDecompressionGuardStream"/> class.
    /// </summary>
    /// <param name="inner">The underlying readable and seekable stream.</param>
    /// <param name="maxPageSize">Maximum allowed uncompressed page size in bytes.</param>
    /// <param name="maxExpansionRatio">Maximum allowed decompression expansion ratio.</param>
    /// <param name="maxRowGroupCount">Maximum allowed row groups in the file metadata footer.</param>
    public ParquetDecompressionGuardStream(
        Stream inner,
        int maxPageSize,
        int maxExpansionRatio,
        int maxRowGroupCount
    )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxPageSize = maxPageSize;
        _maxExpansionRatio = maxExpansionRatio;
        _maxRowGroupCount = maxRowGroupCount;
    }

    /// <summary>
    /// Activates page validation checks on subsequent reads and seeks.
    /// </summary>
    public void Activate()
    {
        _active = true;
        _knownStart = -1;
        _knownEnd = -1;
    }

    /// <summary>
    /// Reads and validates the 4-byte footer length at the end of a Parquet stream,
    /// returning the offset in the stream where the metadata footer starts.
    /// </summary>
    /// <param name="stream">The seekable Parquet stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stream offset where the Parquet metadata footer starts.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown if the stream is too short, unseekable, or the footer length is invalid.</exception>
    public static async Task<long> GetFooterStartAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    )
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(stream);
#else
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
#endif

        if (!stream.CanSeek || stream.Length < 8)
        {
            throw new InvalidDataException(
                "Parquet stream is not seekable or is too small to contain a footer."
            );
        }

        long originalPosition = stream.Position;
        try
        {
            stream.Position = stream.Length - 8;
            var footerLengthBytes = new byte[4];
            int read = 0;
            while (read < footerLengthBytes.Length)
            {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
                int count = await stream
                    .ReadAsync(
                        MemoryExtensions.AsMemory(
                            footerLengthBytes,
                            read,
                            footerLengthBytes.Length - read
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
#else
                int count = await stream
                    .ReadAsync(
                        footerLengthBytes,
                        read,
                        footerLengthBytes.Length - read,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
#endif
                if (count == 0)
                {
                    throw new InvalidDataException("Parquet footer length could not be read.");
                }

                read += count;
            }

            long footerLength =
                footerLengthBytes[0]
                | ((long)footerLengthBytes[1] << 8)
                | ((long)footerLengthBytes[2] << 16)
                | ((long)footerLengthBytes[3] << 24);
            long footerStart = stream.Length - 8 - footerLength;
            if (footerStart < 4 || footerStart > stream.Length - 8)
            {
                throw new InvalidDataException("Parquet footer bounds are invalid.");
            }

            return footerStart;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _inner.Position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        /* Read-only stream: there is nothing buffered to flush. */
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, ReachNextHeader(count));

    /// <inheritdoc/>
    public override int ReadByte()
    {
        ReachNextHeader(1);
        return _inner.ReadByte();
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => _inner.ReadAsync(buffer, offset, ReachNextHeader(count), cancellationToken);

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        // Parquet.Net finds the footer with a seek to 8 bytes before the end of the stream, to
        // read its length, before this guard is active. Everything it then reads is parsed
        // into objects sized by counts inside the footer, so the footer is checked here,
        // before that read happens.
        if (origin == SeekOrigin.End && offset == -8)
        {
            ValidateFooter();
        }
        long position = _inner.Seek(offset, origin);
        if (_active && (position < _knownStart || position >= _knownEnd))
        {
            ValidatePageAt(position);
        }
        return position;
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // The inner stream is owned by the caller and is deliberately not disposed.
        _active = false;
        base.Dispose(disposing);
    }

    private void ValidateFooter()
    {
        long length = _inner.Length;
        if (length < 12)
        {
            return;
        }
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
                if (next < 0)
                {
                    throw new InvalidDataException("Parquet footer length could not be read.");
                }
                stored |= next << shift;
            }
            if (stored <= 0 || stored > length - 12)
            {
                throw new InvalidDataException(
                    $"Parquet footer length {stored} does not fit in a stream of {length} bytes."
                );
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
        if (!_active || count <= 0 || _knownEnd < 0)
        {
            return count;
        }
        long position = _inner.Position;
        if (position == _knownEnd)
        {
            ValidatePageAt(position);
        }
        if (position >= _knownStart && position < _knownEnd && count > _knownEnd - position)
        {
            return (int)(_knownEnd - position);
        }
        return count;
    }

    private void ValidatePageAt(long offset)
    {
        long length = _inner.Length;
        if (offset < 0 || offset >= length)
        {
            return;
        }
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
                throw new InvalidDataException(
                    $"Parquet page at offset {offset} has unsupported page type {pageType}."
                );
            }
            if (compressedSize <= 0 || uncompressedSize < 0)
            {
                throw new InvalidDataException(
                    $"Parquet page at offset {offset} has invalid compressed/uncompressed sizes ({compressedSize}/{uncompressedSize})."
                );
            }
            if (uncompressedSize > _maxPageSize)
            {
                throw new InvalidDataException(
                    $"Parquet page at offset {offset} has uncompressed size {uncompressedSize} bytes, exceeding maximum allowed {_maxPageSize}."
                );
            }
            if ((long)uncompressedSize > (long)compressedSize * _maxExpansionRatio)
            {
                throw new InvalidDataException(
                    $"Parquet page at offset {offset} expands from {compressedSize} to {uncompressedSize} bytes, exceeding maximum expansion ratio {_maxExpansionRatio}."
                );
            }
            if (compressedSize > length - payloadStart)
            {
                throw new InvalidDataException(
                    $"Parquet page at offset {offset} declares {compressedSize} compressed bytes, extending beyond the end of the stream."
                );
            }
            _knownStart = offset;
            _knownEnd = payloadStart + compressedSize;
        }
        finally
        {
            _inner.Position = savedPosition;
        }
    }

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

        private readonly Stream _stream;
        private readonly long _offset;
        private int _budget = MaxItems;
        private int _seen;

        public PageHeaderReader(Stream stream, long offset)
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
                if (header == 0)
                {
                    return;
                }
                if (--_budget < 0)
                {
                    throw Malformed("has too many fields");
                }
                int delta = header >> 4;
                fieldId =
                    delta == 0
                        ? unchecked((short)ReadInt32())
                        : unchecked((short)(fieldId + delta));
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
                throw Malformed(
                    $"has field {id} with compact type {type}, which Parquet.Net would decode differently"
                );
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
            if (kind != 0 || id > 3)
            {
                return;
            }
            int bit = 1 << (id - 1);
            if ((_seen & bit) != 0)
            {
                throw Malformed($"repeats field {id}");
            }
            _seen |= bit;
            if (id == 1)
            {
                PageType = value;
            }
            else if (id == 2)
            {
                UncompressedSize = value;
            }
            else
            {
                CompressedSize = value;
            }
        }

        private static char ExpectedType(int kind, int id)
        {
            string map;
            switch (kind)
            {
                case 0:
                    map = PageHeaderFields;
                    break;
                case 5:
                    map = DataPageHeaderFields;
                    break;
                case 7:
                    map = DictionaryPageHeaderFields;
                    break;
                case 8:
                    map = DataPageHeaderV2Fields;
                    break;
                case 9:
                    map = StatisticsFields;
                    break;
                default:
                    return '?';
            }
            return id > 0 && id < map.Length ? map[id] : '?';
        }

        private static bool TypeMatches(char expected, int type)
        {
            switch (expected)
            {
                case 'i':
                    return type == 5;
                case 'l':
                    return type == 6;
                case 'y':
                    return type == 8;
                case 's':
                    return type == 12;
                default:
                    return type == 1 || type == 2;
            }
        }

        // Skips a field this reader has no schema for. Only the types Parquet.Net skips the same
        // way the Thrift specification does are accepted; anything else is refused.
        private void SkipValue(int type, int depth)
        {
            if (--_budget < 0 || depth > MaxSkipDepth)
            {
                throw Malformed("is too complex to validate");
            }
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
            if (count == 15)
            {
                count = ReadVarint32();
            }
            int elementType = header & 0x0F;
            if (count > _budget)
            {
                throw Malformed("has a list that is too long to validate");
            }
            for (long i = 0; i < count; i++)
            {
                if (elementType == 1 || elementType == 2)
                {
                    // A boolean element is one byte and is not charged by SkipValue.
                    if (--_budget < 0)
                    {
                        throw Malformed("is too complex to validate");
                    }
                    ReadByte();
                }
                else
                {
                    SkipValue(elementType, depth + 1);
                }
            }
        }

        private void SkipBinary()
        {
            uint length = ReadVarint32();
            if (length > _stream.Length - _stream.Position)
            {
                throw Malformed("is truncated");
            }
            _stream.Position += length;
        }

        private int ReadByte()
        {
            int next = _stream.ReadByte();
            if (next < 0)
            {
                throw Malformed("is truncated");
            }
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
                if ((next & 0x80) == 0)
                {
                    return raw;
                }
            }
            throw Malformed("has an overlong integer");
        }

        private void ReadVarint64()
        {
            for (int i = 0; i < 10; i++)
            {
                if ((ReadByte() & 0x80) == 0)
                {
                    return;
                }
            }
            throw Malformed("has an overlong integer");
        }

        private InvalidDataException Malformed(string reason) =>
            new InvalidDataException($"Parquet page header at offset {_offset} {reason}.");
    }

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

        private readonly Stream _stream;
        private readonly long _end;
        private readonly int _maxRowGroupCount;
        private long _position;

        public FooterScanner(Stream stream, long end, int maxRowGroupCount)
        {
            _stream = stream;
            _end = end;
            _maxRowGroupCount = maxRowGroupCount;
            _position = stream.Position;
        }

        public void Scan() => ReadStruct(FileMetaData, 0);

        private void ReadStruct(int kind, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Malformed("is nested too deeply");
            }
            short fieldId = 0;
            while (true)
            {
                int header = ReadByte();
                if (header == 0)
                {
                    return;
                }
                int type = header & 0x0F;
                int delta = header >> 4;
                fieldId =
                    delta == 0
                        ? unchecked((short)ReadInt32())
                        : unchecked((short)(fieldId + delta));
                ReadField(kind, fieldId, type, depth);
            }
        }

        private void ReadField(int kind, int id, int type, int depth)
        {
            int child = ChildKind(kind, id);
            int elementType = ListElementType(kind, id);
            if (elementType >= 0)
            {
                if (type != 9)
                {
                    throw Malformed(
                        $"declares field {id} with compact type {type}, but it is a list"
                    );
                }
                ReadKnownList(kind, id, elementType, child, depth);
            }
            else if (kind == ColumnChunk && id == 3 && type != 12)
            {
                throw Malformed(
                    $"declares field {id} with compact type {type}, but it is a struct"
                );
            }
            else if (type == 12 && child >= 0)
            {
                ReadStruct(child, depth + 1);
            }
            else
            {
                SkipValue(type, depth + 1, false);
            }
        }

        private void ReadKnownList(int kind, int id, int expectedElementType, int child, int depth)
        {
            long count = ReadListHeader(out int elementType);
            if (kind == FileMetaData && id == 4 && count > _maxRowGroupCount)
            {
                throw new InvalidDataException(
                    $"Row group count {count} is invalid or exceeds maximum allowed {_maxRowGroupCount}."
                );
            }
            if (elementType != expectedElementType)
            {
                throw Malformed(
                    $"declares a list in field {id} whose elements are not of compact type {expectedElementType}"
                );
            }
            for (long i = 0; i < count; i++)
            {
                if (child >= 0)
                {
                    ReadStruct(child, depth + 1);
                }
                else
                {
                    SkipValue(elementType, depth + 1, true);
                }
            }
        }

        // The compact element type Parquet.Net reads a known list field's elements as, or -1 when
        // the field is not a list this walk knows. Parquet.Net decodes the elements as that type
        // whatever the list header declares, so a different declared type is refused.
        private static int ListElementType(int kind, int id)
        {
            switch (kind)
            {
                case FileMetaData:
                    return id == 2 || id == 4 || id == 5 || id == 7 ? 12 : -1;
                case RowGroup:
                    return id == 1 || id == 4 ? 12 : -1;
                case ColumnMetaData:
                    if (id == 2)
                    {
                        return 5;
                    }
                    if (id == 3)
                    {
                        return 8;
                    }
                    return id == 8 || id == 13 ? 12 : -1;
                default:
                    return -1;
            }
        }

        // The struct kind of a field's value (or of a list's elements), or -1 when the walk has no schema for it.
        private static int ChildKind(int kind, int id)
        {
            if (kind == FileMetaData && id == 4)
            {
                return RowGroup;
            }
            if (kind == RowGroup && id == 1)
            {
                return ColumnChunk;
            }
            if (kind == ColumnChunk && id == 3)
            {
                return ColumnMetaData;
            }
            return -1;
        }

        // In a list or map a boolean is a byte of its own; in a struct field it is in the header.
        private void SkipValue(int type, int depth, bool inCollection)
        {
            if (depth > MaxDepth)
            {
                throw Malformed("is nested too deeply");
            }
            switch (type)
            {
                case 1:
                case 2:
                    if (inCollection)
                    {
                        ReadByte();
                    }
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
            for (long i = 0; i < count; i++)
            {
                SkipValue(elementType, depth + 1, true);
            }
        }

        private void SkipMap(int depth)
        {
            long count = ReadVarint(5);
            if (count == 0)
            {
                return;
            }
            if (count > _end - _position)
            {
                throw Malformed("declares a map longer than the footer");
            }
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
            if (count == 15)
            {
                count = ReadVarint(5);
            }
            if (count > _end - _position)
            {
                throw Malformed(
                    $"declares a list of {count} elements in the {_end - _position} bytes that remain"
                );
            }
            return count;
        }

        private void Skip(long count)
        {
            if (count > _end - _position)
            {
                throw Malformed($"declares {count} bytes in the {_end - _position} that remain");
            }
            _position += count;
            _stream.Position = _position;
        }

        private int ReadByte()
        {
            if (_position >= _end)
            {
                throw Malformed("is truncated");
            }
            int next = _stream.ReadByte();
            if (next < 0)
            {
                throw Malformed("is truncated");
            }
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
                if (i < 9)
                {
                    value |= (long)(next & 0x7F) << (7 * i);
                }
                if ((next & 0x80) == 0)
                {
                    return value;
                }
            }
            throw Malformed("has an overlong integer");
        }

        private static InvalidDataException Malformed(string reason) =>
            new InvalidDataException($"Parquet footer {reason}.");
    }
}
