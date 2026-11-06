# 15 - Nested Types: M0 Spike Findings

Empirical findings from the throwaway spike for [issue #176](https://github.com/rtkelly13/Parquet.SourceGenerator/issues/176)
(compound types). Every claim here was verified on this machine against Parquet.Net 6.1.0 and
Parquet.Net 4.25.0, with PyArrow 25.0.0 as an independent third-party reader. Spike sources lived
in `temp/` only; nothing here is product code.

This document exists so the M1+ implementation does not re-derive the record-shredding contract.

---

## 1. The level convention (both engines, standard Dremel)

Definitions used below: a leaf's `MaxDefinitionLevel` / `MaxRepetitionLevel` come from
`Field.MaxDefinitionLevel` / `Field.MaxRepetitionLevel`, computed by Parquet.Net itself via
`PropagateLevels` once the `StructField`/`ListField`/`MapField` tree is constructed. **The emitter
never computes level math itself — read it off the built tree.**

### 1.1 What each def level means

For an *optional* list of *optional* elements (`ListField` with nullable item), `maxDef = 3`:

| def | meaning |
|:---:|:---|
| 0 | list column value is null |
| 1 | list is present but **empty** |
| 2 | element present in the list but **null** |
| 3 | element present **with a value** |

Required element (`List<int>`): `maxDef = 2`, same ladder minus the element-null rung.

For an optional struct member, `maxDef = 2` (nullable leaf child): 0 = struct null,
1 = struct present / child null, 2 = child value present.

For `Dictionary<string, string?>` (`MapField`): key leaf `maxDef = 2` (0 = map null,
1 = empty map, 2 = key present — keys are always required), value leaf `maxDef = 3`.

### 1.2 The rep array

- `rep = 0` always starts a new row. For a value that continues a repeated group, `rep` equals
  **the depth of the repeated ancestor being continued** (0-based from the innermost). A single
  list depth therefore shows only 0s and 1s — but with nested repeated fields the levels go
  higher: `List<List<int>>` with row `[[1, 2], [3]]` has reps `[0, 2, 1]` (`2` continues the
  inner list, `1` closes it and starts the next inner list in the same row). The spike data
  covered only single-list-depth shapes; a `List<List<T>>` case must be added to the M3 matrix
  before the codec relies on the general rule.
- Level arrays have **one entry per value slot *including* nulls and empty markers**. A row whose
  list is null, empty, or a struct that is null contributes exactly **one** entry (its marker);
  the array grows beyond `rowCount` only when rows contribute *multiple* slots, i.e. lists with
  two or more elements (or map pairs). Empty-list and null-struct markers are each their row's
  single entry, not extra phantom slots.
- The packed value array contains only the `def == maxDef` entries, in order.

### 1.3 Worked example (row set: `[a,null,b] / [] / null`, plus struct `{NYC,10001} / null / {null,55}`)

```text
Tags/list/element:   defs = [3,2,3,1,0]        reps = [0,1,1,0,0]     values = ["a","b"]
Ship/City:           defs = [2,0,1]            reps = (no rep array)  values = ["NYC"]
Ship/Zip:            defs = [2,0,2]                                   values = [10001,55]
Meta key/value:      defs = [2,2,1,0] / [3,2,1,0]   reps = [0,1,0,0]  values = ["k1","k2"] / ["v1"]
```

Verified by feeding hand-built arrays of exactly these shapes to the write APIs and reading the
files back with **two authoritative readers**: the Parquet.Net 6 raw reader (`ReadRawAsync`,
levels and packed values identical) and PyArrow 25.0.0 (logical values identical, including the
`['a', None, 'b'], [], None` null-vs-empty distinction). Parquet.Net's own reflection serializer
*produced* the convention reference (Spike A) but its *reader* is excluded from validation — see
§2.6 for why it fails the nested cases.

### 1.4 Depth composition is additive

A list inside a struct (`Deep/Members/list/element` in the spike) showed
`maxDef = 4` = struct(+1) + list(+1) + element(+1) + null-vs-value(+1), with rep contributed only
by the repeated `list` group. Arbitrary nesting extends the def ladder additively; reassembly
thresholds derive from `maxDef` per leaf. Repetition is the part that is *not* uniform across
depth — §1.2's ancestor-depth rule governs, and nested repeated fields (`List<List<T>>`) need
their own test row in M3.

---

## 2. Parquet.Net 6.1.0 API facts

1. **`WriteAllPartsAsync<T>` is `where T : struct`.** String leaves are written as
   `ReadOnlyMemory<char>` (already the convention of the flat emitter) and binary as
   `ReadOnlyMemory<byte>`. Plain `string`/`byte[]` generic arguments do not compile.
2. **`WriteAllPartsAsync` already takes the rep-level slot**
   (`(DataField, ReadOnlyMemory<T>, ReadOnlyMemory<int>? def, ReadOnlyMemory<int>? rep, ct)`);
   the current emitter passes `null` there. Compound support is "start filling the slot that
   exists", not a new API surface.
3. **`ReadRawAsync<T>` is also `where T : struct`, and the values buffer must be sized to
   `NumValues` (entry count), NOT to the packed value count** — passing a buffer sized to the
   non-null count throws `ArgumentException: Values buffer is too small …`. Entry count comes
   from `rgReader.GetMetadata(field).MetaData.NumValues`. The **packed** (physical value) count
   is `NumValues - NullCount`, where `NullCount` from `rgReader.GetStatistics(field)` is the
   number of *null/absent entries* (`def < maxDef`), not the packed count. When statistics are
   absent (`GetStatistics` returns null) the packed count must be derived by scanning the def
   array for `def == maxDef` rather than assumed to equal `NumValues`. This is the read-side trap
   #1 for M2/M3.
4. **Struct groups are always `optional` in 6.1.0.** `StructField` exposes no `isNullable` ctor
   parameter, and the serializer emitted `optional group` even for a C# property that was never
   null (`Bill` in the spike). Consequence for #176: do **not** promise required groups; a
   non-nullable nested member means "def 0 is never written / is an error on read", and children
   carry +1 def level regardless. PARQ006-era docs that say "optional/required group mirrors the
   annotation" need this wording.
5. **`ParquetSchema.FindDataField(string)` throws on nested dotted paths** —
   `"tags.list.element"` fails with `data field not found`. Nested leaves are found by walking the
   tree or matching `field.Path.ToString()`, which uses **`/` separators and includes the
   intermediate `list` / `key_value` segments** (`"Tags/list/element"`). The v4-style resolution
   (`Path.ToString()` match) already in `SchemaComponent.ResolveSchemaField` generalizes to v6
   unchanged; do not introduce a dot-path FindDataField call.
6. **Parquet.Net's own reflection serializer loses nested-list interior nulls on read**
   (`[a,null,b]` came back as `[a|b]`; `[]` and `null` both came back as `[null]`) — it reads its
   own files this way, so it is an upstream defect, not a convention question. **Never use the
   reflection serializer as a nested read oracle in #176 tests; use raw levels + PyArrow.**
7. External (PyArrow-produced) nested files parse into the same `Field` tree shape the spike
   built by hand (`ListField` with `.Item`, `MapField` with `.Key`/`.Value`, slash `Path`s), so
   reading the checked-in `04_nested_lists_maps.parquet` fixtures needs no schema-translation
   shim.

---

## 3. Parquet.Net 4.25.0 (legacy backend) API facts

1. **`new DataColumn(field, array, int[])` binds the third array to *repetition* levels, not
   definition levels** — it throws `this column must not have any repetition levels` for a
   zero-rep field. **The emitter must always pass the 4-argument ctor**
   `new DataColumn(field, values, defLevels, repLevels)` and pass `null` explicitly for absent
   arrays. Trap discovered by triggering it, not by reading signatures.
2. `DataColumn` levels survive a full write→read cycle: `ReadColumnAsync` exposes
   `DefinitionLevels` / `RepetitionLevels`, and hand-built levels from §1.3 round-tripped
   identically.
3. **4.x returns value arrays aligned to the def ladder (null placeholders at
   `def < maxDef`), whereas 6.x `ReadRawAsync` returns packed values.** Two different reassembly
   input shapes for the same logical data; the legacy materializer reads the aligned array and can
   skip level-walking for the value lane, while the v6 materializer consumes packed values +
   levels. The per-backend components already differ here (v6 rents packed buffers).
4. String leaves in 4.x are plain `System.String` arrays (no `ReadOnlyMemory<char>`), matching
   the existing `useMemoryForTextAndBinary: false` legacy convention.
5. Schema-level `StructField`/`ListField`/`MapField` exist with identical level propagation;
   nested group names (`Tags/list/element`) match v6's, so the level convention section applies
   unchanged to both backends.

---

## 4. Consequences for the #176 design (feeds M1)

- §1's uniform ladder means `LevelCodecComponent` needs one algorithm, parameterized per leaf by
  (`maxDef`, `maxRep`, path annotation), not per compound kind.
- §2.3/§3.1 are the two buffer-sizing traps that will silently corrupt data if missed: v6 read
  buffer = entry count; legacy ctor = 4 args always.
- §2.4: optionality of struct groups is decided by the *engine*, not the model. The parser still
  records the C# annotation (needed for null-propagation semantics in generated reassembly) but
  the schema emission marks every group optional.
- §2.5: `ResolveSchemaField`'s existing `Path.ToString()` strategy (used today on v4 mode) is the
  correct cross-backend approach for nested leaves; no new resolution mechanism.
- §2.6: PyArrow-only as nested read oracle in the test matrix; Parquet.Net's reflection serializer
  is disqualified (loses interior nulls). Its *write* output remains trustworthy as a convention
  reference because PyArrow validated it.
- §3.3: the v4/v5 reassembly generator gets a slightly simpler input (aligned arrays) but must
  not share the v6 packed-buffer assumption in any shared component.
- Fixture note: in `test/data/v1/04_nested_lists_maps.parquet`, `scores` is `LIST<INT32>` but the
  *element* parses as `nullable=True, maxDef=3` (PyArrow 25 wrote element optionality on), so the
  "required element list" reassembly path is under-covered by that fixture — #176's write-side
  matrix must exercise `List<T>` with `T` non-nullable against its own files and PyArrow, not the
  fixture alone.

## 5. Reproduction

The spike programs (retired with this commit, reconstructed as unit tests during M1–M4) were:
A) reflection-serializer convention dump via `ReadRawColumnDataAsync`/`ReadRawAsync` on a
6-leaf nested model; B) hand-built level write via `WriteAllPartsAsync` + two-reader cross-check
(raw `ReadRawAsync` and PyArrow; the reflection serializer is a write-side reference only);
C) PyArrow fixture read + reassembly of `tags`; E) 4.25.0 `DataColumn` round-trip. PyArrow 25.0.0
(via `uv run --with pyarrow==25.0.0`) read all produced files with exact logical equality,
including `['a', None, 'b'], [], None`.
