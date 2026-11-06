using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Parquet.SourceGenerator.Benchmarks;

/// <summary>
/// Issue #137 — direct columnar hand-off. Measures what the hand-off actually buys on the wide
/// analytical schema docs/12 uses (<see cref="BenchmarkTpchLineItem"/>, 16 columns, 11 nullable
/// value columns + 5 nullable string columns).
/// </summary>
/// <remarks>
/// <para>
/// Three measurements, deliberately separated so the columnar saving is not confused with the
/// cost of writing Parquet:
/// </para>
/// <list type="bullet">
/// <item><c>RowOrientedWriteEndToEnd</c> — the production path: rent 27 pooled buffers, transpose
/// the row collection into them, encode, compress, write.</item>
/// <item><c>ColumnarWriteEndToEnd</c> — the same file from buffers the caller already owns: no
/// rentals, no transpose, straight into the encoder.</item>
/// <item><c>TransposeOnly</c> — the extraction pass on its own, no Parquet call. This is the work
/// the columnar path deletes, isolated from encode/compress/I-O.</item>
/// </list>
/// <para>
/// The batch is built once in <c>GlobalSetup</c>: a caller with genuinely columnar data already
/// holds these buffers, so building them is not part of the measured path — that is the premise
/// of the whole API. <c>TransposeOnly</c> is what such a caller would otherwise have been forced
/// to pay.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[InProcess]
[WarmupCount(3)]
[IterationCount(15)]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ColumnarHandoffBenchmark
{
    private List<BenchmarkTpchLineItem> _rows = null!;
    private BenchmarkTpchLineItemColumnarBatch _batch;

    [Params(10_000, 50_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _rows = BuildRows(Count);
        _batch = Transpose(_rows);
    }

    private static List<BenchmarkTpchLineItem> BuildRows(int count)
    {
        var baseDate = new DateTime(1996, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<BenchmarkTpchLineItem>(count);
        for (int i = 0; i < count; i++)
        {
            // ~1 in 16 nulls per nullable column: enough that the definition-level lane is real
            // work, sparse enough that the packed lane stays near full length.
            bool sparseNull = i % 16 == 0;
            rows.Add(
                new BenchmarkTpchLineItem
                {
                    OrderKey = i,
                    PartKey = sparseNull ? null : i * 3L,
                    SuppKey = i * 7L,
                    LineNumber = i % 7,
                    Quantity = sparseNull ? null : i % 50 + 1m,
                    ExtendedPrice = i * 1.25m,
                    Discount = i % 10 * 0.01m,
                    Tax = sparseNull ? null : 0.08m,
                    ReturnFlag = i % 2 == 0 ? "A" : "N",
                    LineStatus = "F",
                    ShipDate = baseDate.AddDays(i % 2000),
                    CommitDate = sparseNull ? null : baseDate.AddDays(i % 2000 + 3),
                    ReceiptDate = baseDate.AddDays(i % 2000 + 7),
                    ShipInstruct = "DELIVER IN PERSON",
                    ShipMode = i % 3 == 0 ? "AIR" : "TRUCK",
                    Comment = "line item comment " + i.ToString(CultureInfo.InvariantCulture),
                }
            );
        }

        return rows;
    }

    [Benchmark(Baseline = true, Description = "Row-oriented write (transpose + encode)")]
    public async Task RowOrientedWriteEndToEnd()
    {
        using var stream = new MemoryStream();
        await _rows.WriteParquetAsync(stream);
    }

    [Benchmark(Description = "Columnar hand-off write (encode only)")]
    public async Task ColumnarWriteEndToEnd()
    {
        using var stream = new MemoryStream();
        await _batch.WriteParquetAsync(stream);
    }

    [Benchmark(Description = "Transpose only (the work the hand-off deletes)")]
    public int TransposeOnly() => TransposeIntoPooledBuffers(_rows);

    /// <summary>
    /// Row-to-column extraction shaped exactly like the generated write path: one pass over the
    /// rows, one pooled buffer per column, packed values plus definition levels for the nullable
    /// value columns, every buffer returned to the pool afterwards. No Parquet call — this is the
    /// transpose cost on its own.
    /// </summary>
    private static int TransposeIntoPooledBuffers(List<BenchmarkTpchLineItem> rows)
    {
        int count = rows.Count;

        long[] orderKey = ArrayPool<long>.Shared.Rent(count);
        int[] orderKeyDef = ArrayPool<int>.Shared.Rent(count);
        long[] partKey = ArrayPool<long>.Shared.Rent(count);
        int[] partKeyDef = ArrayPool<int>.Shared.Rent(count);
        long[] suppKey = ArrayPool<long>.Shared.Rent(count);
        int[] suppKeyDef = ArrayPool<int>.Shared.Rent(count);
        long[] lineNumber = ArrayPool<long>.Shared.Rent(count);
        int[] lineNumberDef = ArrayPool<int>.Shared.Rent(count);
        decimal[] quantity = ArrayPool<decimal>.Shared.Rent(count);
        int[] quantityDef = ArrayPool<int>.Shared.Rent(count);
        decimal[] extendedPrice = ArrayPool<decimal>.Shared.Rent(count);
        int[] extendedPriceDef = ArrayPool<int>.Shared.Rent(count);
        decimal[] discount = ArrayPool<decimal>.Shared.Rent(count);
        int[] discountDef = ArrayPool<int>.Shared.Rent(count);
        decimal[] tax = ArrayPool<decimal>.Shared.Rent(count);
        int[] taxDef = ArrayPool<int>.Shared.Rent(count);
        DateTime[] shipDate = ArrayPool<DateTime>.Shared.Rent(count);
        int[] shipDateDef = ArrayPool<int>.Shared.Rent(count);
        DateTime[] commitDate = ArrayPool<DateTime>.Shared.Rent(count);
        int[] commitDateDef = ArrayPool<int>.Shared.Rent(count);
        DateTime[] receiptDate = ArrayPool<DateTime>.Shared.Rent(count);
        int[] receiptDateDef = ArrayPool<int>.Shared.Rent(count);
        ReadOnlyMemory<char>?[] returnFlag = ArrayPool<ReadOnlyMemory<char>?>.Shared.Rent(count);
        ReadOnlyMemory<char>?[] lineStatus = ArrayPool<ReadOnlyMemory<char>?>.Shared.Rent(count);
        ReadOnlyMemory<char>?[] shipInstruct = ArrayPool<ReadOnlyMemory<char>?>.Shared.Rent(count);
        ReadOnlyMemory<char>?[] shipMode = ArrayPool<ReadOnlyMemory<char>?>.Shared.Rent(count);
        ReadOnlyMemory<char>?[] comment = ArrayPool<ReadOnlyMemory<char>?>.Shared.Rent(count);

        int nOrderKey = 0,
            nPartKey = 0,
            nSuppKey = 0,
            nLineNumber = 0,
            nQuantity = 0,
            nExtendedPrice = 0,
            nDiscount = 0,
            nTax = 0,
            nShipDate = 0,
            nCommitDate = 0,
            nReceiptDate = 0;

        for (int i = 0; i < count; i++)
        {
            BenchmarkTpchLineItem row = rows[i];

            Pack(row.OrderKey, orderKey, orderKeyDef, i, ref nOrderKey);
            Pack(row.PartKey, partKey, partKeyDef, i, ref nPartKey);
            Pack(row.SuppKey, suppKey, suppKeyDef, i, ref nSuppKey);
            Pack(row.LineNumber, lineNumber, lineNumberDef, i, ref nLineNumber);
            Pack(row.Quantity, quantity, quantityDef, i, ref nQuantity);
            Pack(row.ExtendedPrice, extendedPrice, extendedPriceDef, i, ref nExtendedPrice);
            Pack(row.Discount, discount, discountDef, i, ref nDiscount);
            Pack(row.Tax, tax, taxDef, i, ref nTax);
            Pack(row.ShipDate, shipDate, shipDateDef, i, ref nShipDate);
            Pack(row.CommitDate, commitDate, commitDateDef, i, ref nCommitDate);
            Pack(row.ReceiptDate, receiptDate, receiptDateDef, i, ref nReceiptDate);

            returnFlag[i] = AsMemory(row.ReturnFlag);
            lineStatus[i] = AsMemory(row.LineStatus);
            shipInstruct[i] = AsMemory(row.ShipInstruct);
            shipMode[i] = AsMemory(row.ShipMode);
            comment[i] = AsMemory(row.Comment);
        }

        ArrayPool<long>.Shared.Return(orderKey, clearArray: false);
        ArrayPool<int>.Shared.Return(orderKeyDef, clearArray: false);
        ArrayPool<long>.Shared.Return(partKey, clearArray: false);
        ArrayPool<int>.Shared.Return(partKeyDef, clearArray: false);
        ArrayPool<long>.Shared.Return(suppKey, clearArray: false);
        ArrayPool<int>.Shared.Return(suppKeyDef, clearArray: false);
        ArrayPool<long>.Shared.Return(lineNumber, clearArray: false);
        ArrayPool<int>.Shared.Return(lineNumberDef, clearArray: false);
        ArrayPool<decimal>.Shared.Return(quantity, clearArray: false);
        ArrayPool<int>.Shared.Return(quantityDef, clearArray: false);
        ArrayPool<decimal>.Shared.Return(extendedPrice, clearArray: false);
        ArrayPool<int>.Shared.Return(extendedPriceDef, clearArray: false);
        ArrayPool<decimal>.Shared.Return(discount, clearArray: false);
        ArrayPool<int>.Shared.Return(discountDef, clearArray: false);
        ArrayPool<decimal>.Shared.Return(tax, clearArray: false);
        ArrayPool<int>.Shared.Return(taxDef, clearArray: false);
        ArrayPool<DateTime>.Shared.Return(shipDate, clearArray: false);
        ArrayPool<int>.Shared.Return(shipDateDef, clearArray: false);
        ArrayPool<DateTime>.Shared.Return(commitDate, clearArray: false);
        ArrayPool<int>.Shared.Return(commitDateDef, clearArray: false);
        ArrayPool<DateTime>.Shared.Return(receiptDate, clearArray: false);
        ArrayPool<int>.Shared.Return(receiptDateDef, clearArray: false);
        ArrayPool<ReadOnlyMemory<char>?>.Shared.Return(returnFlag, clearArray: true);
        ArrayPool<ReadOnlyMemory<char>?>.Shared.Return(lineStatus, clearArray: true);
        ArrayPool<ReadOnlyMemory<char>?>.Shared.Return(shipInstruct, clearArray: true);
        ArrayPool<ReadOnlyMemory<char>?>.Shared.Return(shipMode, clearArray: true);
        ArrayPool<ReadOnlyMemory<char>?>.Shared.Return(comment, clearArray: true);

        return nOrderKey
            + nPartKey
            + nSuppKey
            + nLineNumber
            + nQuantity
            + nExtendedPrice
            + nDiscount
            + nTax
            + nShipDate
            + nCommitDate
            + nReceiptDate;
    }

    /// <summary>
    /// Setup-only transpose into plainly allocated buffers. A caller with genuinely columnar data
    /// already owns buffers like these; building them is the premise of the API, not part of any
    /// measured path.
    /// </summary>
    private static BenchmarkTpchLineItemColumnarBatch Transpose(List<BenchmarkTpchLineItem> rows)
    {
        int count = rows.Count;

        var orderKey = new long[count];
        var orderKeyDef = new int[count];
        var partKey = new long[count];
        var partKeyDef = new int[count];
        var suppKey = new long[count];
        var suppKeyDef = new int[count];
        var lineNumber = new long[count];
        var lineNumberDef = new int[count];
        var quantity = new decimal[count];
        var quantityDef = new int[count];
        var extendedPrice = new decimal[count];
        var extendedPriceDef = new int[count];
        var discount = new decimal[count];
        var discountDef = new int[count];
        var tax = new decimal[count];
        var taxDef = new int[count];
        var shipDate = new DateTime[count];
        var shipDateDef = new int[count];
        var commitDate = new DateTime[count];
        var commitDateDef = new int[count];
        var receiptDate = new DateTime[count];
        var receiptDateDef = new int[count];
        var returnFlag = new ReadOnlyMemory<char>?[count];
        var lineStatus = new ReadOnlyMemory<char>?[count];
        var shipInstruct = new ReadOnlyMemory<char>?[count];
        var shipMode = new ReadOnlyMemory<char>?[count];
        var comment = new ReadOnlyMemory<char>?[count];

        int nOrderKey = 0,
            nPartKey = 0,
            nSuppKey = 0,
            nLineNumber = 0,
            nQuantity = 0,
            nExtendedPrice = 0,
            nDiscount = 0,
            nTax = 0,
            nShipDate = 0,
            nCommitDate = 0,
            nReceiptDate = 0;

        for (int i = 0; i < count; i++)
        {
            BenchmarkTpchLineItem row = rows[i];

            Pack(row.OrderKey, orderKey, orderKeyDef, i, ref nOrderKey);
            Pack(row.PartKey, partKey, partKeyDef, i, ref nPartKey);
            Pack(row.SuppKey, suppKey, suppKeyDef, i, ref nSuppKey);
            Pack(row.LineNumber, lineNumber, lineNumberDef, i, ref nLineNumber);
            Pack(row.Quantity, quantity, quantityDef, i, ref nQuantity);
            Pack(row.ExtendedPrice, extendedPrice, extendedPriceDef, i, ref nExtendedPrice);
            Pack(row.Discount, discount, discountDef, i, ref nDiscount);
            Pack(row.Tax, tax, taxDef, i, ref nTax);
            Pack(row.ShipDate, shipDate, shipDateDef, i, ref nShipDate);
            Pack(row.CommitDate, commitDate, commitDateDef, i, ref nCommitDate);
            Pack(row.ReceiptDate, receiptDate, receiptDateDef, i, ref nReceiptDate);

            returnFlag[i] = AsMemory(row.ReturnFlag);
            lineStatus[i] = AsMemory(row.LineStatus);
            shipInstruct[i] = AsMemory(row.ShipInstruct);
            shipMode[i] = AsMemory(row.ShipMode);
            comment[i] = AsMemory(row.Comment);
        }

        return new BenchmarkTpchLineItemColumnarBatch
        {
            RowCount = count,
            OrderKey = orderKey.AsMemory(0, nOrderKey),
            OrderKeyDefinitionLevels = orderKeyDef.AsMemory(0, count),
            PartKey = partKey.AsMemory(0, nPartKey),
            PartKeyDefinitionLevels = partKeyDef.AsMemory(0, count),
            SuppKey = suppKey.AsMemory(0, nSuppKey),
            SuppKeyDefinitionLevels = suppKeyDef.AsMemory(0, count),
            LineNumber = lineNumber.AsMemory(0, nLineNumber),
            LineNumberDefinitionLevels = lineNumberDef.AsMemory(0, count),
            Quantity = quantity.AsMemory(0, nQuantity),
            QuantityDefinitionLevels = quantityDef.AsMemory(0, count),
            ExtendedPrice = extendedPrice.AsMemory(0, nExtendedPrice),
            ExtendedPriceDefinitionLevels = extendedPriceDef.AsMemory(0, count),
            Discount = discount.AsMemory(0, nDiscount),
            DiscountDefinitionLevels = discountDef.AsMemory(0, count),
            Tax = tax.AsMemory(0, nTax),
            TaxDefinitionLevels = taxDef.AsMemory(0, count),
            ReturnFlag = returnFlag.AsMemory(0, count),
            LineStatus = lineStatus.AsMemory(0, count),
            ShipDate = shipDate.AsMemory(0, nShipDate),
            ShipDateDefinitionLevels = shipDateDef.AsMemory(0, count),
            CommitDate = commitDate.AsMemory(0, nCommitDate),
            CommitDateDefinitionLevels = commitDateDef.AsMemory(0, count),
            ReceiptDate = receiptDate.AsMemory(0, nReceiptDate),
            ReceiptDateDefinitionLevels = receiptDateDef.AsMemory(0, count),
            ShipInstruct = shipInstruct.AsMemory(0, count),
            ShipMode = shipMode.AsMemory(0, count),
            Comment = comment.AsMemory(0, count),
        };
    }

    private static void Pack<T>(
        T? source,
        T[] values,
        int[] definitionLevels,
        int i,
        ref int packed
    )
        where T : struct
    {
        int flag = source.HasValue ? 1 : 0;
        values[packed] = source.GetValueOrDefault();
        packed += flag;
        definitionLevels[i] = flag;
    }

    private static ReadOnlyMemory<char>? AsMemory(string? value) =>
        value is null ? (ReadOnlyMemory<char>?)null : value.AsMemory();
}
