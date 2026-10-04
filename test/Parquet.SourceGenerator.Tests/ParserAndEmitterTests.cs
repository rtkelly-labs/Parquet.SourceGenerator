using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Parquet.SourceGenerator.Diagnostics;
using Parquet.SourceGenerator.Models;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public sealed class ParserAndEmitterTests
{
    private static readonly string[] ClassArgs1 = new[] { "TestClass" };
    private static readonly string[] ClassArgs2 = new[] { "col", "TestClass" };

    [Fact]
    public void DiagnosticInfoValueEqualityAndMethods()
    {
        var diag1 = new DiagnosticInfo(
            DiagnosticDescriptors.MustBePartial,
            Location.None,
            ClassArgs1
        );
        var diag2 = new DiagnosticInfo(
            DiagnosticDescriptors.MustBePartial,
            Location.None,
            ClassArgs1
        );
        var diag3 = new DiagnosticInfo(
            DiagnosticDescriptors.DuplicateColumnName,
            Location.None,
            ClassArgs2
        );

        diag1.Equals(diag2).ShouldBeTrue();
        diag1.Equals(diag3).ShouldBeFalse();
        diag1.GetHashCode().ShouldBe(diag2.GetHashCode());

        Diagnostic diagnostic = diag1.ToDiagnostic();
        diagnostic.Id.ShouldBe("PARQ001");
    }

    // ── Parquet.Net Contract Enforcement Tests ─────────────────────────────

    [Fact]
    public async Task WriteParquetAsyncNullStreamThrowsArgumentNullException()
    {
        var items = new List<TypeCoverageRecord> { new() };
        await Should.ThrowAsync<ArgumentNullException>(() => items.WriteParquetAsync(null!));
    }

    [Fact]
    public async Task WriteParquetAsyncNullItemsThrowsArgumentNullException()
    {
        IReadOnlyCollection<TypeCoverageRecord> items = null!;
        var stream = new MemoryStream();
        await Should.ThrowAsync<ArgumentNullException>(() => items.WriteParquetAsync(stream));
    }

    [Fact]
    public async Task WriteParquetBatchedAsyncInvalidRowGroupSizeThrowsArgumentOutOfRange()
    {
        var items = new List<TypeCoverageRecord> { new() };
        var stream = new MemoryStream();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            items.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = 0 }
            )
        );
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            items.WriteParquetBatchedAsync(
                stream,
                new ParquetSerializerOptions { RowGroupSize = -10 }
            )
        );
    }

    [Fact]
    public async Task ToArrayAsyncNullStreamThrowsArgumentNullExceptionAsync()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            TypeCoverageRecordParquet.From((Stream)null!).ToArrayAsync()
        );
    }
}
