using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;

namespace Parquet.SourceGenerator.Tests.PropertyBased;

/// <summary>One row group as the reference engine sees it.</summary>
public sealed record EngineRowGroup(long RowCount, IReadOnlyDictionary<string, object?[]> Columns);

/// <summary>A whole file as the reference engine sees it.</summary>
public sealed record EngineFile(
    IReadOnlyList<EngineRowGroup> RowGroups,
    IReadOnlyList<string> FieldNames
)
{
    /// <summary>Gets the file's row count, summed across row groups.</summary>
    public long TotalRows => RowGroups.Sum(g => g.RowCount);

    /// <summary>Gets one column's values in row order across the whole file.</summary>
    public IReadOnlyList<object?> Column(string name) =>
        RowGroups.SelectMany(g => g.Columns[name]).ToList();
}

/// <summary>One column handed to the reference writer: a field plus its canonical values.</summary>
public sealed record ReferenceColumn(DataField Field, IReadOnlyList<object?> Values)
{
    /// <summary>Projects a fuzz column over the given rows.</summary>
    public static ReferenceColumn FromFuzz(FuzzColumn column, IReadOnlyList<FuzzWideRecord> rows) =>
        new(column.Field, rows.Select(column.NormalizedValue).ToList());
}

/// <summary>
/// A hand-written reference implementation of Parquet reading and writing over Parquet.Net, used as
/// the oracle the generated code is compared against.
/// </summary>
/// <remarks>
/// Parquet.Net 6 removed the untyped <c>DataColumn</c> API, so a genuinely separate in-process
/// codec no longer exists; what is left is a second, independently written client of the same
/// library. That still buys most of what the oracle is for. Everything the generator actually
/// decides — column ordering and index resolution, definition-level and null handling, buffer
/// slicing across row groups, the physical type chosen for each logical type, timestamp and
/// decimal unit selection, string deduplication — is decided again here, by hand, and the two have
/// to agree. What it does not independently verify is Parquet.Net's own encoders and decoders; that
/// is what the pinned PyArrow fixtures are for, and it is the reason this oracle complements rather
/// than replaces them.
/// <para>
/// It also takes the opposite branch wherever the library offers one: strings and binary go through
/// the collection-based <c>WriteAsync</c> helpers rather than the <c>ReadOnlyMemory</c> path the
/// generator emits, so an error in either path shows up as a disagreement.
/// </para>
/// </remarks>
public static class IndependentParquetEngine
{
    /// <summary>Reads a Parquet file with the hand-written reference reader.</summary>
    public static async Task<EngineFile> ReadAsync(
        ReadOnlyMemory<byte> bytes,
        IReadOnlyList<FuzzColumn> columns,
        CancellationToken cancellationToken = default
    )
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(
            stream,
            cancellationToken: cancellationToken
        );

        DataField[] fileFields = reader.Schema.DataFields;
        var byName = fileFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var groups = new List<EngineRowGroup>(reader.RowGroupCount);

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using ParquetRowGroupReader groupReader = reader.OpenRowGroupReader(g);
            int rowCount = (int)groupReader.RowCount;
            var values = new Dictionary<string, object?[]>(StringComparer.Ordinal);

            foreach (FuzzColumn column in columns)
            {
                if (!byName.TryGetValue(column.Name, out DataField? field))
                {
                    continue;
                }

                object?[] raw = await ColumnCodecs
                    .For(field)
                    .ReadAsync(groupReader, field, rowCount, cancellationToken);

                var canonical = new object?[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                {
                    canonical[i] = column.Normalize(
                        FuzzCoercion.ToCanonical(raw[i], column.CanonicalType)
                    );
                }

                values[column.Name] = canonical;
            }

            groups.Add(new EngineRowGroup(groupReader.RowCount, values));
        }

        return new EngineFile(groups, fileFields.Select(f => f.Name).ToList());
    }

    /// <summary>
    /// Writes a Parquet file with the hand-written reference writer, with an arbitrary subset and
    /// ordering of fields. This is what lets the suite point the generated reader at files it did
    /// not produce.
    /// </summary>
    public static Task<byte[]> WriteAsync(
        IReadOnlyList<FuzzWideRecord> rows,
        IReadOnlyList<FuzzColumn> columns,
        int rowGroupSize,
        ParquetCompressionMethod compression,
        CancellationToken cancellationToken = default
    ) =>
        WriteColumnsAsync(
            columns.Select(c => ReferenceColumn.FromFuzz(c, rows)).ToList(),
            rows.Count,
            rowGroupSize,
            compression,
            cancellationToken
        );

    /// <summary>Writes arbitrary reference columns, including deliberately mis-declared ones.</summary>
    public static async Task<byte[]> WriteColumnsAsync(
        IReadOnlyList<ReferenceColumn> columns,
        int rowCount,
        int rowGroupSize,
        ParquetCompressionMethod compression,
        CancellationToken cancellationToken = default
    )
    {
        var schema = new ParquetSchema(columns.Select(c => (Field)c.Field).ToList());
        var options = new ParquetOptions { CompressionMethod = ToFormat(compression) };

        using var stream = new MemoryStream();
        await using (
            ParquetWriter writer = await ParquetWriter.CreateAsync(
                schema,
                stream,
                options,
                cancellationToken: cancellationToken
            )
        )
        {
            int written = 0;
            int size = Math.Max(1, rowGroupSize);
            do
            {
                int take = Math.Min(size, rowCount - written);
                using ParquetRowGroupWriter groupWriter = writer.CreateRowGroup();
                foreach (ReferenceColumn column in columns)
                {
                    await ColumnCodecs
                        .For(column.Field)
                        .WriteAsync(
                            groupWriter,
                            column.Field,
                            column.Values.Skip(written).Take(take).ToList(),
                            cancellationToken
                        );
                }

                written += take;
            } while (written < rowCount);
        }

        return stream.ToArray();
    }

    /// <summary>Maps the generator's compression enum onto Parquet.Net's.</summary>
    public static CompressionMethod ToFormat(ParquetCompressionMethod compression) =>
        compression switch
        {
            ParquetCompressionMethod.None => CompressionMethod.None,
            ParquetCompressionMethod.Gzip => CompressionMethod.Gzip,
            ParquetCompressionMethod.Lz4 => CompressionMethod.LZ4,
            ParquetCompressionMethod.Brotli => CompressionMethod.Brotli,
            ParquetCompressionMethod.Zstd => CompressionMethod.Zstd,
            _ => CompressionMethod.Snappy,
        };
}

/// <summary>Reads and writes one physical column type through Parquet.Net's typed API.</summary>
public interface IColumnCodec
{
    Task<object?[]> ReadAsync(
        ParquetRowGroupReader reader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken
    );

    Task WriteAsync(
        ParquetRowGroupWriter writer,
        DataField field,
        IReadOnlyList<object?> values,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The physical-type dispatch table for the reference engine.
/// </summary>
/// <remarks>
/// Keyed on <see cref="DataField.ClrType"/>, which is Parquet.Net's own answer to "what does this
/// column hold on the wire" — a <c>TIME_MILLIS</c> field says <see cref="int"/>, a UTF8 field says
/// <c>ReadOnlyMemory&lt;char&gt;</c>. Deriving the dispatch from the field rather than from the
/// model keeps the oracle honest: it encodes what the file claims, not what the generator meant.
/// </remarks>
public static class ColumnCodecs
{
    private static readonly Dictionary<Type, IColumnCodec> Registry = new()
    {
        [typeof(bool)] = new ValueCodec<bool>(),
        [typeof(sbyte)] = new ValueCodec<sbyte>(),
        [typeof(byte)] = new ValueCodec<byte>(),
        [typeof(short)] = new ValueCodec<short>(),
        [typeof(ushort)] = new ValueCodec<ushort>(),
        [typeof(int)] = new ValueCodec<int>(),
        [typeof(uint)] = new ValueCodec<uint>(),
        [typeof(long)] = new ValueCodec<long>(),
        [typeof(ulong)] = new ValueCodec<ulong>(),
        [typeof(float)] = new ValueCodec<float>(),
        [typeof(double)] = new ValueCodec<double>(),
        [typeof(decimal)] = new ValueCodec<decimal>(),
        [typeof(DateTime)] = new ValueCodec<DateTime>(),
        [typeof(TimeSpan)] = new ValueCodec<TimeSpan>(),
        [typeof(Guid)] = new ValueCodec<Guid>(),
        [typeof(string)] = new StringCodec(),
        [typeof(ReadOnlyMemory<char>)] = new StringCodec(),
        [typeof(byte[])] = new BinaryCodec(),
        [typeof(ReadOnlyMemory<byte>)] = new BinaryCodec(),
    };

    /// <summary>Gets the codec for a field's physical type.</summary>
    public static IColumnCodec For(DataField field) =>
        Registry.TryGetValue(field.ClrType, out IColumnCodec? codec)
            ? codec
            : throw new NotSupportedException(
                $"The reference engine has no codec for column '{field.Name}' of CLR type "
                    + $"{field.ClrType}. Add one to {nameof(ColumnCodecs)} when the supported "
                    + "envelope grows."
            );
}

/// <summary>Codec for a value-typed column, nullable or not.</summary>
public sealed class ValueCodec<T> : IColumnCodec
    where T : struct
{
    public async Task<object?[]> ReadAsync(
        ParquetRowGroupReader reader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken
    )
    {
        if (field.IsNullable)
        {
            var nullable = new T?[rowCount];
            await reader.ReadAsync<T>(
                field,
                new Memory<T?>(nullable),
                cancellationToken: cancellationToken
            );
            return nullable.Select(v => v.HasValue ? (object?)v.Value : null).ToArray();
        }

        var buffer = new T[rowCount];
        await reader.ReadAsync<T>(
            field,
            new Memory<T>(buffer),
            cancellationToken: cancellationToken
        );
        return buffer.Select(v => (object?)v).ToArray();
    }

    public async Task WriteAsync(
        ParquetRowGroupWriter writer,
        DataField field,
        IReadOnlyList<object?> values,
        CancellationToken cancellationToken
    )
    {
        if (field.IsNullable)
        {
            var nullable = new T?[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                nullable[i] = (T?)FuzzCoercion.ToEngine(values[i], typeof(T));
            }

            await writer.WriteAsync<T>(
                field,
                new ReadOnlyMemory<T?>(nullable),
                cancellationToken: cancellationToken
            );
            return;
        }

        var buffer = new T[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            buffer[i] =
                (T?)FuzzCoercion.ToEngine(values[i], typeof(T))
                ?? throw new InvalidOperationException(
                    $"Column '{field.Name}' is required but row {i.ToString(CultureInfo.InvariantCulture)} is null."
                );
        }

        await writer.WriteAsync<T>(
            field,
            new ReadOnlyMemory<T>(buffer),
            cancellationToken: cancellationToken
        );
    }
}

/// <summary>Codec for a UTF8 column.</summary>
public sealed class StringCodec : IColumnCodec
{
    public async Task<object?[]> ReadAsync(
        ParquetRowGroupReader reader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken
    )
    {
        var buffer = new string?[rowCount];
        await reader.ReadAsync(
            field,
            new Memory<string?>(buffer),
            cancellationToken: cancellationToken
        );
        return buffer.Select(v => (object?)v).ToArray();
    }

    public async Task WriteAsync(
        ParquetRowGroupWriter writer,
        DataField field,
        IReadOnlyList<object?> values,
        CancellationToken cancellationToken
    )
    {
        // The collection overload, deliberately: the generated writer takes the
        // ReadOnlyMemory<char> path, so this exercises the other one.
        string?[] strings = values.Select(v => (string?)v).ToArray();
        await writer.WriteAsync(field, strings!);
    }
}

/// <summary>Codec for a binary column.</summary>
public sealed class BinaryCodec : IColumnCodec
{
    public async Task<object?[]> ReadAsync(
        ParquetRowGroupReader reader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[]?[rowCount];
        await reader.ReadAsync(
            field,
            new Memory<byte[]?>(buffer),
            cancellationToken: cancellationToken
        );
        return buffer.Select(v => (object?)v).ToArray();
    }

    public async Task WriteAsync(
        ParquetRowGroupWriter writer,
        DataField field,
        IReadOnlyList<object?> values,
        CancellationToken cancellationToken
    )
    {
        byte[]?[] blobs = values.Select(v => (byte[]?)v).ToArray();
        await writer.WriteAsync(field, blobs!);
    }
}

/// <summary>
/// Converts between the comparison-canonical form of a value and the form Parquet.Net's typed
/// column API uses.
/// </summary>
/// <remarks>
/// The two disagree in a handful of places — a TIME_MILLIS column is an <see cref="int"/> on the
/// wire but a <see cref="TimeSpan"/> to the model, an enum is its underlying integer — and the
/// conversions live here rather than in each column definition so that adding a column does not
/// mean rediscovering them.
/// </remarks>
public static class FuzzCoercion
{
    private const long TicksPerMicrosecond = 10L;

    /// <summary>Converts a canonical value into what the Parquet.Net writer expects.</summary>
    public static object? ToEngine(object? canonical, Type engineType)
    {
        if (canonical is null)
        {
            return null;
        }

        Type target = Nullable.GetUnderlyingType(engineType) ?? engineType;
        if (target.IsInstanceOfType(canonical))
        {
            return canonical;
        }

        if (canonical is TimeSpan span)
        {
            if (target == typeof(int))
            {
                return (int)(span.Ticks / TimeSpan.TicksPerMillisecond);
            }

            if (target == typeof(long))
            {
                return span.Ticks / TicksPerMicrosecond;
            }
        }

        if (canonical is DateTime dateTime && target == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        }

        return Convert.ChangeType(canonical, target, CultureInfo.InvariantCulture);
    }

    /// <summary>Converts a value the Parquet.Net reader produced into canonical form.</summary>
    public static object? ToCanonical(object? engineValue, Type canonicalType)
    {
        if (engineValue is null)
        {
            return null;
        }

        if (canonicalType.IsInstanceOfType(engineValue))
        {
            return engineValue;
        }

        if (canonicalType == typeof(TimeSpan))
        {
            return engineValue switch
            {
                int millis => TimeSpan.FromTicks(millis * TimeSpan.TicksPerMillisecond),
                long micros => TimeSpan.FromTicks(micros * TicksPerMicrosecond),
                _ => Convert.ChangeType(engineValue, canonicalType, CultureInfo.InvariantCulture),
            };
        }

        if (canonicalType == typeof(DateTime) && engineValue is DateTimeOffset offset)
        {
            return offset.UtcDateTime;
        }

        if (canonicalType == typeof(byte[]))
        {
            return engineValue switch
            {
                ReadOnlyMemory<byte> memory => memory.ToArray(),
                _ => engineValue,
            };
        }

        if (canonicalType == typeof(string) && engineValue is ReadOnlyMemory<char> chars)
        {
            return chars.ToString();
        }

        return Convert.ChangeType(engineValue, canonicalType, CultureInfo.InvariantCulture);
    }
}
