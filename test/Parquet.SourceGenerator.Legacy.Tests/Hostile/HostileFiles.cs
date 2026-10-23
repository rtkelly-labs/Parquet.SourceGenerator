using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using FileMetaData = Parquet.Meta.FileMetaData;

namespace Parquet.SourceGenerator.Legacy.Tests.Hostile;

/// <summary>
/// Builds hostile Parquet bytes from valid ones: footer rewrites through Parquet.Net 4.25's own
/// compact-protocol writer (so the result parses exactly as a real file would), and the
/// deterministic structural corruptions of the modern <c>CorruptionCatalog</c>.
/// </summary>
internal static class HostileFiles
{
    public static async Task<byte[]> WriteAsync(
        IReadOnlyList<HostileRow> rows,
        ParquetSerializerOptions? options = null
    )
    {
        using var stream = new MemoryStream();
        await rows.WriteParquetAsync(stream, options);
        return stream.ToArray();
    }

    public static List<HostileRow> Rows(int count) =>
        Enumerable
            .Range(0, count)
            .Select(i => new HostileRow { Id = i, Name = "row " + i })
            .ToList();

    public static int FooterStart(byte[] bytes) =>
        bytes.Length - 8 - BitConverter.ToInt32(bytes, bytes.Length - 8);

    /// <summary>
    /// Rewrites the footer. The mutation receives the parsed metadata and the footer's file offset.
    /// </summary>
    public static async Task<byte[]> RewriteFooterAsync(
        byte[] bytes,
        Action<FileMetaData, int> mutate
    )
    {
        int footerStart = FooterStart(bytes);

        FileMetaData metadata;
        using (var input = new MemoryStream(bytes, writable: false))
        using (ParquetReader reader = await ParquetReader.CreateAsync(input))
        {
            metadata = reader.Metadata!;
        }

        mutate(metadata, footerStart);

        using var footer = new MemoryStream();
        System.Type writerType = typeof(ParquetReader).Assembly.GetType(
            "Parquet.Meta.Proto.ThriftCompactProtocolWriter",
            throwOnError: true
        )!;
        object writer = Activator.CreateInstance(
            writerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { footer },
            culture: CultureInfo.InvariantCulture
        )!;
        MethodInfo write = typeof(FileMetaData).GetMethod(
            "Write",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        )!;
        write.Invoke(metadata, new[] { writer });

        byte[] result = new byte[footerStart + footer.Length + 8];
        Buffer.BlockCopy(bytes, 0, result, 0, footerStart);
        Buffer.BlockCopy(footer.ToArray(), 0, result, footerStart, (int)footer.Length);
        Buffer.BlockCopy(
            BitConverter.GetBytes((int)footer.Length),
            0,
            result,
            footerStart + (int)footer.Length,
            4
        );
        Buffer.BlockCopy(Magic, 0, result, result.Length - 4, 4);
        return result;
    }

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PAR1");

    /// <summary>The structural corruptions. Each must be rejected: none can be read correctly.</summary>
    public static readonly IReadOnlyDictionary<string, Func<byte[], byte[]>> AlwaysRejected =
        new Dictionary<string, Func<byte[], byte[]>>(StringComparer.Ordinal)
        {
            ["empty"] = _ => [],
            ["magic-only"] = _ => (byte[])Magic.Clone(),
            ["truncate-half"] = bytes => Take(bytes, bytes.Length / 2),
            ["truncate-one-byte"] = bytes => Take(bytes, bytes.Length - 1),
            ["truncate-footer-magic"] = bytes => Take(bytes, bytes.Length - 4),
            ["truncate-footer-length"] = bytes => Take(bytes, bytes.Length - 8),
            ["clobber-footer-magic"] = bytes =>
                Patch(bytes, bytes.Length - 4, Encoding.ASCII.GetBytes("XXXX")),
            ["footer-length-zero"] = bytes =>
                Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(0)),
            ["footer-length-huge"] = bytes =>
                Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(int.MaxValue)),
            ["footer-length-negative"] = bytes =>
                Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(-1)),
            ["footer-length-overshoots-file"] = bytes =>
                Patch(bytes, bytes.Length - 8, BitConverter.GetBytes(bytes.Length + 1024)),
            ["header-magic-in-footer-position"] = bytes =>
                Patch(bytes, bytes.Length - 8, Encoding.ASCII.GetBytes("PAR1PAR1")),
        };

    /// <summary>
    /// Corruptions that may happen to miss everything the reader consults: each must be rejected
    /// or read back exactly, never read back changed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<byte[], byte[]>> RejectedOrExact =
        new Dictionary<string, Func<byte[], byte[]>>(StringComparer.Ordinal)
        {
            ["clobber-header-magic"] = bytes => Patch(bytes, 0, Encoding.ASCII.GetBytes("NOPE")),
            ["footer-metadata-zeroed"] = ZeroFooterMetadata,
            ["footer-metadata-shifted"] = bytes =>
                Patch(bytes, ClampedFooterStart(bytes), new byte[] { 0xFF, 0xFF }),
            ["data-region-hole"] = bytes =>
                Patch(bytes, 8, new byte[Math.Min(512, bytes.Length / 4)]),
        };

    private static byte[] Take(byte[] bytes, int length)
    {
        byte[] copy = new byte[Math.Max(0, length)];
        Buffer.BlockCopy(bytes, 0, copy, 0, copy.Length);
        return copy;
    }

    private static byte[] Patch(byte[] bytes, int offset, byte[] replacement)
    {
        byte[] copy = (byte[])bytes.Clone();
        if (offset < 0 || offset >= copy.Length)
        {
            return copy;
        }

        Buffer.BlockCopy(
            replacement,
            0,
            copy,
            offset,
            Math.Min(replacement.Length, copy.Length - offset)
        );
        return copy;
    }

    private static int ClampedFooterStart(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return 0;
        }

        int start = FooterStart(bytes);
        return start < 4 || start >= bytes.Length ? 4 : start;
    }

    private static byte[] ZeroFooterMetadata(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        int start = ClampedFooterStart(copy);
        int end = Math.Max(start, copy.Length - 8);
        Array.Clear(copy, start, end - start);
        return copy;
    }
}
