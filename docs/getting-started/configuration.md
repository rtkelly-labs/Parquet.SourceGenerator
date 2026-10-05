# Generator Feature Levels

The generator supports a small, named compatibility policy rather than arbitrary boolean switches:

| Level | Meaning |
|:---|:---|
| `Level1Flat` | Flat models and the compatibility-safe surface. |
| `Level2CompoundPreview` | The current default; enables supported compound preview shapes. |
| `Level3ModernCSharp` | Opts into the latest modern generator shapes. |

Set the project-wide level in MSBuild:

```xml
<PropertyGroup>
  <ParquetGeneratorFeatureLevel>Level2CompoundPreview</ParquetGeneratorFeatureLevel>
</PropertyGroup>
```

An assembly may set the same policy when MSBuild cannot be changed:

```csharp
[assembly: ParquetGeneratorOptions(FeatureLevel = ParquetGeneratorFeatureLevel.Level3ModernCSharp)]
```

MSBuild takes precedence over the assembly attribute. If neither is present, the default is
`Level2CompoundPreview`, preserving the existing 0.0.x emission behavior. Every generated artifact
records the selected level and generator version in its header so output can be audited without
reflection.
