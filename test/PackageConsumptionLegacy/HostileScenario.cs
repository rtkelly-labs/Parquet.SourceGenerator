using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Parquet;
using Parquet.Meta;
using Parquet.SourceGenerator;

namespace PackageConsumptionLegacy;

/// <summary>
/// Reads files whose footer lies about row and value counts through the legacy read path on a real
/// Parquet.Net 4.25 (#362). Each lie used to surface as the wrong exception type, or as no error at
/// all: a row count past <c>int.MaxValue</c> wrapped negative and threw from the array allocation, a
/// count of exactly 2^32 truncated to zero and returned an empty array without complaint, and a row
/// count larger than the column's values indexed past the end of the column. The documented contract
/// is that hostile input throws <see cref="InvalidDataException"/>.
/// </summary>
internal static class HostileScenario
{
    // A plain class rather than a record: net472 has no IsExternalInit.
    private sealed class Case
    {
        public Case(
            string name,
            Action<FileMetaData> mutate,
            ParquetSerializerOptions? options,
            string expectedMessage
        )
        {
            Name = name;
            Mutate = mutate;
            Options = options;
            ExpectedMessage = expectedMessage;
        }

        public string Name { get; }

        public Action<FileMetaData> Mutate { get; }

        public ParquetSerializerOptions? Options { get; }

        public string ExpectedMessage { get; }
    }

    public static async Task<bool> RunAsync()
    {
        var rows = new List<Measurement>
        {
            new()
            {
                Id = 1,
                Val = 1.5,
                Label = "one",
            },
            new()
            {
                Id = 2,
                Val = 2.5,
                Label = "two",
            },
        };
        using var written = new MemoryStream();
        await rows.WriteParquetAsync(written);
        byte[] valid = written.ToArray();

        var cases = new[]
        {
            new Case(
                "a row count past int.MaxValue",
                metadata => metadata.RowGroups[0].NumRows = 1L << 31,
                null,
                "Row group 0 row count 2147483648 is invalid or exceeds maximum allowed"
            ),
            new Case(
                "a row count of exactly 2^32, which truncates to zero",
                metadata => metadata.RowGroups[0].NumRows = 1L << 32,
                null,
                "Row group 0 row count 4294967296 is invalid or exceeds maximum allowed"
            ),
            new Case(
                "a negative row count",
                metadata => metadata.RowGroups[0].NumRows = -1,
                null,
                "Row group 0 row count -1 is invalid or exceeds maximum allowed"
            ),
            new Case(
                "a row count above the allocation limit",
                metadata => metadata.RowGroups[0].NumRows = 500,
                new ParquetSerializerOptions { MaxAllocationValues = 100 },
                "Row group 0 row count 500 is invalid or exceeds maximum allowed 100"
            ),
            new Case(
                "a row count larger than the column it describes",
                metadata => metadata.RowGroups[0].NumRows = 5_000,
                null,
                "supplied 2 values for a row group declaring 5000 rows"
            ),
            new Case(
                "a column value count above the allocation limit",
                metadata => metadata.RowGroups[0].Columns[0].MetaData!.NumValues = 100_000,
                new ParquetSerializerOptions { MaxAllocationValues = 1_000 },
                "NumValues (100000) is invalid or exceeds maximum allowed 1000"
            ),
        };

        // Every case is run so a failure report lists all of them, not only the first.
        bool allRejected = true;
        foreach (Case hostile in cases)
        {
            byte[] bytes = await RewriteFooterAsync(valid, hostile.Mutate);
            allRejected &= await IsRejectedAsync(hostile, bytes);
        }

        if (!allRejected)
        {
            return false;
        }

        // The same file reads when the footer is left alone, so a rejection above is the lie.
        using var control = new MemoryStream(valid, writable: false);
        Measurement[] read = await MeasurementParquetLegacyExtensions.ReadParquetArrayAsync(
            control
        );
        if (read.Length != rows.Count)
        {
            Console.Error.WriteLine("FAILED: the unmodified control file did not read back.");
            return false;
        }

        return true;
    }

    private static async Task<bool> IsRejectedAsync(Case hostile, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            Measurement[] read = await MeasurementParquetLegacyExtensions.ReadParquetArrayAsync(
                stream,
                hostile.Options
            );
            Console.Error.WriteLine(
                $"FAILED: {hostile.Name} was read without an error ({read.Length} rows)."
            );
            return false;
        }
        catch (InvalidDataException exception)
        {
            if (exception.Message.Contains(hostile.ExpectedMessage, StringComparison.Ordinal))
            {
                return true;
            }

            Console.Error.WriteLine(
                $"FAILED: {hostile.Name} raised the wrong message: {exception.Message}"
            );
            return false;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"FAILED: {hostile.Name} raised {exception.GetType().Name}, not InvalidDataException: {exception.Message}"
            );
            return false;
        }
    }

    /// <summary>
    /// Rewrites the footer through Parquet.Net's own (internal) compact-protocol writer, so the
    /// result is a file Parquet.Net parses exactly as it would any other.
    /// </summary>
    private static async Task<byte[]> RewriteFooterAsync(byte[] bytes, Action<FileMetaData> mutate)
    {
        int footerLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - footerLength;

        FileMetaData metadata;
        using (var input = new MemoryStream(bytes, writable: false))
        using (ParquetReader reader = await ParquetReader.CreateAsync(input))
        {
            metadata = reader.Metadata!;
        }

        mutate(metadata);

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
        write.Invoke(metadata, new object[] { writer });

        byte[] magic = System.Text.Encoding.ASCII.GetBytes("PAR1");
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
        Buffer.BlockCopy(magic, 0, result, result.Length - 4, 4);
        return result;
    }
}
