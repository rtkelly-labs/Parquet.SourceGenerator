# Compiler Diagnostics Reference

`Parquet.SourceGenerator` validates models at compile time to ensure type safety, schema correctness, and optimal code generation. If an invalid or unsupported pattern is detected, the Roslyn analyzer emits a `PARQxxx` diagnostic.

This document details all diagnostic codes, their severity, rationale, and remediation steps.

---

## 📋 Diagnostics Catalog

| Diagnostic ID | Severity | Title | Short Description |
|:--- |:---:|:--- |:--- |
| **[`PARQ001`](#parq001-type-must-be-partial)** | **Error** | Type must be partial | Target type decorated with `[ParquetSerializable]` must be declared as `partial`. |
| **[`PARQ002`](#parq002-duplicate-column-name)** | **Error** | Duplicate column name | Duplicate Parquet column name detected on the same model. |
| **[`PARQ003`](#parq003-no-public-serializable-properties)** | **Warning** | No serializable properties found | Target type has no valid public serializable properties or fields. |
| **[`PARQ004`](#parq004-non-public-property-ignored)** | **Warning** | Non-public property ignored | Non-public property decorated with `[ParquetColumn]` is ignored. |
| **[`PARQ005`](#parq005-invalid-decimal-precision-or-scale)** | **Error** | Invalid decimal precision/scale | Invalid `[ParquetDecimal]` precision or scale parameters. |
| **[`PARQ006`](#parq006-unsupported-property-type)** | **Error** | Unsupported property type | Member type has no Parquet column representation. |
| **[`PARQ007`](#parq007-member-not-assignable)** | **Error** | Member not assignable | Member cannot be assigned by the generated deserializer. |
| **[`PARQ008`](#parq008-no-parameterless-constructor)** | **Error** | No parameterless constructor | Type has no accessible parameterless constructor (e.g. positional records). |
| **[`PARQ009`](#parq009-nested-type-is-not-accessible-to-generated-code)** | **Error** | Nested type not accessible | A nested target type is `private`, so generated code cannot name it. Nested targets are supported otherwise. |
| **[`PARQ010`](#parq010-generic-type-not-supported)** | **Error** | Generic types not supported | Target type is generic. |
| **[`PARQ011`](#parq011-type-unsupported-on-classic-v5-api)** | **Error** | Unsupported on classic API | Member type is supported by Parquet.Net 6 but not by the 4.x/5.x API. |
| **[`PARQ012`](#parq012-cyclic-compound-type)** | **Error** | Cyclic compound type | A struct or list member reaches its own declaring type again, so there is no finite column layout. |
| **[`PARQ013`](#parq013-compound-nesting-too-deep)** | **Error** | Compound nesting too deep | Compound members nest more than 6 levels deep. |
| **[`PARQ014`](#parq014-member-cannot-be-a-sort-key)** | **Error** | Member cannot be a sort key | `[ParquetSortKey]` is on a member that cannot drive row-group pruning. |
| **[`PARQ016`](#parq016-generated-type-names-collide)** | **Error** | Generated type names collide | Two targets whose containing-type paths flatten to the same name, such as `A.BC` and `AB.C`. |
| **[`PARQ020`](#parq020-abstract-type-not-supported)** | **Error** | Abstract type not supported | Target type is abstract. |
| **[`PARQ021`](#parq021-ref-struct-not-supported)** | **Error** | Ref struct not supported | Target type is a `ref struct`. |
| **[`PARQ022`](#parq022-file-local-type-not-supported)** | **Error** | File-local type not supported | Target type is declared with the `file` modifier, or nested in one. |

---

## 🔍 Detailed Diagnostic Explanations

### PARQ001: Type Must Be Partial
- **Severity**: Error
- **Cause**: The type decorated with `[ParquetSerializable]` is not declared with the `partial` keyword.
- **Why**: The source generator emits extension methods and schema companion definitions tied to your model. For future emitter enhancements and static members, declaring the type as `partial` allows clean compiler integration without reflection.
- **Remediation**:
  ```csharp
  // ❌ Incorrect
  [ParquetSerializable]
  public record UserEvent { ... }

  // ✅ Correct
  [ParquetSerializable]
  public partial record UserEvent { ... }
  ```

---

### PARQ002: Duplicate Column Name
- **Severity**: Error
- **Cause**: Multiple properties map to the same Parquet column name (either through explicit `[ParquetColumn("name")]` or default property naming).
- **Why**: Parquet schema field names within a flat row group must be distinct. Emitting duplicate fields creates invalid Parquet metadata that fails when creating the `ParquetWriter`.
- **Remediation**:
  ```csharp
  // ❌ Incorrect
  [ParquetColumn("id")] public int Id { get; init; }
  [ParquetColumn("id")] public string AltId { get; init; }

  // ✅ Correct
  [ParquetColumn("id")] public int Id { get; init; }
  [ParquetColumn("alt_id")] public string AltId { get; init; }
  ```

---

### PARQ003: No Public Serializable Properties Found
- **Severity**: Warning
- **Cause**: The type has no accessible public properties or fields that can be serialized.
- **Why**: A Parquet schema cannot have zero columns. Serializing this type produces an empty schema and causes writer failures.
- **Remediation**: Ensure the model exposes at least one public get/init/set property, or remove `[ParquetSerializable]` if the type is not meant to be serialized.

---

### PARQ004: Non-Public Property Ignored
- **Severity**: Warning
- **Cause**: A `private`, `protected`, or `internal` member is annotated with `[ParquetColumn]`.
- **Why**: The generated serializer extension class resides in the model's namespace and cannot access non-public members of the model.
- **Remediation**: Make the property `public`, or remove the `[ParquetColumn]` attribute.

---

### PARQ005: Invalid Decimal Precision or Scale
- **Severity**: Error
- **Cause**: `[ParquetDecimal(precision, scale)]` was configured with any of: `precision < scale`, `precision > 38`, `precision <= 0`, or `scale < 0`.
- **Why**: Apache Parquet's physical representation of fixed-point decimals tops out at 38 digits (128-bit integer backing). Precision must be positive and at least the scale, and the scale cannot be negative.
- **Remediation**:
  ```csharp
  // ❌ Incorrect: Precision < Scale
  [ParquetDecimal(2, 4)]
  public decimal Price { get; init; }

  // ❌ Also incorrect: non-positive precision or negative scale
  [ParquetDecimal(0, 0)]
  [ParquetDecimal(10, -1)]

  // ✅ Correct: 0 < Precision <= 38, 0 <= Scale <= Precision
  [ParquetDecimal(15, 2)]
  public decimal Price { get; init; }
  ```

---

### PARQ006: Unsupported Property Type
- **Severity**: Error
- **Cause**: A property's type cannot be represented as a primitive Parquet column. Examples: `char`, `DateTimeOffset`, nested collections (`List<T>`, `Dictionary<TKey, TValue>`), or custom complex objects.
- **Why**: The allowed set strictly mirrors Parquet.Net's `SchemaEncoder.SupportedTypes`. Rejecting unsupported types at compile time prevents runtime `ArgumentException` crashes deep inside Parquet.Net.
- **Remediation**:
  - For `char`: store as `string` or `int`.
  - For `DateTimeOffset`: store as `DateTime` (UTC) plus a separate offset column.
  - For nested collections / complex types: serialize to JSON string (`string`) or flatten into separate columns.
  - Or exclude the property: `[ParquetIgnore]`.

---

### PARQ007: Parquet Member Is Not Assignable
- **Severity**: Error
- **Cause**: A member is get-only (no setter or init) or marked `readonly`.
- **Why**: The generated read methods use an object initializer to construct records and classes. If a member has no setter or `init` accessor, the generated code fails to compile.
- **Remediation**:
  ```csharp
  // ❌ Incorrect: get-only
  public string Name { get; }

  // ✅ Correct: add init or set accessor
  public string Name { get; init; } = string.Empty;
  ```

---

### PARQ008: Type Has No Accessible Parameterless Constructor
- **Severity**: Error
- **Cause**: The type does not have a parameterless constructor (e.g. positional records like `public record Person(int Id, string Name);`).
- **Why**: The emitted reader deserializes columns by invoking `new T() { Prop1 = ..., Prop2 = ... }`. Without a parameterless constructor, instantiation fails.
- **Remediation**:
  ```csharp
  // ❌ Incorrect: Positional record
  [ParquetSerializable]
  public partial record Person(int Id, string Name);

  // ✅ Correct: Nominal record or class with parameterless constructor
  [ParquetSerializable]
  public partial record Person
  {
      public int Id { get; init; }
      public string Name { get; init; } = string.Empty;
  }
  ```

---

### PARQ009: Nested Type Is Not Accessible to Generated Code
- **Severity**: Error
- **Cause**: `[ParquetSerializable]` is declared on a nested type whose accessibility generated code cannot reach, in practice a `private` nested declaration.
- **Why**: Nested targets are supported (#176): the generated extension class is emitted at namespace scope under the flattened containing-type path (`Outer.Inner` becomes `OuterInnerParquetExtensions`, `OuterInnerBatch` and so on). A `private` nested type cannot be named from there. Two targets whose paths flatten to the same name are a separate error, [PARQ016](#parq016-generated-type-names-collide).
- **Remediation**: Give the nested type `internal` or `public` accessibility. It does not need to be moved to namespace scope.

---

### PARQ010: Generic Type Not Supported
- **Severity**: Error
- **Cause**: `[ParquetSerializable]` is declared on an open generic type (e.g., `public partial record Metric<T>`).
- **Why**: The emitted Parquet `ParquetSchema` is a `static readonly` field generated at compile time. It cannot dynamically vary based on runtime generic type arguments.
- **Remediation**: Create concrete, closed types for each type argument you need to serialize.

---

### PARQ011: Property Type Unsupported on Classic (v5) API
- **Severity**: Error *(Parquet.SourceGenerator.Legacy only)*
- **Cause**: The member uses a type supported by Parquet.Net 6 (e.g., `ReadOnlyMemory<byte>`, `ReadOnlyMemory<char>`, `BigDecimal`), but the project references the legacy `Parquet.SourceGenerator.Legacy` package.
- **Why**: Parquet.Net 4.x/5.x lacks the primitive APIs required for these types.
- **Remediation**: Upgrade to the main `Parquet.SourceGenerator` package, or change the property to a type compatible with Parquet.Net 4.x/5.x (such as `byte[]` or `string`).

---

### PARQ012: Cyclic Compound Type
- **Severity**: Error
- **Cause**: A struct member or a list element reaches its own declaring type again, directly or through other compound types (`A` holds a `B`, `B` holds an `A`, or a type holds a list of itself).
- **Why**: A Parquet schema is a tree. A type that reaches itself has no finite column layout, and without this rule the parser recursed until the stack gave out and the build failed with CS8785 and no pointer to the declaration responsible.
- **Remediation**: Mark the member that closes the cycle with `[ParquetIgnore]`, or break the cycle by storing a key instead of the reference.

  ```csharp
  // ❌ Node holds a Node
  [ParquetSerializable]
  public partial record Node { public int Id { get; init; } public Node? Next { get; init; } }

  // ✅ Store the key, or [ParquetIgnore] the reference
  [ParquetSerializable]
  public partial record Node { public int Id { get; init; } public int? NextId { get; init; } }
  ```

---

### PARQ013: Compound Nesting Too Deep
- **Severity**: Error
- **Cause**: Compound members (structs, lists) nest more than 6 levels deep (`TargetParser.MaxCompoundDepth`). The message names the member, the depth reached and the maximum.
- **Why**: Record shredding is unrolled per leaf at generation time, so emitted-code size grows with the leaf count and the depth of the row path. Six levels is generous for real domain models and keeps pathological nesting from emitting megabyte-scale source files.
- **Remediation**: Flatten the model, or mark the deep member `[ParquetIgnore]`.

---

### PARQ014: Member Cannot Be a Sort Key
- **Severity**: Error
- **Cause**: `[ParquetSortKey]` is on a member that cannot drive row-group pruning. The message ends with the reason, which is one of: the member is a compound member (only a flat root column has `[Min, Max]` statistics of its own); it is a `string` column (Parquet orders byte arrays bytewise while `string.CompareTo` is culture-sensitive, so the two orders can disagree); it is nullable (statistics say nothing about where the nulls sit); or its type has no Parquet statistics order guaranteed to match `Comparer<T>.Default` (supported key types are the integral types, `float`, `double` and `System.DateTime`).
- **Why**: Sorted pruning binary searches the footer `[Min, Max]` statistics, which is only sound for a flat, non-nullable root column of a totally ordered type. Emitting nothing for an ineligible marker would leave the author expecting a sorted-key lookup that never appeared, so the marker is reported with the reason instead.
- **Remediation**: Remove `[ParquetSortKey]`, or move it to an eligible flat, non-nullable column of an integral, floating-point or `DateTime` type.

---

### PARQ016: Generated Type Names Collide
- **Severity**: Error
- **Cause**: Two `[ParquetSerializable]` types in the same namespace have containing-type paths that
  become the same identifier once the dots are removed: `A.BC` and `AB.C`, or a nested `A.BC` and
  a top-level `ABC`.
- **Why**: Generated types (`…ParquetExtensions`, `…RowGroupMetadata`, `…Batch`, the read
  sources) are emitted at namespace scope under that flattened name, so both targets would declare
  the same types. Before this rule the build failed with a cascade of `CS0101` errors inside
  generated files that named neither declaration.
- **Remediation**: Rename one of the types (or one of their containing types), or move one to
  another namespace.

---

### PARQ020: Abstract Type Not Supported
- **Severity**: Error
- **Cause**: `[ParquetSerializable]` is declared on an abstract class.
- **Why**: The read path builds each row with `new T { ... }`, which an abstract type cannot satisfy; before this rule the build failed with `CS0144` inside the generated file. A static class is reported by PARQ008 instead.
- **Remediation**: Put the attribute on a concrete derived type. Members inherited from an abstract base are included in the derived type's columns.

---

### PARQ021: Ref Struct Not Supported
- **Severity**: Error
- **Cause**: `[ParquetSerializable]` is declared on a `ref struct`.
- **Why**: Generated code stores rows in `List<T>`, arrays and `Task<List<T>>`, none of which can hold a ref struct; before this rule the build failed with `CS0306` and `CS8345` across the generated file.
- **Remediation**: Declare an ordinary `struct` or `class`.

---

### PARQ022: File-Local Type Not Supported
- **Severity**: Error
- **Cause**: `[ParquetSerializable]` is declared on a type with the `file` modifier, or on a type nested inside one.
- **Why**: Generated code is emitted into a different file and names the target in public signatures, which a file-local type cannot be (`CS9051`). Two file-local targets of the same name in one namespace also used to share a generated hint name.
- **Remediation**: Remove the `file` modifier.

---

## Deferred diagnostics

- **Disabled-feature omission diagnostic (#226).** Reporting the cause when a member is missing
  because a feature is disabled is deferred until the feature-profile and configuration state it
  would read is settled. See [document 37](37-DISABLED-FEATURE-DIAGNOSTIC-SCOPE-226.md).

---

## 🗄️ Retired Diagnostics

- **PARQ015: Invalid Generator Feature Level**: Retired in 0.1 after collapsing `ParquetGeneratorFeatureLevel` and `[ParquetGeneratorOptions]` into the boolean MSBuild switch `<ParquetGeneratorFlatOnly>true</ParquetGeneratorFlatOnly>`. Flat-only schema enforcement is now configured via MSBuild.
