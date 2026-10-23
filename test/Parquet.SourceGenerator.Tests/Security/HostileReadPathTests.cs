using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Shouldly;
using Xunit;
using ParquetPhysicalType = Parquet.Meta.Type;

namespace Parquet.SourceGenerator.Tests.Security;

/// <summary>
/// The hostile-input sweep (#607): every read limit on <see cref="ParquetSerializerOptions"/> x every
/// route through the generated reader x every source kind, on a flat and two compound shapes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the matrix is a hand-written list of terminals. The routes are built by reflection
/// over each shape's generated <c>&lt;Model&gt;ParquetReader</c>: its <c>From</c> overloads are the
/// sources, its methods returning the reader are the modifiers, the rest are the terminals. A new
/// terminal therefore gets cells without anyone adding them, and a cell for a terminal whose return
/// shape this file cannot drive fails rather than being skipped. The limits are every property of
/// the options type, each either given a cell or listed in <see cref="NotReadLimits"/> with the
/// reason; <see cref="EveryOptionIsALimitWithCellsOrExplicitlyNotOne"/> fails on one that is neither.
/// </para>
/// <para>
/// Each cell reads a small valid file with exactly one limit set just below what the file needs and
/// every other option at its default, so only that guard can fire. It asserts the exact exception
/// type and the limit's own message (including the configured value), under a timeout and an
/// allocation ceiling. The negative control is the same file and route with that one limit relaxed
/// to its maximum: the read must then succeed with every row, which proves the guard, and nothing
/// else about the file, caused the rejection.
/// </para>
/// <para>
/// The footer corruptions of <see cref="HostileParquetTests"/> run over the same routes with default
/// options; their negative control is the untampered fixture reading on every route.
/// </para>
/// <para>
/// Modern backend only. The legacy backend's hostile matrix belongs to #597.
/// </para>
/// </remarks>
[Collection(AllocationMeasurementSuite.Name)]
public sealed class HostileReadPathTests
{
    /// <summary>How long one cell may take before it counts as a hang.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Each fixture is a few kilobytes; a cell allocating anywhere near this means a declared size
    /// was believed before the guard ran.
    /// </summary>
    private const long AllocationCeilingBytes = 64L * 1024 * 1024;

    private const int RowGroups = 3;
    private const int RowsPerGroup = 4;
    private const int TotalRows = RowGroups * RowsPerGroup;

    /// <summary>Long enough to compress well and to exceed small page and string limits.</summary>
    private const int PayloadLength = 512;

    // ---------------------------------------------------------------- shapes

    private sealed record Shape(string Name, Type Model, Func<Task<byte[]>> Write, int SchemaDepth)
    {
        private readonly Lazy<Task<byte[]>> _bytes = new(Write);

        public Task<byte[]> BytesAsync() => _bytes.Value;
    }

    /// <summary>
    /// Payloads alternate between two values, so a dictionary-encoded column holds two entries, and
    /// are long runs of one character, so every payload page compresses far beyond ratio 1.
    /// </summary>
    private static string Payload(int i) => new(i % 2 == 0 ? 'a' : 'b', PayloadLength);

    /// <summary>
    /// Parquet.Net 6 dictionary-encodes a string column only when hinted, keyed by its path, and
    /// never a repeated one; the list shape therefore carries a top-level <c>Label</c> column too.
    /// </summary>
    private static ParquetSerializerOptions WriteOptions(params string[] dictionaryColumns)
    {
        var options = new ParquetSerializerOptions { RowGroupSize = RowsPerGroup };
        foreach (string column in dictionaryColumns)
        {
            options.ColumnEncodingHints[column] = ParquetColumnEncoding.Dictionary;
        }

        return options;
    }

    private static readonly Shape[] Shapes =
    [
        new(
            "flat",
            typeof(CompressibleRecord),
            () =>
                WriteAsync(ms =>
                    Enumerable
                        .Range(0, TotalRows)
                        .Select(i => new CompressibleRecord { Id = i, Payload = Payload(i) })
                        .ToList()
                        .WriteParquetAsync(ms, WriteOptions("payload"))
                ),
            SchemaDepth: 1
        ),
        new(
            "list",
            typeof(HostileListRow),
            () =>
                WriteAsync(ms =>
                    Enumerable
                        .Range(0, TotalRows)
                        .Select(i => new HostileListRow
                        {
                            Id = i,
                            Label = Payload(i),
                            Tags = [Payload(i), Payload(i + 1)],
                            Scores = [i, i + 1],
                        })
                        .ToList()
                        .WriteParquetAsync(ms, WriteOptions("Label"))
                ),
            SchemaDepth: 2
        ),
        new(
            "struct",
            typeof(NestedOrder),
            () =>
                WriteAsync(ms =>
                    Enumerable
                        .Range(0, TotalRows)
                        .Select(i => new NestedOrder
                        {
                            Id = i,
                            Ship = new Address { City = Payload(i), Zip = i },
                            Bill = new Address { City = Payload(i + 1), Zip = i },
                        })
                        .ToList()
                        .WriteParquetAsync(ms, WriteOptions("Ship/City", "Bill/City"))
                ),
            SchemaDepth: 2
        ),
    ];

    private static async Task<byte[]> WriteAsync(Func<Stream, Task> write)
    {
        using var ms = new MemoryStream();
        await write(ms);
        return ms.ToArray();
    }

    private static Shape ShapeNamed(string name) => Shapes.Single(s => s.Name == name);

    // ---------------------------------------------------------------- limits

    /// <summary>
    /// One read guard: what it bounds, the value just below what every fixture needs, and the exact
    /// diagnostic it raises. <c>{0}</c> in <paramref name="Message"/> is the configured limit.
    /// </summary>
    private sealed record Limit(
        string Name,
        Func<Shape, long> Tight,
        string Message,
        string Guards
    );

    private static readonly Limit[] Limits =
    [
        new(
            nameof(ParquetSerializerOptions.MaxRowGroupCount),
            _ => RowGroups - 1,
            @"Row group count \d+ is invalid or exceeds maximum allowed {0}\.",
            "the row-group count the footer declares"
        ),
        new(
            nameof(ParquetSerializerOptions.MaxAllocationValues),
            _ => RowsPerGroup - 1,
            @"(Row group \d+ row count \d+ is invalid or exceeds|Total (matching )?row count \d+ exceeds|Column '[^']+' NumValues \(\d+\) is invalid or exceeds) maximum allowed {0}\.",
            "the row and value counts the footer and pages declare"
        ),
        new(
            nameof(ParquetSerializerOptions.MaxAllocationBytes),
            _ => 16,
            @"exceeding MaxAllocationBytes \({0}\)\.",
            "the bytes a row group's buffers would take (#361)"
        ),
        new(
            nameof(ParquetSerializerOptions.MaxDictionaryEntries),
            _ => 1,
            @"Dictionary column '[^']+' entry count \d+ exceeds maximum allowed {0}\.",
            "the entries a dictionary page declares"
        ),
        new(
            nameof(ParquetSerializerOptions.MaxStringLengthBytes),
            _ => PayloadLength - 1,
            @"String column '[^']+' value at index \d+ UTF-8 length \d+ exceeds maximum allowed {0}\.",
            "the UTF-8 length of a decoded string"
        ),
        new(
            nameof(ParquetSerializerOptions.MaxNestingDepth),
            shape => shape.SchemaDepth - 1,
            @"Schema nesting depth \d+ exceeds maximum allowed {0}\.",
            "the nesting depth of the file schema"
        ),
        new(
            nameof(ParquetSerializerOptions.MaxDecompressedPageSize),
            _ => 64,
            @"has uncompressed size \d+ bytes, exceeding maximum allowed {0}\.",
            "the uncompressed size a page header declares (#358, #359)"
        ),
        new(
            nameof(ParquetSerializerOptions.MaxDecompressionExpansionRatio),
            _ => 1,
            @"expands from \d+ to \d+ bytes, exceeding maximum expansion ratio {0}\.",
            "the expansion ratio a page header declares (#358, #359)"
        ),
    ];

    /// <summary>
    /// Options properties that bound nothing a hostile file controls, with the reason. Every other
    /// property must be a <see cref="Limits"/> entry.
    /// </summary>
    private static readonly Dictionary<string, string> NotReadLimits = new(StringComparer.Ordinal)
    {
        [nameof(ParquetSerializerOptions.MaxDegreeOfParallelism)] =
            "a concurrency setting chosen by the caller; it bounds no quantity read from the file",
        [nameof(ParquetSerializerOptions.RowGroupSize)] = "write only",
        [nameof(ParquetSerializerOptions.CompressionMethod)] = "write only",
        [nameof(ParquetSerializerOptions.CompressionLevel)] = "write only",
        [nameof(ParquetSerializerOptions.DictionaryEncodingThreshold)] = "write only",
        [nameof(ParquetSerializerOptions.DictionaryEncodingSampleSize)] = "write only",
        [nameof(ParquetSerializerOptions.ColumnEncodingHints)] = "write only",
        [nameof(ParquetSerializerOptions.DeduplicateStrings)] =
            "changes how decoded strings are shared, not how much a read may decode",
    };

    private static Limit LimitNamed(string name) => Limits.Single(l => l.Name == name);

    // ---------------------------------------------------------------- footer corruptions

    /// <summary>
    /// A footer rewrite from <see cref="HostileParquetTests"/>, read with default options, and the
    /// diagnostic it must raise. Each mutates the first column chunk (<c>Id</c>, INT32, in every
    /// shape), so none depends on the shape's other columns.
    /// </summary>
    private sealed record Corruption(
        string Name,
        Action<Parquet.Meta.FileMetaData, int> Mutate,
        string Message
    );

    private static readonly Corruption[] Corruptions =
    [
        new(
            "physical-type",
            (m, _) => First(m).Type = ParquetPhysicalType.BYTE_ARRAY,
            "physical type 'BYTE_ARRAY'"
        ),
        new(
            "offset-before-header",
            (m, _) => First(m).DataPageOffset = -1,
            "outside the file data bounds"
        ),
        new(
            "offset-in-footer",
            (m, footerStart) => First(m).DataPageOffset = footerStart,
            "outside the file data bounds"
        ),
        new(
            "size-past-footer",
            (m, _) => First(m).TotalCompressedSize = long.MaxValue,
            "extends beyond the file data bounds"
        ),
        new(
            "size-negative",
            (m, _) => First(m).TotalCompressedSize = -1,
            "negative compressed size"
        ),
        new("size-zero", (m, _) => First(m).TotalCompressedSize = 0, "no compressed data"),
        new(
            "chunk-contains-next",
            (m, _) =>
                First(m).TotalCompressedSize =
                    ChunkStart(m.RowGroups[0].Columns[1].MetaData!) - First(m).DataPageOffset + 1,
            "ranges overlap"
        ),
        new(
            "chunks-share-offset",
            (m, _) => m.RowGroups[0].Columns[1].MetaData!.DataPageOffset = First(m).DataPageOffset,
            "ranges overlap"
        ),
    ];

    private static Parquet.Meta.ColumnMetaData First(Parquet.Meta.FileMetaData metadata) =>
        metadata.RowGroups[0].Columns[0].MetaData!;

    private static long ChunkStart(Parquet.Meta.ColumnMetaData column) =>
        column.DictionaryPageOffset ?? column.DataPageOffset;

    private static Corruption CorruptionNamed(string name) =>
        Corruptions.Single(c => c.Name == name);

    private static ParquetSerializerOptions OptionsWith(string limit, long value)
    {
        var options = new ParquetSerializerOptions();
        PropertyInfo property = typeof(ParquetSerializerOptions).GetProperty(limit)!;
        property.SetValue(
            options,
            Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture)
        );
        return options;
    }

    private static long Relaxed(string limit) =>
        typeof(ParquetSerializerOptions).GetProperty(limit)!.PropertyType == typeof(long)
            ? long.MaxValue
            : int.MaxValue;

    // ---------------------------------------------------------------- routes, from the reader type

    private const string WithOptionsModifier = "WithOptions";
    private const string WhereModifier = "Where";
    private const string ParallelModifier = "Parallel";

    /// <summary>
    /// What the matrix knows how to compose. A new modifier on any generated reader fails
    /// <see cref="EveryGeneratedReaderMemberIsCoveredByTheMatrix"/> until it is understood here.
    /// </summary>
    private static readonly string[] KnownModifiers =
    [
        WithOptionsModifier,
        WhereModifier,
        ParallelModifier,
    ];

    private static readonly string[] EntryPointMembers = ["From"];
    private static readonly string[] SourceKinds = ["Memory", "Stream"];

    /// <summary>Examined N: the terminals and modifiers generated readers declare today.</summary>
    private static readonly string[] ExaminedTerminals =
    [
        "AsAsyncEnumerable",
        "AsBatches",
        "ToArrayAsync",
    ];
    private static readonly string[] ExaminedModifiers =
    [
        ParallelModifier,
        WhereModifier,
        WithOptionsModifier,
    ];

    /// <summary>A path through the reader: a source, the modifiers applied in order, a terminal.</summary>
    private sealed record Route(string Source, string[] Modifiers, string Terminal)
    {
        public string Id =>
            string.Join(".", new[] { Source }.Concat(Modifiers).Append(Terminal).ToArray());

        public static Route Parse(string id)
        {
            string[] parts = id.Split('.');
            return new Route(parts[0], parts[1..^1], parts[^1]);
        }

        /// <summary>
        /// The combinations the reader rejects with <see cref="NotSupportedException"/> by design
        /// (see <c>ReadBuilderComponent</c>): parallel needs a buffer, cannot filter and only
        /// materialises; batches cannot filter.
        /// </summary>
        public bool IsRejectedByDesign
        {
            get
            {
                bool parallel = Modifiers.Contains(ParallelModifier);
                bool where = Modifiers.Contains(WhereModifier);
                return (parallel && Source == "Stream")
                    || (parallel && where)
                    || (parallel && Terminal != "ToArrayAsync")
                    || (where && Terminal == "AsBatches");
            }
        }
    }

    private sealed record ReaderSurface(
        Type Entry,
        Type Reader,
        Dictionary<string, MethodInfo> Sources,
        Dictionary<string, MethodInfo> Modifiers,
        Dictionary<string, MethodInfo> Terminals
    );

    private static string SourceKind(Type parameter) =>
        parameter == typeof(Stream) ? "Stream"
        : parameter == typeof(ReadOnlyMemory<byte>) ? "Memory"
        : throw new InvalidOperationException(
            $"Unknown read source parameter type {parameter}: add a source kind to the matrix."
        );

    private static ReaderSurface SurfaceOf(Type model)
    {
        Type entry = model.Assembly.GetType(model.FullName + "Parquet", throwOnError: true)!;
        MethodInfo[] froms = entry
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "From")
            .ToArray();
        Type reader = froms[0].ReturnType;
        return SurfaceOfReader(entry, reader, froms);
    }

    private static ReaderSurface SurfaceOfReader(Type entry, Type reader, MethodInfo[] froms)
    {
        var sources = froms.ToDictionary(
            m => SourceKind(m.GetParameters().Single().ParameterType),
            m => m
        );
        MethodInfo[] members = reader
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();
        var modifiers = members.Where(m => m.ReturnType == reader).ToDictionary(m => m.Name);
        var terminals = members.Where(m => m.ReturnType != reader).ToDictionary(m => m.Name);
        return new ReaderSurface(entry, reader, sources, modifiers, terminals);
    }

    /// <summary>
    /// Every source x every ordering of the optional modifiers x every terminal the reader declares.
    /// <c>WithOptions</c> is applied to every route, so it is not part of the route.
    /// </summary>
    private static IEnumerable<Route> RoutesOf(ReaderSurface surface)
    {
        string[] optional = surface
            .Modifiers.Keys.Where(m => m != WithOptionsModifier)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToArray();
        var chains = new List<string[]> { Array.Empty<string>() };
        foreach (string[] chain in Orderings(optional))
        {
            chains.Add(chain);
        }

        foreach (string source in surface.Sources.Keys.OrderBy(s => s, StringComparer.Ordinal))
        foreach (string[] chain in chains)
        foreach (string terminal in surface.Terminals.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            yield return new Route(source, chain, terminal);
        }
    }

    /// <summary>Every non-empty ordered selection of distinct modifiers.</summary>
    private static IEnumerable<string[]> Orderings(string[] items)
    {
        foreach (string first in items)
        {
            yield return [first];
            foreach (string[] rest in Orderings(items.Where(i => i != first).ToArray()))
            {
                yield return [first, .. rest];
            }
        }
    }

    // ---------------------------------------------------------------- the matrix

    public static TheoryData<string, string, string> Cells
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (Limit limit in Limits)
            foreach (Shape shape in Shapes)
            foreach (
                Route route in RoutesOf(SurfaceOf(shape.Model)).Where(r => !r.IsRejectedByDesign)
            )
            {
                data.Add(limit.Name, shape.Name, route.Id);
            }

            return data;
        }
    }

    public static TheoryData<string, string> RejectedRoutes
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (Shape shape in Shapes)
            foreach (
                Route route in RoutesOf(SurfaceOf(shape.Model)).Where(r => r.IsRejectedByDesign)
            )
            {
                data.Add(shape.Name, route.Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task LimitIsEnforcedOnRoute(string limitName, string shapeName, string routeId)
    {
        Limit limit = LimitNamed(limitName);
        Shape shape = ShapeNamed(shapeName);
        long tight = limit.Tight(shape);
        tight.ShouldBeGreaterThanOrEqualTo(
            0,
            $"{limitName} has no value below what {shapeName} needs."
        );

        Outcome outcome = await RunAsync(
            shape,
            Route.Parse(routeId),
            OptionsWith(limitName, tight)
        );
        string cell = $"{limitName} x {shapeName} x {routeId}";
        AssertBounded(cell, outcome);

        outcome.Exception.ShouldNotBeNull(
            $"{cell}: the read succeeded with {limitName} = {tight}; {limit.Guards} is not bounded on this route."
        );
        outcome
            .Exception.GetType()
            .ShouldBe(
                typeof(InvalidDataException),
                $"{cell}: expected the documented InvalidDataException, got {outcome.Exception}"
            );
        outcome.Exception.Message.ShouldMatch(
            string.Format(CultureInfo.InvariantCulture, limit.Message, tight),
            $"{cell}: rejected, but not by the {limitName} guard."
        );
    }

    /// <summary>
    /// The negative control: the same file, route and options with only the limit under test
    /// relaxed. The read must succeed and return every row, so the failure above was that guard's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task RelaxingTheLimitAdmitsTheSameFile(
        string limitName,
        string shapeName,
        string routeId
    )
    {
        Shape shape = ShapeNamed(shapeName);
        Outcome outcome = await RunAsync(
            shape,
            Route.Parse(routeId),
            OptionsWith(limitName, Relaxed(limitName))
        );
        string cell = $"{limitName} relaxed x {shapeName} x {routeId}";
        AssertBounded(cell, outcome);
        outcome.Exception.ShouldBeNull($"{cell}: the fixture is not otherwise readable.");
        outcome.Rows.ShouldBe(TotalRows, $"{cell}: rows read");
    }

    public static TheoryData<string, string, string> CorruptionCells
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (Corruption corruption in Corruptions)
            foreach (Shape shape in Shapes)
            foreach (
                Route route in RoutesOf(SurfaceOf(shape.Model)).Where(r => !r.IsRejectedByDesign)
            )
            {
                data.Add(corruption.Name, shape.Name, route.Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CorruptionCells))]
    public async Task CorruptionIsRejectedOnRoute(
        string corruptionName,
        string shapeName,
        string routeId
    )
    {
        Corruption corruption = CorruptionNamed(corruptionName);
        Shape shape = ShapeNamed(shapeName);
        byte[] hostile = await HostileParquetTests.RewriteColumnMetadataAsync(
            await shape.BytesAsync(),
            corruption.Mutate
        );

        Outcome outcome = await RunAsync(
            shape,
            Route.Parse(routeId),
            new ParquetSerializerOptions(),
            hostile
        );
        string cell = $"{corruptionName} x {shapeName} x {routeId}";
        AssertBounded(cell, outcome);
        outcome.Exception.ShouldNotBeNull(
            $"{cell}: the corrupted file was read without complaint."
        );
        outcome
            .Exception.GetType()
            .ShouldBe(
                typeof(InvalidDataException),
                $"{cell}: expected the documented InvalidDataException, got {outcome.Exception}"
            );
        outcome.Exception.Message.ShouldContain(corruption.Message, Case.Sensitive, cell);
    }

    /// <summary>
    /// The corruptions' negative control: the same fixture before the footer rewrite reads every row
    /// on every route with default options.
    /// </summary>
    [Theory]
    [MemberData(nameof(RouteCells))]
    public async Task UntamperedFixtureReadsWithDefaultOptions(string shapeName, string routeId)
    {
        Outcome outcome = await RunAsync(
            ShapeNamed(shapeName),
            Route.Parse(routeId),
            new ParquetSerializerOptions()
        );
        string cell = $"default options x {shapeName} x {routeId}";
        AssertBounded(cell, outcome);
        outcome.Exception.ShouldBeNull(cell);
        outcome.Rows.ShouldBe(TotalRows, cell);
    }

    public static TheoryData<string, string> RouteCells
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (Shape shape in Shapes)
            foreach (
                Route route in RoutesOf(SurfaceOf(shape.Model)).Where(r => !r.IsRejectedByDesign)
            )
            {
                data.Add(shape.Name, route.Id);
            }

            return data;
        }
    }

    /// <summary>
    /// Routes the reader rejects by design must reject even on a hostile read, before decoding, so
    /// the classification that keeps them out of <see cref="Cells"/> is not hiding a working route.
    /// </summary>
    [Theory]
    [MemberData(nameof(RejectedRoutes))]
    public async Task RouteRejectedByDesignThrowsNotSupported(string shapeName, string routeId)
    {
        Outcome outcome = await RunAsync(
            ShapeNamed(shapeName),
            Route.Parse(routeId),
            new ParquetSerializerOptions()
        );
        AssertBounded(routeId, outcome);
        outcome.Exception.ShouldBeOfType<NotSupportedException>($"{shapeName} x {routeId}");
    }

    // ---------------------------------------------------------------- completeness ("examined N")

    [Fact]
    public void EveryOptionIsALimitWithCellsOrExplicitlyNotOne()
    {
        string[] properties = typeof(ParquetSerializerOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        string[] limits = Limits.Select(l => l.Name).ToArray();

        properties
            .Except(limits)
            .Except(NotReadLimits.Keys)
            .ShouldBeEmpty(
                "An option is neither a read limit with matrix cells nor listed in NotReadLimits."
            );
        limits
            .Concat(NotReadLimits.Keys)
            .Except(properties)
            .ShouldBeEmpty("The matrix names an option that no longer exists.");
        limits.Intersect(NotReadLimits.Keys).ShouldBeEmpty();

        // Examined N: the eight guards on main today. A new Max* option lands here first.
        limits.Length.ShouldBe(8);
        typeof(ParquetSerializerOptions)
            .GetProperties()
            .Where(p => p.Name.StartsWith("Max", StringComparison.Ordinal))
            .Select(p => p.Name)
            .Except(limits)
            .ToArray()
            .ShouldBe(new[] { nameof(ParquetSerializerOptions.MaxDegreeOfParallelism) });
    }

    /// <summary>
    /// Every generated reader in this assembly, not only the matrix shapes, may declare only
    /// sources, modifiers and terminals the matrix drives, and every terminal and modifier that
    /// exists anywhere must be reached by at least one shape. A new terminal emitted for some model
    /// kind therefore fails here until a shape with it joins the matrix.
    /// </summary>
    [Fact]
    public void EveryGeneratedReaderMemberIsCoveredByTheMatrix()
    {
        Type[] entries = typeof(CompressibleRecord)
            .Assembly.GetTypes()
            .Where(t =>
                t.IsAbstract
                && t.IsSealed
                && t.Name.EndsWith("Parquet", StringComparison.Ordinal)
                && t.GetMethod("From", [typeof(Stream)]) is not null
            )
            .ToArray();
        entries.Length.ShouldBeGreaterThan(Shapes.Length);

        var everyModifier = new HashSet<string>(StringComparer.Ordinal);
        var everyTerminal = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type entry in entries)
        {
            MethodInfo[] froms = entry
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "From")
                .ToArray();
            entry
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Distinct()
                .ToArray()
                .ShouldBe(
                    EntryPointMembers,
                    $"{entry.Name} has an entry point the matrix does not drive."
                );
            ReaderSurface surface = SurfaceOfReader(entry, froms[0].ReturnType, froms);
            surface
                .Sources.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .ToArray()
                .ShouldBe(SourceKinds, entry.Name);
            surface.Modifiers.Keys.Except(KnownModifiers).ShouldBeEmpty(entry.Name);
            foreach (MethodInfo terminal in surface.Terminals.Values)
            {
                IsDrivable(terminal.ReturnType)
                    .ShouldBeTrue(
                        $"{entry.Name}.{terminal.Name} returns a shape no cell can drain."
                    );
            }

            everyModifier.UnionWith(surface.Modifiers.Keys);
            everyTerminal.UnionWith(surface.Terminals.Keys);
        }

        ReaderSurface[] matrix = Shapes.Select(s => SurfaceOf(s.Model)).ToArray();
        everyTerminal
            .Except(matrix.SelectMany(s => s.Terminals.Keys))
            .ShouldBeEmpty("A terminal exists that no matrix shape reaches: add a shape with it.");
        everyModifier
            .Except(matrix.SelectMany(s => s.Modifiers.Keys))
            .ShouldBeEmpty("A modifier exists that no matrix shape reaches: add a shape with it.");

        // Examined N.
        everyTerminal
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(ExaminedTerminals);
        everyModifier.OrderBy(t => t, StringComparer.Ordinal).ToArray().ShouldBe(ExaminedModifiers);
        Shapes
            .Select(s => s.Model)
            .ShouldContain(t => SurfaceOf(t).Terminals.ContainsKey("AsBatches"));
        Shapes
            .Select(s => s.Model)
            .Count(t => !SurfaceOf(t).Terminals.ContainsKey("AsBatches"))
            .ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The fixtures carry what the tight limits need, so a cell cannot pass because the file never
    /// reached the guard: several row groups and a dictionary page in every one.
    /// </summary>
    [Theory]
    [InlineData("flat")]
    [InlineData("list")]
    [InlineData("struct")]
    public async Task FixtureCarriesEveryFeatureTheLimitsNeed(string shapeName)
    {
        byte[] bytes = await ShapeNamed(shapeName).BytesAsync();
        using var stream = new MemoryStream(bytes, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        reader.RowGroupCount.ShouldBe(RowGroups);
        for (int r = 0; r < reader.RowGroupCount; r++)
        {
            using ParquetRowGroupReader group = reader.OpenRowGroupReader(r);
            group.RowCount.ShouldBe(RowsPerGroup);
            reader
                .Schema.GetDataFields()
                .Count(f =>
                    group
                        .GetMetadata(f)!
                        .MetaData!.Encodings.Exists(e =>
                            e
                                is Parquet.Meta.Encoding.RLE_DICTIONARY
                                    or Parquet.Meta.Encoding.PLAIN_DICTIONARY
                        )
                )
                .ShouldBeGreaterThan(0, $"{shapeName} row group {r} has no dictionary page.");
        }
    }

    [Fact]
    public void MatrixSizeIsWhatTheReaderSurfaceImplies()
    {
        int supported = Shapes.Sum(s =>
            RoutesOf(SurfaceOf(s.Model)).Count(r => !r.IsRejectedByDesign)
        );
        int rejected = Shapes.Sum(s =>
            RoutesOf(SurfaceOf(s.Model)).Count(r => r.IsRejectedByDesign)
        );

        // flat: Stream x {-, Where} x {Array, Async, Batches} minus Where.Batches = 5, Memory adds
        // Parallel.ToArrayAsync = 6; each compound shape has no AsBatches: 4 + 5.
        supported.ShouldBe(11 + 9 + 9);
        RouteCells.Count.ShouldBe(supported);
        Cells.Count.ShouldBe(Limits.Length * supported);
        CorruptionCells.Count.ShouldBe(Corruptions.Length * supported);
        RejectedRoutes.Count.ShouldBe(rejected);
        rejected.ShouldBeGreaterThan(0);
    }

    // ---------------------------------------------------------------- execution

    private sealed record Outcome(
        int Rows,
        Exception? Exception,
        bool TimedOut,
        long AllocatedBytes
    );

    private static void AssertBounded(string cell, Outcome outcome)
    {
        outcome.TimedOut.ShouldBeFalse($"{cell}: did not finish within {ReadTimeout}.");
        (outcome.Exception is OutOfMemoryException).ShouldBeFalse(
            $"{cell}: exhausted memory rather than rejecting the file."
        );
        outcome.AllocatedBytes.ShouldBeLessThan(
            AllocationCeilingBytes,
            $"{cell}: allocated {outcome.AllocatedBytes} bytes reading a file of a few kilobytes."
        );
    }

    private static async Task<Outcome> RunAsync(
        Shape shape,
        Route route,
        ParquetSerializerOptions options,
        byte[]? file = null
    )
    {
        byte[] bytes = file ?? await shape.BytesAsync();
        ReaderSurface surface = SurfaceOf(shape.Model);
        using var cts = new CancellationTokenSource(ReadTimeout);

        Task<AllocationMeasurementResult<Outcome>> read = Task.Run(
            () =>
                AllocationMeasurement.MeasureAsync(() =>
                    AttemptAsync(surface, route, bytes, options, cts.Token)
                ),
            CancellationToken.None
        );

        Task finished = await Task.WhenAny(read, Task.Delay(ReadTimeout + TimeSpan.FromSeconds(5)));
        if (finished != read)
        {
            return new Outcome(0, null, TimedOut: true, 0);
        }

        AllocationMeasurementResult<Outcome> measurement = await read;
        return measurement.Result with { AllocatedBytes = measurement.AllocatedBytes };
    }

    private static async Task<Outcome> AttemptAsync(
        ReaderSurface surface,
        Route route,
        byte[] bytes,
        ParquetSerializerOptions options,
        CancellationToken cancellationToken
    )
    {
        using var stream = new MemoryStream(bytes, writable: false);
        try
        {
            object source = route.Source == "Stream" ? stream : new ReadOnlyMemory<byte>(bytes);
            object reader = Invoke(surface.Sources[route.Source], null, source);
            reader = Invoke(surface.Modifiers[WithOptionsModifier], reader, options);
            foreach (string modifier in route.Modifiers)
            {
                MethodInfo method = surface.Modifiers[modifier];
                reader = modifier switch
                {
                    WhereModifier => Invoke(method, reader, AcceptEveryRowGroup(method)),
                    ParallelModifier => Invoke(method, reader),
                    _ => throw new InvalidOperationException(
                        $"No way to apply modifier {modifier}."
                    ),
                };
            }

            MethodInfo terminal = surface.Terminals[route.Terminal];
            object result = Invoke(terminal, reader, cancellationToken);
            int rows = await DrainAsync(result, cancellationToken);
            return new Outcome(rows, null, TimedOut: false, 0);
        }
        catch (Exception ex)
        {
            return new Outcome(0, ex, TimedOut: false, 0);
        }
    }

    /// <summary>
    /// <c>m => true</c> for the reader's metadata type, so <c>Where</c> prunes nothing and every
    /// row group still reaches the guards.
    /// </summary>
    private static Delegate AcceptEveryRowGroup(MethodInfo where)
    {
        Type predicate = where.GetParameters().Single().ParameterType;
        Type metadata = predicate.GetGenericArguments()[0];
        return Expression
            .Lambda(predicate, Expression.Constant(true), Expression.Parameter(metadata, "m"))
            .Compile();
    }

    private static object Invoke(MethodInfo method, object? target, params object[] args)
    {
        try
        {
            return method.Invoke(target, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static bool IsDrivable(Type returnType) =>
        ElementOf(returnType, typeof(Task<>)) is { IsArray: true }
        || ElementOf(returnType, typeof(IAsyncEnumerable<>)) is not null;

    private static Type? ElementOf(Type type, Type generic) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == generic
            ? type.GetGenericArguments()[0]
            : null;

    private static readonly MethodInfo DrainEnumerableMethod =
        typeof(HostileReadPathTests).GetMethod(
            nameof(DrainEnumerableAsync),
            BindingFlags.NonPublic | BindingFlags.Static
        )!;

    /// <summary>Runs a terminal's result to completion and counts the rows it produced.</summary>
    private static async Task<int> DrainAsync(object result, CancellationToken cancellationToken)
    {
        Type type = result.GetType();
        if (result is Task task)
        {
            await task;
            return ((Array)task.GetType().GetProperty("Result")!.GetValue(task)!).Length;
        }

        Type? element = type.GetInterfaces()
            .Select(i => ElementOf(i, typeof(IAsyncEnumerable<>)))
            .FirstOrDefault(e => e is not null);
        if (element is null)
        {
            throw new InvalidOperationException($"No way to drain a terminal returning {type}.");
        }

        return await (Task<int>)
            DrainEnumerableMethod
                .MakeGenericMethod(element)
                .Invoke(null, [result, cancellationToken])!;
    }

    /// <summary>
    /// Counts rows, or sums <c>RowCount</c> for a columnar batch, which is borrowed and must be read
    /// before the enumerator advances.
    /// </summary>
    private static async Task<int> DrainEnumerableAsync<T>(
        IAsyncEnumerable<T> items,
        CancellationToken cancellationToken
    )
    {
        PropertyInfo? rowCount = typeof(T).GetProperty("RowCount", typeof(int));
        int rows = 0;
        await foreach (T item in items.WithCancellation(cancellationToken))
        {
            rows += rowCount is null ? 1 : (int)rowCount.GetValue(item)!;
        }

        return rows;
    }
}

/// <summary>
/// The list shape of <see cref="HostileReadPathTests"/>: <see cref="ListRow"/>'s repeated columns plus
/// a top-level string, because Parquet.Net never dictionary-encodes a repeated column and the
/// dictionary guard needs a dictionary page to bound.
/// </summary>
[ParquetSerializable]
public sealed partial record HostileListRow
{
    public int Id { get; init; }
    public string? Label { get; init; }
    public List<string?>? Tags { get; init; }
    public List<int>? Scores { get; init; }
}
