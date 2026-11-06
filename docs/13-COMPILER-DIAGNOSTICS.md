# 13 - Compiler Diagnostics Reference

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
| **[`PARQ009`](#parq009-nested-type-not-supported)** | **Error** | Nested types not supported | Target type is nested within another type. |
| **[`PARQ010`](#parq010-generic-type-not-supported)** | **Error** | Generic types not supported | Target type is generic. |
| **[`PARQ011`](#parq011-type-unsupported-on-classic-v5-api)** | **Error** | Unsupported on classic API | Member type is supported by Parquet.Net 6 but not by the 4.x/5.x API. |

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
- **Cause**: `[ParquetDecimal(precision, scale)]` was configured with `precision < scale` or `precision > 38`.
- **Why**: Apache Parquet's physical representation of fixed-point decimals tops out at 38 digits (128-bit integer backing). Precision must always be greater than or equal to scale.
- **Remediation**:
  ```csharp
  // ❌ Incorrect: Precision < Scale
  [ParquetDecimal(2, 4)]
  public decimal Price { get; init; }

  // ✅ Correct: Precision >= Scale and <= 38
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
- **Why**: The generated `ReadParquetAsync` method uses an object initializer to construct records and classes. If a member has no setter or `init` accessor, the generated code fails to compile.
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

### PARQ009: Nested Type Not Supported
- **Severity**: Error
- **Cause**: `[ParquetSerializable]` is declared on a type defined inside another class or struct.
- **Why**: Generated extension classes are emitted at namespace scope. Nested target types cause ambiguity and hint-name collisions.
- **Remediation**: Move the type to namespace scope (top-level type).

---

### PARQ010: Generic Type Not Supported
- **Severity**: Error
- **Cause**: `[ParquetSerializable]` is declared on an open generic type (e.g., `public partial record Metric<T>`).
- **Why**: The emitted Parquet `ParquetSchema` is a `static readonly` field generated at compile time. It cannot dynamically vary based on runtime generic type arguments.
- **Remediation**: Create concrete, closed types for each type argument you need to serialize.

---

### PARQ011: Property Type Unsupported on Classic (v5) API
- **Severity**: Error *(Parquet.SourceGenerator.V5 only)*
- **Cause**: The member uses a type supported by Parquet.Net 6 (e.g., `ReadOnlyMemory<byte>`, `ReadOnlyMemory<char>`, `BigDecimal`), but the project references the legacy `Parquet.SourceGenerator.V5` package.
- **Why**: Parquet.Net 4.x/5.x lacks the primitive APIs required for these types.
- **Remediation**: Upgrade to the main `Parquet.SourceGenerator` package, or change the property to a type compatible with Parquet.Net 4.x/5.x (such as `byte[]` or `string`).
