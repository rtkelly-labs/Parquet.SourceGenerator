using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Parquet.SourceGenerator.Benchmarks;

/// <summary>
/// End-to-end read of the Adult Census dataset (32,561 rows, 9 categorical string columns)
/// with and without span-keyed string deduplication.
/// </summary>
[MemoryDiagnoser]
[InProcess]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class StringDeduplicationReadBenchmark
{
    private byte[] _rawBytes = null!;

    private readonly ParquetSerializerOptions _plain = new() { DeduplicateStrings = false };
    private readonly ParquetSerializerOptions _deduplicating = new() { DeduplicateStrings = true };

    [GlobalSetup]
    public void Setup()
    {
        string dataPath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "adult_census_income.parquet"
        );
        if (!System.IO.File.Exists(dataPath))
        {
            dataPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "benchmarks",
                    "data",
                    "adult_census_income.parquet"
                )
            );
        }

        if (!System.IO.File.Exists(dataPath))
        {
            throw new FileNotFoundException($"Cannot locate benchmark dataset: {dataPath}");
        }

        _rawBytes = System.IO.File.ReadAllBytes(dataPath);
    }

    [Benchmark(Baseline = true)]
    public async Task<List<BenchmarkAdultCensus>> CensusReadWithoutDeduplication()
    {
        using var stream = new MemoryStream(_rawBytes);
        return await BenchmarkAdultCensusParquetExtensions.ReadParquetAsync(stream, _plain);
    }

    [Benchmark]
    public async Task<List<BenchmarkAdultCensus>> CensusReadWithSpanDeduplication()
    {
        using var stream = new MemoryStream(_rawBytes);
        return await BenchmarkAdultCensusParquetExtensions.ReadParquetAsync(stream, _deduplicating);
    }
}

/// <summary>
/// Isolates the deduplication routine itself, independent of any Parquet I/O.
/// </summary>
/// <remarks>
/// The two cache implementations below mirror the code the generator emits: the managed
/// <see cref="string"/>-keyed table that shipped before issue #143, and the span-keyed table
/// that replaces it. The input is the categorical distribution of the Adult Census
/// <c>workclass</c>/<c>education</c>/<c>occupation</c> columns.
/// </remarks>
[MemoryDiagnoser]
[InProcess]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class StringDeduplicationRoutineBenchmark
{
    private const int Rows = 32_561;

    private static readonly string[] Categories =
    {
        "Private",
        "Self-emp-not-inc",
        "Self-emp-inc",
        "Federal-gov",
        "Local-gov",
        "State-gov",
        "Without-pay",
        "Never-worked",
        "?",
    };

    private char[][] _rawValues = null!;
    private string[] _sink = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rawValues = new char[Rows][];
        for (int i = 0; i < Rows; i++)
        {
            _rawValues[i] = Categories[i % Categories.Length].ToCharArray();
        }

        _sink = new string[Rows];
    }

    /// <summary>
    /// What the reader did before: one <see cref="string"/> is materialized per row (standing in
    /// for Parquet.Net's own per-row decode) and only then handed to the cache.
    /// </summary>
    [Benchmark(Baseline = true)]
    public string[] StringKeyedDeduplication()
    {
        using var cache = new ManagedStringCache(512);
        for (int i = 0; i < Rows; i++)
        {
            _sink[i] = cache.Deduplicate(new string(_rawValues[i]))!;
        }

        return _sink;
    }

    /// <summary>
    /// What the reader does now: the span is looked up directly and a cache hit allocates nothing.
    /// </summary>
    [Benchmark]
    public string[] SpanKeyedDeduplication()
    {
        using var cache = new SpanStringCache(512);
        for (int i = 0; i < Rows; i++)
        {
            _sink[i] = cache.GetOrAdd(_rawValues[i]);
        }

        return _sink;
    }

    /// <summary>No cache at all — one string per row, the floor for allocation cost.</summary>
    [Benchmark]
    public string[] NoDeduplication()
    {
        for (int i = 0; i < Rows; i++)
        {
            _sink[i] = new string(_rawValues[i]);
        }

        return _sink;
    }

    private struct ManagedStringCache : IDisposable
    {
        private const int ProbeLimit = 4;
        private string?[]? _entries;
        private readonly int _mask;

        public ManagedStringCache(int capacity)
        {
            _entries = ArrayPool<string?>.Shared.Rent(capacity);
            _mask = capacity - 1;
            Array.Clear(_entries, 0, capacity);
        }

        public string? Deduplicate(string? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value.Length == 0)
            {
                return string.Empty;
            }

            var entries = _entries;
            if (entries is null)
            {
                return value;
            }

            int mask = _mask;
            int index = (int)(Hash(value.AsSpan()) & (uint)mask);
            for (int probe = 0; probe < ProbeLimit; probe++)
            {
                int slot = (index + probe) & mask;
                string? candidate = entries[slot];
                if (candidate is null)
                {
                    entries[slot] = value;
                    return value;
                }

                if (string.Equals(candidate, value, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            entries[index] = value;
            return value;
        }

        public void Dispose()
        {
            var entries = _entries;
            if (entries is not null)
            {
                _entries = null;
                ArrayPool<string?>.Shared.Return(entries, clearArray: true);
            }
        }

        internal static uint Hash(ReadOnlySpan<char> value)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= 16777619u;
            }

            return hash;
        }
    }

    private struct SpanStringCache : IDisposable
    {
        private const int ProbeLimit = 4;
        private string?[]? _entries;
        private readonly int _mask;

        public SpanStringCache(int capacity)
        {
            _entries = ArrayPool<string?>.Shared.Rent(capacity);
            _mask = capacity - 1;
            Array.Clear(_entries, 0, capacity);
        }

        public string GetOrAdd(ReadOnlySpan<char> value)
        {
            if (value.Length == 0)
            {
                return string.Empty;
            }

            var entries = _entries;
            if (entries is null)
            {
                return new string(value);
            }

            int mask = _mask;
            int index = (int)(ManagedStringCache.Hash(value) & (uint)mask);
            for (int probe = 0; probe < ProbeLimit; probe++)
            {
                int slot = (index + probe) & mask;
                string? candidate = entries[slot];
                if (candidate is null)
                {
                    string inserted = new string(value);
                    entries[slot] = inserted;
                    return inserted;
                }

                if (SpanEqualsOrdinal(value, candidate))
                {
                    return candidate;
                }
            }

            string replacement = new string(value);
            entries[index] = replacement;
            return replacement;
        }

        private static bool SpanEqualsOrdinal(ReadOnlySpan<char> value, string candidate)
        {
            if (value.Length != candidate.Length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != candidate[i])
                {
                    return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            var entries = _entries;
            if (entries is not null)
            {
                _entries = null;
                ArrayPool<string?>.Shared.Return(entries, clearArray: true);
            }
        }
    }
}
