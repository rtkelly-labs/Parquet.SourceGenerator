# 09 - Performance & Memory Triage with dotnet-dump

This guide explains the runtime memory diagnostic and dump triage workflow in **Parquet.SourceGenerator** using `dotnet-dump` and SOS commands.

---

## 🎯 Motivation

While [BenchmarkDotNet](./05-TESTING-STRATEGY-AND-BENCHMARKS.md) measures elapsed execution time and total allocated bytes per operation, it does not reveal:
1. **Live Managed Heap Topography**: Which objects survive Gen 0 collections and accumulate into Gen 1/Gen 2.
2. **Buffer Pool Leaks & Anchored References**: Verifying that `ArrayPool<T>` buckets and string deduplicators (`StringDeduplicator`) return rented buffers and do not anchor large byte/char arrays.
3. **Large Object Heap (LOH) & Pinned Object Heap (POH) Fragmentation**: Identifying buffers exceeding 85,000 bytes or pinned spans that cause fragmentation and GC pauses under analytical workloads (e.g. 1M+ rows).
4. **Hidden Ephemeral Boxing**: Identifying transient boxing objects that escape compile-time analyzers.

---

## 🛠️ Tooling Overview

`dotnet-dump` is registered in `.config/dotnet-tools.json`:

```bash
dotnet tool restore
```

To run `dotnet-dump` directly:
```bash
dotnet tool run dotnet-dump --help
```

---

## 🚀 Running the Automated Memory Triage Tool

We provide an automated memory triage script in `scripts/TriageMemoryDump.cs`. It can capture dumps from running processes, execute test workloads, and generate a comprehensive SOS diagnostic report.

### 1. Analyze an Existing Dump
If a memory dump was already captured during testing or CI:
```bash
dotnet run scripts/TriageMemoryDump.cs -- --analyze path/to/memory.dmp
```

### 2. Capture and Triage a Running Process
To capture a memory dump from an active benchmark or CLI process by PID:
```bash
dotnet run scripts/TriageMemoryDump.cs -- --pid 12345 --type Full --out temp/dumps
```

### 3. Launch Workload and Capture
To launch a specific workload command and capture a dump after an initial warmup:
```bash
dotnet run scripts/TriageMemoryDump.cs -- --command "dotnet run --project test/Parquet.SourceGenerator.CLI/Parquet.SourceGenerator.CLI.csproj -c Release" --delay 3
```

---

## 📊 Triage Report Artifacts

The tool writes output to `temp/dumps/`:
- **`MEMORY_TRIAGE_REPORT.md`**: Markdown summary containing:
  - GC Generation Sizes (`Gen 0`, `Gen 1`, `Gen 2`, `LOH`, `POH`)
  - Top 25 managed types ranked by total memory consumption
  - Dedicated breakdown for all `Parquet.*` types and internal buffer structures
- **`dump_*.dmp`**: The raw process core/memory dump file.

---

## 🔬 Interactive SOS Diagnostic Playbook

To explore a captured dump interactively, launch the SOS analyzer shell:
```bash
dotnet tool run dotnet-dump analyze temp/dumps/dump_cli.dmp
```

### Key SOS Commands for Parquet Codegen

| SOS Command | Purpose |
| :--- | :--- |
| `eeheap -gc` | Inspect heap size across all GC heaps, ephemeral segment sizes, LOH, and POH. |
| `dumpheap -stat` | Display count and total memory usage grouped by MethodTable / Type. |
| `dumpheap -type Parquet` | Filter heap objects to types containing `Parquet` (e.g. serializers, field readers). |
| `dumpheap -min 10000` | List individual heap objects exceeding 10 KB to detect buffer leaks or large arrays. |
| `gcroot <address>` | Trace the GC root chain anchoring an object to determine why it cannot be collected. |
| `poh` | Inspect Pinned Object Heap entries to detect pinned memory blocking compaction. |
| `clrstack -all` | View managed call stacks for all active threads. |
