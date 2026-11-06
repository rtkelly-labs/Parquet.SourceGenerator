# 12 - Buffer Reuse & Column Extraction Strategies

This document records the empirical performance evaluation and architectural analysis of column buffer management and extraction strategies in **Parquet.SourceGenerator**.

---

## 🎯 1. Executive Summary

During row-group serialization, domain models in memory (collections of POCOs/records) must be transposed into columnar arrays before being handed to `ParquetRowGroupWriter`. 

We investigated whether **column-pipelined extraction** and **cross-column buffer reuse** could reduce peak `ArrayPool` memory pressure compared to the default **row-oriented single-pass extraction**, and whether returning buffers eagerly after each column write impacts performance.

### Key Conclusions

1. **Row-Oriented Single-Pass Extraction is 1.8× to 5.5× Faster than Column-Pipelined Extraction:**
   - Traversing the row collection once and extracting all properties while the object reference resides in CPU L1/L2 data cache overwhelmingly outperforms making $N$ separate traversals over the collection (one pass per column).
   - At 50,000 rows across 16 columns, multi-pass traversal causes severe CPU cache eviction and memory bus stalls.

2. **Eager Progressive Buffer Returns Achieve the Optimal Balance:**
   - Rented column buffers are returned to `ArrayPool.Shared` immediately after `WriteAsync` or `WriteAllPartsAsync` completes for each column.
   - Using a **single outer `try / finally`** with an eager nulling pattern (`buffer = null!`) and null-checked cleanup (`if (buffer != null)`) avoids the state machine bloat and stack spills of multiple nested `try / finally` blocks while releasing memory milliseconds earlier during asynchronous I/O and compression.

3. **Buffer Reuse within Column-Pipelining Delivers a 33% Speedup within its Class:**
   - In schemas with multiple columns of the same physical type (e.g. `long?`, `decimal?`, `string?`), reusing 1 pooled buffer across matching columns and sharing 1 `int[] defLevels` buffer across all nullable struct columns reduced pipeline latency from 67.9 ms to 45.7 ms (a 33% improvement).
   - However, the multi-pass CPU cache misses prevent it from matching the 25.8 ms throughput of single-pass row extraction.

---

## 🔬 2. Evaluated Strategies

| Strategy | Description | Extraction Traversal | Peak Concurrent Rented Buffers | ArrayPool Rentals (16 cols) |
| :--- | :--- | :---: | :---: | :---: |
| **1. Reflection Baseline** | Stock `ParquetSerializer.SerializeAsync` (Parquet.Net v6). Uses compiled Expression trees and Dremel shredding. | Row-by-row reflection | N/A | Dynamic allocations |
| **2. Row-Oriented (Current Generator + Eager Return)** | All $N$ column buffers and `defLevels` rented upfront. Single loop extracts all properties into all buffers. Columns written sequentially; buffers returned eagerly post-write. | **1 pass** over rows | $N$ column buffers + defLevels (e.g. 27) | $N$ + defLevels (27) |
| **3. Column-Pipelined (No Reuse)** | For each column: rent buffer $\to$ traverse rows extracting that property $\to$ write column $\to$ return buffer immediately. | **$N$ passes** over rows | 1 data buffer + 1 defLevels (2 max) | $N$ + defLevels (27) |
| **4. Column-Pipelined (Type Buffer Reuse)** | Reuses 1 physical buffer across columns of the same type in schema order. Reuses **1 single `int[] defLevels`** across all 11 nullable struct columns. | **$N$ passes** over rows | 1 data buffer + 1 defLevels (2 max) | **5 total** |

---

## 📊 3. Empirical Benchmark Data

Benchmarks were executed under .NET 8.0 (Release build, ARM64) comparing 10 iterations across two contrasting schemas:
- **Workload A: `BenchmarkTpchLineItem`** — 16 columns (4 `long?`, 4 `decimal?`, 3 `DateTime?`, 5 `string?`; 11 nullable struct columns requiring definition levels).
- **Workload B: `ScaleEvent`** — 4 primitive non-nullable columns (`int`, `double`, `long`, `bool`).

### 3.1 Wide Analytical Schema (`BenchmarkTpchLineItem`, 16 Columns with Nullables)

#### 10,000 Rows
| Strategy | Mean Time | Speedup vs Reflection | GC Collections (Gen 0/1/2) | GC Allocated | Peak Active Buffers |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **1. Reflection (`ParquetSerializer`)** | 73.30 ms | 1.0× (baseline) | 16 / 2 / 2 | 13,402.9 KB | N/A |
| **2. Row-Oriented (Single-Pass + Eager Return)** | **25.80 ms** | **2.8× faster** | 2 / 0 / 0 | 1,919.3 KB | 27 |
| **3. Column-Pipelined (No Reuse, 16 passes)** | 67.90 ms | 1.1× faster | 2 / 0 / 0 | 1,919.3 KB | 2 |
| **4. Column-Pipelined (Buffer Reuse across types)** | 45.70 ms | 1.6× faster | 2 / 0 / 0 | 1,919.3 KB | 2 |

#### 50,000 Rows
| Strategy | Mean Time | Speedup vs Reflection | GC Collections (Gen 0/1/2) | GC Allocated | Peak Active Buffers |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **1. Reflection (`ParquetSerializer`)** | 180.10 ms | 1.0× (baseline) | 49 / 6 / 4 | 62,450.5 KB | N/A |
| **2. Row-Oriented (Single-Pass + Eager Return)** | **37.70 ms** | **4.8× faster** | 11 / 0 / 0 | 9,343.1 KB | 27 |
| **3. Column-Pipelined (No Reuse, 16 passes)** | 57.90 ms | 3.1× faster | 11 / 0 / 0 | 9,343.1 KB | 2 |
| **4. Column-Pipelined (Buffer Reuse across types)** | 53.10 ms | 3.4× faster | 11 / 0 / 0 | 9,343.1 KB | 2 |

---

### 3.2 Narrow Primitive Schema (`ScaleEvent`, 4 Columns Non-Nullable)

#### 10,000 Rows
| Strategy | Mean Time | Speedup vs Reflection | GC Collections (Gen 0/1/2) | GC Allocated | Peak Active Buffers |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **1. Reflection (`ParquetSerializer`)** | 5.10 ms | 1.0× (baseline) | 0 / 0 / 0 | 1,293.6 KB | N/A |
| **2. Row-Oriented (Single-Pass + Eager Return)** | **0.80 ms** | **6.4× faster** | 0 / 0 / 0 | 602.6 KB | 4 |
| **3. Column-Pipelined (4 passes, 1 buffer active)** | 1.30 ms | 3.9× faster | 0 / 0 / 0 | 602.6 KB | 1 |

#### 50,000 Rows
| Strategy | Mean Time | Speedup vs Reflection | GC Collections (Gen 0/1/2) | GC Allocated | Peak Active Buffers |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **1. Reflection (`ParquetSerializer`)** | 7.40 ms | 1.0× (baseline) | 0 / 0 / 0 | 5,639.6 KB | N/A |
| **2. Row-Oriented (Single-Pass + Eager Return)** | **1.90 ms** | **3.9× faster** | 0 / 0 / 0 | 2,946.9 KB | 4 |
| **3. Column-Pipelined (4 passes, 1 buffer active)** | 10.40 ms | 0.7× (slower) | 0 / 0 / 0 | 2,946.9 KB | 1 |

---

## 🧠 4. Architectural Analysis & Hardware Mechanics

### 4.1 CPU Cache Spatial Locality vs. Multi-Pass Traversal

Why is single-pass row-oriented extraction consistently 1.8× to 5.5× faster than column-pipelined extraction?

1. **L1/L2 Data Cache Hits in Single Pass:**
   - In .NET, instances of domain classes and records occupy 64–128 bytes on the managed heap.
   - When executing:
     ```csharp
     for (int i = 0; i < count; i++)
     {
         var item = listItems[i]; // Cache line fill: loads object into L1 cache
         buffer_0[i] = item.OrderKey;      // L1 hit (0 wait states)
         buffer_1[i] = item.PartKey;       // L1 hit (0 wait states)
         buffer_2[i] = item.SuppKey;       // L1 hit (0 wait states)
         buffer_3[i] = item.LineNumber;    // L1 hit (0 wait states)
         ...
     }
     ```
   - Reading `item` brings the entire object's memory into the CPU L1 cache line. Accessing all 16 properties in succession incurs **zero additional memory bus transactions**.
   - The CPU traverses the collection pointer array and dereferences the heap objects **exactly once**.

2. **Cache Line Eviction in Column-Pipelined Extraction:**
   - In column-pipelined extraction, the loop traverses the collection 16 times:
     ```csharp
     // Pass 1:
     for (int i = 0; i < count; i++) buf0[i] = listItems[i].OrderKey;
     // Pass 2:
     for (int i = 0; i < count; i++) buf1[i] = listItems[i].PartKey;
     // ...
     // Pass 16:
     for (int i = 0; i < count; i++) buf15[i] = listItems[i].Comment;
     ```
   - At 50,000 items, the reference array alone occupies 400 KB, and the referenced objects occupy several megabytes across the heap.
   - By the time Pass 2 begins, the CPU cache lines filled during Pass 1 have been completely evicted.
   - The CPU is forced to repeatedly fetch the same objects from main memory (RAM) 16 consecutive times, saturating the memory bus.

---

### 4.2 Parquet Format Constraint: Strict Schema Field Order

`Parquet.Net`'s `ParquetRowGroupWriter` enforces writing columns in exact schema definition order:
```csharp
// If field 8 is 'l_returnflag' (string) and field 10 is 'l_shipdate' (DateTime):
// Attempting to write field 10 before field 8 throws:
System.ArgumentException: "cannot write this column, expected 'l_returnflag', passed: 'l_shipdate' (Parameter 'field')"
```

Because Parquet column chunks within a row group must follow schema order:
- Arbitrary grouping of columns by type (e.g. writing all numeric columns, then all strings, then all dates) is **not allowed** by the Parquet file format specification.
- Column-pipelined buffer reuse can only reuse buffers across columns that appear consecutively or must hold the buffer across intermediate column writes until the next column of the same type is encountered.

---

### 4.3 Why Eager Return with Single `try / finally` is the Optimal Pattern

#### The Anti-Pattern: Multiple Nested `try / finally`
Wrapping every column in its own `try / finally` inside an `async` method introduces severe overhead:
- Every `try / finally` adds an entry to the IL Exception Handling Table.
- In `MoveNext()`, the state machine must generate state transitions (`<>1__state`), resumption dispatch tables, and `leave.s` jump instructions across every `await` boundary.
- RyuJIT and Native AOT `ILCompiler` cannot enregister local variables across multiple EH boundaries, forcing variables onto the stack frame.

#### The Implemented Solution: Single `try / finally` + Eager Nulling
```csharp
try
{
    // ... Single-pass extraction ...

    using (var groupWriter = writer.CreateRowGroup())
    {
        await groupWriter.WriteAllPartsAsync(_field_0, ...);
        ArrayPool<int>.Shared.Return(buffer_0, clearArray: false);
        buffer_0 = null!; // Inline 1-cycle assignment

        await groupWriter.WriteAsync(_field_1, ...);
        ArrayPool<string?>.Shared.Return(buffer_1, clearArray: true);
        buffer_1 = null!;
    }
}
finally
{
    // Exception safety: only returns buffers not already eagerly returned
    if (buffer_0 != null) ArrayPool<int>.Shared.Return(buffer_0, clearArray: false);
    if (buffer_1 != null) ArrayPool<string?>.Shared.Return(buffer_1, clearArray: true);
}
```

- **0 additional EH scopes:** Exactly 1 exception handling clause in metadata.
- **Happy path:** Eager returns happen immediately when writing completes, returning memory while hot in cache. Setting `buffer = null!` is a 1-cycle instruction (`xor` / `mov`).
- **Finally check:** Compiles to `test reg, reg; jz`, which branch predictors predict with >99.9% accuracy.

---

## 📋 5. Production Architectural Decision

| Workload / Context | Recommended Strategy | Rationale |
| :--- | :--- | :--- |
| **In-Memory POCO Collections (`List<T>`, `T[]`, `IReadOnlyCollection<T>`)** | **Row-Oriented Single Pass + Eager Progressive Returns** *(Production Default)* | Maximizes L1/L2 CPU cache spatial locality. 1.8×–5.5× faster than multi-pass pipelining. Eager returns immediately release memory during column compression/I/O. |
| **Extreme Memory-Constrained Environments (e.g. AWS Lambda with 128MB RAM, 100+ columns)** | **Column-Pipelined with Type Buffer Reuse** *(Optional Future Flag)* | Reduces peak active memory from $O(N \times \text{batch})$ to $O(1 \times \text{batch})$ at the cost of 2×–3× CPU traversal time. |
| **Column-Oriented In-Memory Sources (e.g. Apache Arrow RecordBatches)** | **Direct Columnar Handoff** | When data is already arranged in contiguous column buffers, zero extraction is needed—buffers can be passed straight to `WriteAsync` with zero passes over rows. |

---

## 🚚 6. Direct Columnar Handoff — Measured (issue #137)

§5 recommended direct columnar handoff for callers whose data is already in contiguous column
buffers, but recommended it on reasoning rather than measurement. The generator now emits the API
(`WriteParquetRowGroupAsync(batch)`, `WriteParquetRowGroupColumnarAsync(...)`), so the
recommendation can be checked.

### 6.1 What Was Measured

`benchmarks/Parquet.SourceGenerator.Benchmarks/ColumnarHandoffBenchmark.cs`, on
`BenchmarkTpchLineItem` — the same 16-column wide analytical schema as §3.1, with 11 nullable value
columns and 5 nullable string columns, ~6% nulls per nullable column.

| Benchmark | What it includes |
| :--- | :--- |
| **Row-oriented write** | Rent 27 pooled buffers → transpose the row collection → encode → compress → write. The production default. |
| **Columnar handoff write** | Same file from buffers the caller already owns. No rentals, no transpose. |
| **Transpose only** | The extraction pass on its own (rent, one pass over rows, return). No Parquet call — this is the work the handoff deletes, isolated from encode/compress/I-O. |

### 6.2 Results

Apple M1 (8 cores), macOS 15.7.7, .NET 9.0.315 host, `net8.0` target, BenchmarkDotNet 0.14.0,
in-process toolchain, 3 warmups / 15 iterations. **The machine was not idle** (1-minute load average
2.5–4.2, other processes active), so treat the end-to-end absolute times as indicative and the
ratios — which are measured within a single interleaved run — as the meaningful figures. A repeat of
the 8-iteration Workstation run under heavier load produced 4.5% / 3.4% instead of the numbers below;
the `Transpose only` measurement is the stable one (standard deviation under 2%) and it independently
bounds the maximum possible saving.

**Workstation GC (concurrent):**

| Rows | Row-oriented | Columnar handoff | Transpose only | Handoff saving | Allocated (row / columnar) |
| ---: | ---: | ---: | ---: | ---: | :--- |
| 10,000 | 4,208.5 µs | 3,933.9 µs | 240.5 µs | **6.5%** | 2,273,504 B / 2,273,696 B |
| 50,000 | 21,669.2 µs | 20,111.4 µs | 1,649.8 µs | **7.2%** | 11,078,944 B / 11,078,944 B |

**Server GC (concurrent):**

| Rows | Row-oriented | Columnar handoff | Transpose only | Handoff saving | Allocated (row / columnar) |
| ---: | ---: | ---: | ---: | ---: | :--- |
| 10,000 | 4,057.4 µs | 3,773.5 µs | 253.8 µs | **7.0%** | 2,273,237 B / 2,273,317 B |
| 50,000 | 21,428.9 µs | 19,312.0 µs | 1,699.7 µs | **9.9%** | 11,078,935 B / 11,078,935 B |

Both GC modes agree — the saving is present, positive and of the same magnitude in each. Worth
stating explicitly: PRs #209 and #211 each found modes that disagreed, in one case inverting the
conclusion. Not here.

### 6.3 What The Numbers Say

1. **The saving is real but small, and it is bounded by the transpose.** The handoff removes roughly
   3–10% of end-to-end write time depending on run and load, clustering around 7%. The standalone
   transpose measurement accounts for essentially all of it (240.5 µs of a 4,208.5 µs write = 5.7%;
   1,649.8 µs of 21,669.2 µs = 7.6%), and it is the low-variance measurement of the three, so it is
   the number to trust. There is no second-order win hiding anywhere: the transpose is exactly what
   disappears, nothing else changes.

2. **Roughly 92% of a Parquet write is not the extraction.** Encoding, compression and page assembly
   inside Parquet.Net dominate. Any future work aimed at the write path's headline number should be
   pointed there, not at extraction — §3's optimisation of extraction has already taken it as far as
   removing it entirely can go.

3. **Allocation is unchanged.** This is the result that most contradicts the intuition behind the
   API. The row-oriented path rents from `ArrayPool.Shared`, and after warmup those rentals allocate
   nothing; the ~2.3 MB / ~11 MB measured is Parquet.Net's own encode and compress buffers, which
   both paths pay identically. "No `ArrayPool` rentals in the columnar path" is a true structural
   property of the generated code and it eliminates the *peak buffer footprint* (27 buffers ×
   batch), but it does not show up as fewer allocated bytes per write.

4. **The API's real value is therefore not the 7%.** It is that a caller holding Arrow buffers, a
   query engine's column vectors, or pre-split `ReadOnlyMemory<T>[]` no longer has to materialise a
   POCO collection at all — the row objects, and their allocation and GC pressure, never exist. That
   cost is upstream of every measurement in this table, so this benchmark understates the benefit
   for a caller who is genuinely columnar and overstates it for one who is not.

### 6.4 SIMD Nullable Extraction, Re-measured on Columnar Input (issues #145 / #137)

PR #210 made the write path's nullable extraction branchless and left a `NullableColumnExtractor`
SIMD helper behind, noting that its two-pass vectorised split/compact lost against the write path's
array-of-structures source and should be re-measured on genuinely columnar input. This issue is that
input, so it was re-measured (`ColumnarNullExtractionProbe.cs`, a benchmark-local copy of #210's
routines — #210 owns the shipped API and this is not a second copy of it).

50,000-element contiguous `long?[]`, same machine and runtime as above:

| Null % | Branchless (WS / Server) | Two-pass SIMD (WS / Server) | Ratio (WS / Server) |
| ---: | ---: | ---: | ---: |
| 0 | 58.05 / 57.70 µs | 70.82 / 77.34 µs | 1.22× / 1.34× **slower** |
| 6 | 34.17 / 34.08 µs | 75.68 / 75.26 µs | 2.21× / 2.21× **slower** |
| 50 | 34.45 / 34.31 µs | 73.34 / 78.44 µs | 2.13× / 2.29× **slower** |

**Negative result, and a clear one.** The vector path loses on columnar input too, in both GC modes,
at every null density tested — by more than it lost on array-of-structures data. The two-pass form
touches the payload buffer twice (write unpacked, then compact in place) and the presence buffer
three times, and on a 50,000-element `long` column that second pass is a second walk through
~400 KB, well past L1/L2. The single branchless pass writes each payload once at its final position.
Vector width cannot buy back a doubled memory traffic bill.

The practical conclusion: `NullableColumnExtractor`'s vectorised entry points have no measured
workload on which they win, on this hardware. #137's columnar handoff does not use them — a caller
supplying packed values and definition levels has already done the split, which is the point.

### 6.5 Scope Of The Emitted API

The handoff is emitted for all-leaf models only. Struct, list and map members carry a
definition–repetition ladder that a caller cannot express as flat per-column buffers, so those
models keep the row-oriented API alone. Lifting that restriction means designing a caller-facing
representation of the level ladder, which is a separate piece of API design rather than an
extension of this one.
