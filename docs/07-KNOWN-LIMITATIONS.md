# 07 - Known Limitations & Remediation Plan

An audit of the generator as it stands, recording what does not work, why, and what closing each
gap requires. Unlike the other documents in this folder — which describe intended design — this one
describes **observed behaviour**.

Findings come from reading `TargetParser`, `CodeEmitter` and the emitted code, and from inspecting
the published Parquet.Net packages on nuget.org. Items are marked ✅ once fixed, with the closing
commit's behaviour described inline.

**Status key**: 🔴 broken / emits wrong or uncompilable code · 🟠 silently does nothing ·
🟡 missing capability · ⚪ hygiene or documentation drift

---

## 1. Parquet.Net version support and .NET Framework

> **Section 1 status.** The classic backend now exists as
> `Parquet.SourceGenerator.V5`, so 1.1–1.3 are closed as *shipped* rather than as *analysis*. The
> findings below are kept because they are what determined the package's shape — in particular that
> it targets the v4/v5 `DataColumn` API rather than being a retarget of the v6 emitter. What that
> package got wrong on its first cut is recorded in [section 6](#6-classic-v4v5-backend).

### 1.1 A `.V5` package would not restore net472 support ✅

The assumption that a Parquet.Net 5.x-targeting variant retains .NET Framework support is
incorrect. TFMs as published:

| Parquet.Net | Ships `lib/` for | net472-capable |
|:---|:---|:---:|
| 6.0.3 | `net8.0`, `net10.0` | ✗ |
| 5.6.1 | `net8.0`, `net10.0` | ✗ |
| 4.25.0 | `netstandard2.0`, `netstandard2.1`, `net6.0`, `net8.0` | ✓ |
| 3.10.0 | `netstandard2.0`, `net5.0`, `net6.0` | ✓ |

**4.25.0 is the last net472-capable release.** Its dependency chain is netstandard2.0-clean
(IronCompress 1.5.2, Microsoft.Data.Analysis 0.21.1, Microsoft.IO.RecyclableMemoryStream 3.0.1),
and its nuspec carries an explicit `.NETStandard2.0` dependency group pulling
`System.Threading.Tasks.Extensions` — .NET Framework consumption is deliberate, not accidental.

### 1.2 The API break is between v5 and v6, not v4 and v5 ✅

Comparing the public API in each package's shipped `Parquet.xml`:

| Concern | v4.25.0 / v5.6.1 | v6.0.3 |
|:---|:---|:---|
| Column write | `WriteColumnAsync(DataColumn, …)` | `WriteAsync<T>(DataField, ReadOnlyMemory<T>, …)` |
| Column read | `ReadColumnAsync(DataField, …) → DataColumn` | `ReadAsync<T>(DataField, Memory<T>, …)` |
| Writer disposal | `Dispose` (v5 adds `DisposeAsync`) | `DisposeAsync` |
| Reader disposal | `Dispose` only | `DisposeAsync` |
| Compression | `ParquetWriter.CompressionMethod` property | `ParquetOptions.CompressionMethod` |

v4 and v5 are one API family; v6 introduced the `Memory<T>` buffer API this generator is built on.

The consequence: **a `.V4` backend is not a retarget, it is a second emitter.** The upside is that
one `DataColumn`-based backend covers v4 *and* v5, and the same work unlocks net472. Suggested
naming is `Parquet.SourceGenerator.V4` (or `.Classic`) rather than `.V5`.

Emitter changes required for a classic backend:

- `WriteAsync<T>(field, memory)` → `WriteColumnAsync(new DataColumn(field, array))`
- `ReadAsync<T>(field, memory)` → `ReadColumnAsync(field)`, then read `.Data` / `.DefinedData`
- `await using` → `using` (no `DisposeAsync` on the v4 reader)
- Compression moves from `ParquetOptions` onto the writer

The ArrayPool zero-allocation story does not survive this — `DataColumn` allocates its own arrays —
so the classic package should not inherit the main package's performance claims.

**Shipped as `Parquet.SourceGenerator.V5`** — the name kept from the original suggestion even though
the backend covers v4 and v5 alike. It reuses the shared parser and models and supplies its own
emitter, which is the split this finding predicted. It offers write, batched write and read; there
is no streaming or parallel reader on the classic API.

### 1.3 net472 constraints beyond Parquet.Net itself ✅

Three issues independent of which Parquet.Net version is chosen:

- The generated code uses `IAsyncEnumerable<T>` and `[EnumeratorCancellation]`, requiring
  `Microsoft.Bcl.AsyncInterfaces` and `System.Memory` on net472 — or those APIs must be `#if`'d out
  of the net472 emit.
- **IronCompress ships no `win-x86` native binary** (only `linux-x64`, `linux-arm64`, `osx-arm64`,
  `win-x64`). 32-bit .NET Framework applications fail at runtime on any compressed write. `runtimes/`
  RID assets also do not resolve at all under `packages.config`.
- `IsExternalInit` is `internal` in `Parquet.SourceGenerator.Attributes`, so net472 consumers using
  `init` accessors need their own copy.

**Addressed, with one of the three findings turning out to be the load-bearing one.** The classic
emitter uses neither `IAsyncEnumerable<T>` nor `[EnumeratorCancellation]`, so the first bullet
resolved itself by the backend offering no streaming reader. The IronCompress `win-x86` gap is
unchanged and now recorded in the CHANGELOG's known gaps rather than only here — it is a runtime
property of a dependency, not something this repository can close.

A fourth constraint went unnoticed until CI was made to compile the target: **net472's default
`LangVersion` is 7.3**, which predates switch expressions, `??=` and nullable annotations — all of
which the generated code uses. Consuming projects need `<LangVersion>latest</LangVersion>`. This is
exactly the class of failure that only compiling for net472 can catch, and nothing did until now
(see 6.5).

### 1.4 The `IsExternalInit` polyfill missed netstandard2.1 ✅

`IsExternalInit` reached the BCL in .NET 5, so netstandard2.0 **and** netstandard2.1 both lack it.
The polyfill in the Attributes assembly was guarded on `#if NETSTANDARD2_0` alone, leaving the
netstandard2.1 target with no polyfill — the first `init` accessor or positional record added to
that assembly would have broken that one TFM and no other. Found while evaluating whether
`ParquetSerializerOptions` could become init-only (3.3); the guard now names both.

---

## 2. Correctness

### 2.1 `ReadParquetParallelAsync` performs no parallel work ✅

`CodeEmitter.EmitReadParallelAsync` computes `targetParallelism` and never reads it again. The
emitted body is a sequential `for (int r = 0; r < rgCount; r++)` loop; the emitter contains no
`Task.Run`, `Parallel.For` or `Task.WhenAll` anywhere. Both the `maxDegreeOfParallelism` parameter
and `ParquetSerializerOptions.MaxDegreeOfParallelism` are inert.

The README ("Multi-core parallel read across row groups"), CHANGELOG ("distributes object
construction across row groups") and the generated XML doc comment all claim otherwise.

**Resolved with design 2 below**, scoped to the `ReadOnlyMemory<byte>` overload. The `Stream`
overload remains sequential and says so, because the constraint there is real and not a matter of
effort: an arbitrary `Stream` cannot be handed to more than one reader.

The shape of the implementation:

- A single cheap probe reader reads the footer to learn the row-group layout and total row count,
  which is what lets the result array be allocated once and indexed by row-group offset.
- Each worker constructs its own `MemoryStream` over the same buffer — `CreateBufferStream` already
  returns an independent stream per call — and its own `ParquetReader`. Decompression and decoding
  therefore overlap, which is where the cost is.
- Row groups are claimed with an `Interlocked.Increment` on a shared cursor rather than split into
  fixed per-worker ranges. Fixed ranges idle a worker whenever row groups differ in size, which is
  common in files written by anything other than a single `WriteParquetBatchedAsync` call.
- Workers write into disjoint index ranges of the result array, so nothing beyond claiming a group
  needs synchronising, and results come back in file order for free.
- `Task.Run` is load-bearing rather than ceremony: reads over a `MemoryStream` complete
  synchronously, so invoking the worker method directly would run each one to completion on the
  calling thread and the "parallel" loop would be sequential.
- Worker count is `min(requested, rowGroupCount)`, so a single-row-group file takes a branch that
  stays on the calling thread rather than paying for a thread-pool hop and a second reader.
- The buffer is normalised to an array once, up front. `CreateBufferStream` copies whenever the
  memory is not array-backed, and every worker calls it — so without this a `MemoryManager`-owned or
  native buffer was copied once per worker plus once for the probe, materialising the file N+1 times.
  That is exactly the allocation profile the buffer overload exists to avoid. Array-backed buffers,
  which is what `File.ReadAllBytes` and `MemoryStream.ToArray` produce, pay nothing for the check.

The tests deliberately assert ordering, completeness and equivalence with the sequential reader
rather than a wall-clock speedup, which would be flaky on a loaded or single-core runner.

Why the sequential form was a deliberate fix rather than an oversight: a single `ParquetReader`
seeks within its `Stream`, so overlapping row-group reads corrupt one another. That left two
designs:

1. **Parallelise materialisation only** — decode each row group sequentially, then hand the
   `new T { … }` loop to the thread pool. Cheap to build, but the parallelised part is a tight loop
   of field copies while the sequential part does decompression and decoding. Amdahl's law caps the
   win at a few percent, and it buys that with thread-pool hops and buffer-ownership transfer out of
   the `ArrayPool` `finally`.
2. **One reader and one stream per worker** — parallelises decode, which is where the cost actually
   is. This is what a real parallel Parquet reader does, and it is the design worth building. It
   requires a seekable, shareable source, so it cannot be offered on an arbitrary `Stream`; the
   natural home is the `ReadOnlyMemory<byte>` overload (see 3.5), where each worker can cheaply get
   its own view over the same buffer.

### 2.2 The read path reallocates its result list on every row group ✅

`EmitReadAsync` constructs `results` with capacity equal to the *total* row count, then emits
`results.Capacity = results.Count + rowCount;` inside the row-group loop. `List<T>.Capacity`
reallocates whenever the assigned value differs from the current backing array length, so each row
group allocates a smaller array and copies into it — repeatedly — before the list grows back.

Single-row-group files are unaffected. Multi-row-group files (exactly what `WriteParquetBatchedAsync`
produces) pay O(groups × rows) of copying. This is the most likely cause of the published benchmark
showing reads at 1.25× the reflection baseline's time with 2.36× its allocations.

### 2.3 Positional records emit uncompilable code ✅

The emitter always materialises via object initializer (`new {ClassName} { Prop = … }`). A positional
record synthesises a primary constructor and a copy constructor but **no parameterless constructor**,
so the emitted code fails with CS7036.

`[ParquetSerializable] public partial record Person(int Id, string Name);` — the shape used as the
example in `04-ROADMAP-AND-CONTRIBUTING.md` — is affected. No test covers a positional record; every
test model declares property bodies.

**Resolved** by `PARQ008`, which fires when a reference type has no accessible parameterless
constructor — covering positional records and any class whose only constructors take arguments.
Value types are exempt, since they always have one. Constructor-based materialisation was the richer
alternative but is a much larger change; rejecting the shape with a message that names the fix is
the honest interim.

### 2.4 Get-only properties and readonly fields emit uncompilable code ✅

`TargetParser` performs no settability check — there is no inspection of `SetMethod`, `IsReadOnly` or
`IsRequired`. A `public int Id { get; }` is collected and then assigned in an object initializer,
producing CS0200 inside generated code with no diagnostic pointing at the cause.

**Resolved** by `PARQ007`. A property needs a set or init accessor reachable from the generated
extension class — which sits beside the type rather than inside it, so `private` and `protected`
setters do not count — and a field must be neither `readonly` nor `const`.

### 2.5 Inherited members are silently dropped ✅

`TargetParser` uses `typeSymbol.GetMembers()`, which returns declared members only. A
`[ParquetSerializable]` type deriving from a base that carries columns loses every inherited member,
with no warning.

**Resolved** by walking `BaseType`. Two deliberate choices: the walk stops at the first base not
declared in source, so a model deriving from a framework type does not acquire `Exception.Data` and
friends as columns; and members are collected base-first with a derived declaration replacing a
shadowed base one *in the base's position*, so adding an `override` changes which declaration is
used without reordering the schema.

### 2.6 Nested and generic types are unhandled ✅

`ClassName` is `typeSymbol.Name`, so:

- A **nested type** emits a namespace-level extension class referencing an unqualified name that
  cannot resolve from that scope.
- A **generic type** emits `Foo` without its type parameters.
- The `AddSource` hint name is namespace + class name only, so two same-named nested types under one
  namespace collide — the same class of failure the namespace-qualification fix addressed.

**Resolved** with diagnostics rather than emission: `PARQ009` for nested types, `PARQ010` for
generic ones, and emission is suppressed for both. Supporting nested types is a genuine future
feature — the emitter would need to carry the containing-type path and flatten it into the extension
class name — but rejecting with a message that names the fix beats emitting code that does not
compile. Generic types are a harder no: the schema is a single `static readonly` field and cannot
vary by type argument.

### 2.7 `DateTimeOffset` is claimed but does not round-trip ✅

`TargetParser.ClassifyKind` maps `System.DateTimeOffset` to `PropertyKind.DateTime`, which emits a
`DateTimeDataField` whose CLR type is `DateTime`, while the write call is `WriteAsync<DateTimeOffset>`
over `ReadOnlyMemory<DateTimeOffset>`. The types disagree, and the offset would be lost even if the
call bound. There is no test coverage for `DateTimeOffset` anywhere in the repository.

**Resolved** by removing the mapping, so `DateTimeOffset` now reports as `PARQ006`. Parquet.Net's
`SupportedTypes` has no entry for it, and silently normalising to UTC would discard the offset
without the caller asking for that. Callers who want it should store a `DateTime` plus an explicit
offset column.

### 2.8 Unsupported property types fail at runtime, not compile time ✅

`ClassifyKind` funnels everything unrecognised into `PropertyKind.Primitive` and emits
`typeof(X)` for Parquet.Net to reject at runtime. Affected: `List<T>` and other collections, nested
POCOs, `char`, `sbyte`, `ushort`, `uint`, `ulong`, `DateOnly`, `TimeOnly`, `BigInteger`.

**Resolved** by `PARQ006`. The allowlist is mirrored from Parquet.Net 6's
`Parquet.Encodings.SchemaEncoder.SupportedTypes` rather than invented, so the diagnostic can never
fail a build that would otherwise have worked — an over-narrow list would have been worse than no
list at all. Unresolved (error) types are skipped rather than reported, so the rule stays quiet
while code is being typed.

Correcting the original finding: `byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong`, `DateOnly`,
`TimeOnly` and `BigInteger` were listed here as failing, but Parquet.Net supports all of them and
they work through the passthrough today. What genuinely has no representation is `char`,
`DateTimeOffset`, arrays other than `byte[]`, collections, and nested user types.

### 2.9 Every reference-type column is forced nullable ✅

`TargetParser` computes `isNullable = memberType.IsReferenceType || NullableAnnotation == Annotated`.
A non-nullable `string Name` under `#nullable enable` therefore emits `isNullable: true`, so the
required/optional distinction is lost in the written schema. Downstream consumers (Spark, Athena,
PyArrow) do read that distinction.

**Resolved**: the annotation is now authoritative wherever the compilation has nullable analysis
switched on. In an oblivious context (`NullableAnnotation.None`) nothing can be inferred about a
reference type, so the conservative optional column is kept.

This is a **behaviour change**, and the one place in this series where a build that worked can start
failing at runtime: a `string Name` under `#nullable enable` now produces a *required* column, so
writing an actual null into it throws from Parquet.Net rather than being silently accepted. That is
the point — the annotation was a promise the schema was not recording — and it matches how EF Core
treats reference-type nullability. Declaring the member `string?` restores the optional column.

---

## 3. Options and API surface

### 3.1 `ParquetSerializerOptions` does nothing on any read path ✅

`EmitReadAsync`, `EmitReadParallelAsync` and `EmitReadStreamAsync` each emit
`options ??= …Default;` and then never reference `options` again — `ParquetReader.CreateAsync` is
called without it. Every setting silently no-ops on read.

**Resolved** by passing `BuildFormatOptions(options)` to all three `ParquetReader.CreateAsync`
calls. This is plumbing rather than a behaviour change today: `ParquetSerializerOptions` currently
carries only write-side settings, so the constructed `ParquetOptions` matches the reader's defaults.
What it buys is that any read-relevant option added later reaches the reader instead of being
silently dropped — see 3.7.

### 3.2 Row-group sizing uses a magic-number sentinel ✅

`options.RowGroupSize > 0 && options.RowGroupSize != 50_000 ? options.RowGroupSize : rowGroupSize`

Setting `RowGroupSize = 50_000` explicitly is indistinguishable from leaving it at its default, and
the method parameter silently wins. A nullable `int?` default expresses "unset" without a sentinel.
`MaxDegreeOfParallelism` uses the same `> 0` pattern.

**Resolved** for row-group sizing: the parameter is now `int?`, precedence is explicit argument →
options → the options default, and a non-positive value from either source throws
`ArgumentOutOfRangeException`. `MaxDegreeOfParallelism` is left alone deliberately — it is inert
until 2.1 is addressed, and giving it real precedence rules before it does anything would only
enshrine behaviour that does not exist.

### 3.3 `ParquetSerializerOptions.Default` is a mutable shared singleton ✅

`public static ParquetSerializerOptions Default { get; } = new();` exposes `{ get; set; }`
properties, so any consumer can mutate process-global defaults for every serializer in the
application.

**Resolved** by making `Default` return a fresh instance per access. Init-only properties were the
other candidate and were rejected: this assembly targets netstandard2.0/2.1, neither of which
carries `IsExternalInit`, so init-only would break object-initializer use for exactly the .NET
Framework consumers section 1 is about.

### 3.4 `SchemaName` is a dead public API ✅

`ParquetSerializableAttribute.SchemaName` is public and documented, is parsed by `TargetParser`, is
carried on `TargetClassModel` — and is never read by `CodeEmitter`. `TargetClassModel.IsRecord` and
`IsValueType` are likewise captured and unused.

**Resolved by removal.** It could not have been wired up: Parquet.Net's `ParquetSchema` has no name
to set — both constructors take fields and nothing else. `IsRecord` and `IsValueType` went with it.
Dead state on the model was not merely untidy: it still took part in the incremental pipeline's
equality comparison, so a change to any of it invalidated the cache and re-ran generation for no
difference in output.

### 3.5 Memory overloads are asymmetric, and one leaks ✅

`ReadParquetAsync(ReadOnlyMemory<byte>)` constructs a `MemoryStream` it never disposes (CA2000).
It is also the only memory overload — `ReadParquetParallelAsync` and `ReadParquetStreamAsync` have
no equivalent.

**Resolved.** The leak first: the method awaits inside a `using`, keeping ownership where the stream
is created rather than handing it to a caller who never sees it. `ReadParquetParallelAsync` and
`ReadParquetStreamAsync` then gained matching buffer overloads over a shared `CreateBufferStream`
helper. The streaming one iterates inside its `using` rather than returning the inner sequence, so
the stream lives exactly as long as enumeration does — including when a consumer breaks out early,
which disposes the iterator and runs the `using`.

This is also the entry point the real parallel reader should be built on (see 2.1): a buffer can
cheaply give each worker its own view over the same bytes, which an arbitrary `Stream` cannot.

### 3.6 `[ParquetColumn]` cannot express order without a name ✅

The attribute has only a name-taking constructor, so `[ParquetColumn(Order = 2)]` — reordering a
column without renaming it — is impossible.

**Resolved** with a parameterless constructor and a nullable settable `Name`. This uncovered a
second defect: the parser read named arguments *only when a constructor argument was also present*,
so even with a parameterless constructor the `Order` would have been silently ignored. `Name` is now
accepted as a named argument too.

### 3.7 `ParquetOptions.CompressionLevel` is not exposed ✅

`ParquetSerializerOptions` surfaces `CompressionMethod` but not the compression level Parquet.Net
accepts alongside it.

**Resolved** with a nullable `CompressionLevel`. Nullable rather than defaulted so "unspecified"
stays distinguishable from every legal value — the mistake 3.2 was about — and so an unset level
leaves Parquet.Net's own default (`SmallestSize`) in place instead of this generator quietly picking
one. The enum is declared locally rather than reusing `System.IO.Compression.CompressionLevel`,
whose `SmallestSize` member only exists from .NET 6 and so is not available to this assembly's
netstandard targets.

---

## 4. Documentation drift ✅

- `CHANGELOG.md` states "Nothing has been released yet"; **0.0.1 is published on nuget.org**
  (alongside `0.0.1-dev.1` and `0.0.1-dev.2`).
- `CHANGELOG.md` lists `MaxDegreeOfParallelism` among options that are "all applied" (see 2.1).
- `README.md` says "Not benchmarked" three lines above an embedded benchmark table.
- `04-ROADMAP-AND-CONTRIBUTING.md` leaves phases 2–5 entirely unticked despite being shipped, and
  still lists "Update Parquet.Net to 4.x / 5.x" as pending while the repo targets 6.0.3.
- `release.yml`'s package-layout check verifies `netstandard2.0;netstandard2.1;net8.0` for the
  Attributes package, but the csproj also targets `net9.0`.
- `INDEX.md` says "Native AOT … Not yet verified — CI does not run an AOT publish", but `ci.yml`
  publishes and executes the AOT test binary on every run.
- `02-API-DESIGN-AND-ATTRIBUTES.md` documents `SchemaName`, and
  `03-INCREMENTAL-GENERATOR-PIPELINE.md` shows `IsRecord`/`IsValueType` on the model. Both are gone
  (see 3.4).

**All resolved.** The roadmap now marks phases 1–5 shipped, records that the 4.x/5.x plan was
superseded by v6 and why, and adds a phase 6 for the classic backend and real parallel reading.

---

## 5. Remediation order

Sequenced so that each step is independently shippable and the cheap high-impact fixes land first.

| # | Item | Change |
|:---|:---|:---|
| 1 | 2.2 | ✅ Removed the in-loop `Capacity` assignment |
| 2 | 3.1, 3.2, 3.3 | ✅ Threaded options through reads; dropped the sentinel; `Default` is now per-access |
| 3 | 2.1 | ✅ Real parallelism on the buffer overload — a reader and stream per worker, row groups claimed dynamically |
| 4 | 2.8 | ✅ `PARQ006`, allowlist mirrored from Parquet.Net's `SupportedTypes` |
| 5 | 2.3, 2.4 | ✅ `PARQ007` unassignable member, `PARQ008` no parameterless constructor |
| 6 | 2.6 | ✅ `PARQ009` nested, `PARQ010` generic |
| 7 | 2.5 | ✅ Walk base types for inherited members |
| 8 | 2.7, 2.9 | ✅ `DateTimeOffset` reported by `PARQ006`; nullable annotations honoured |
| 9 | 3.4, 3.5, 3.6, 3.7 | ✅ `SchemaName` removed; buffer overloads for every reader; order-only `[ParquetColumn]`; `CompressionLevel` |
| 10 | 4 | ✅ Reconciled documentation with behaviour |
| 11 | 1.1, 1.2, 1.3 | ✅ `Parquet.SourceGenerator.V5` classic backend + net472 target |
| 12 | 6 | ✅ Correctness pass over that backend once it existed — see section 6 |

Items 4–6 were prerequisites for 11: the classic backend supports a narrower type set, so
compile-time rejection had to exist before a second backend could report it. That dependency was
real but under-served — 11 shipped reusing the v6 allowlist unchanged, and 12's `PARQ011` is what
actually closed it.

Every item in this audit is now closed. What remains is not remediation but work the audit
identified along the way, tracked in
[`04-ROADMAP-AND-CONTRIBUTING.md`](04-ROADMAP-AND-CONTRIBUTING.md): benchmarks have never been
re-run since the `Capacity` fix (2.2) or the parallel reader (2.1), so both performance claims are
reasoned rather than measured.


---

## 6. Classic (v4/v5) backend

Findings from auditing `Parquet.SourceGenerator.V5` after it shipped, against Parquet.Net 4.25.0's
actual API and `SchemaEncoder.SupportedTypes`. All are closed.

### 6.1 `byte[]` columns emitted code that does not parse ✅

The array-creation expression was built as `new {elementType}[count]`. `byte[]` is the one column
element type that is itself an array, so that produced `new byte[][count]` — not C#. The rank suffix
belongs after the length: `new byte[count][]`. Every model with a binary column produced a generated
file the compiler could not parse, and no test or consumption sample had a `byte[]` column.

### 6.2 Nullable enums could not round-trip ✅

The element type dropped the `?` for enums, so a nullable enum column built a non-nullable underlying
array and cast `(int)item.MaybeEnum` straight into it — throwing on a null. `DataColumn` would have
rejected it anyway: it compares the array's element type against
`DataField.ClrNullableIfHasNullsType` and throws on a mismatch, so the column could not have carried
the null even had the cast succeeded.

The general rule the fix encodes: nullable **value-type** columns need `T?[]`, reference-type columns
need the *unannotated* type, because there is no `Nullable<string>` and the annotation has no runtime
meaning.

### 6.3 Compression was accepted and discarded ✅

`BuildFormatOptions` populated a `ParquetOptions` and returned it — but this API generation keeps
compression on `ParquetWriter`: `CompressionMethod` is a property and `CompressionLevel` a field,
both set after the writer exists and before the first row group. A `Gzip` request wrote Snappy. This
is precisely what [1.2](#12-the-api-break-is-between-v5-and-v6-not-v4-and-v5-) predicted and the
first implementation carried over from the v6 emitter regardless.

`CompressionLevel.SmallestSize` arrived in .NET 6, and the generated code compiles inside the
consumer's project — so it is emitted behind `#if NET6_0_OR_GREATER`, falling back to `Optimal`,
which is the strongest level net472 and netstandard2.0 have. Naming it unguarded would have broken
exactly the consumers this backend exists for.

### 6.4 The reader was created without options ✅

The same defect [3.1](#31-parquetserializeroptions-does-nothing-on-any-read-path-) closed on the v6
side, reintroduced. Plumbing rather than behaviour today — v4's `ParquetOptions` carries only
decoding hints this generator does not expose — but it is what stops a read-relevant option added
later from silently vanishing.

### 6.5 net472 was declared but never compiled ✅

`PackageConsumptionV5` listed `net472` in its `TargetFrameworks`, but every CI step pinned
`--framework` to `net8.0` or `net9.0`, so the one target the package exists for was never built. It
could not have built: see the `LangVersion` finding in [1.3](#13-net472-constraints-beyond-parquetnet-itself-).
CI now compiles it — executing it would need Windows or Mono, but compiling is what catches the
failures that actually occur here.

### 6.6 The v6 type allowlist was too wide for v4 ✅

The shared parser mirrors Parquet.Net 6's `SchemaEncoder.SupportedTypes`. 4.25.0's list has no
`ReadOnlyMemory<byte>`, `ReadOnlyMemory<char>` or `BigDecimal`, so the classic backend emitted
columns that fail at runtime. `ParquetApiLevel` now threads the backend through the parser and
`PARQ011` reports the gap — deliberately a separate diagnostic from `PARQ006`, because the answer is
different: the member is fine, the backend is not, and the fix is to switch packages rather than to
change the model.

`DateOnly` and `TimeOnly` need no special case: 4.25.0 guards them behind `NET6_0_OR_GREATER`, and a
consumer old enough to lack them cannot name the types either, so the compiler rejects such a member
before the rule is reached.

### 6.7 Read-path hygiene ⚪ ✅

Row counts came from opening every row group and totalling `RowCount` — a second full pass whose only
product was an `int`, when `reader.RowGroups[r].RowCount` answers from metadata. Schema fields were
resolved *inside* the row-group loop, re-invoking the allocating `reader.Schema.GetDataFields()` on
every iteration; resolution is a property of the file, so it now happens once.

---
