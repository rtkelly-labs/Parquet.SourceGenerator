using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Parquet.SourceGenerator.Diagnostics;

namespace Parquet.SourceGenerator.Models;

internal enum GeneratorFeatureLevel
{
    Level1Flat = 1,
    Level2CompoundPreview = 2,
    Level3ModernCSharp = 3,
}

/// <summary>
/// Value-equatable project configuration carried through the incremental pipeline.
/// </summary>
internal sealed record GeneratorConfiguration(
    GeneratorFeatureLevel FeatureLevel,
    string GeneratorVersion,
    DiagnosticInfo? ConfigurationDiagnostic = null
)
{
    private const string OptionsAttributeName =
        "Parquet.SourceGenerator.ParquetGeneratorOptionsAttribute";

    public static GeneratorConfiguration Default { get; } =
        new(GeneratorFeatureLevel.Level2CompoundPreview, GetGeneratorVersion());

    public static GeneratorConfiguration From(
        Compilation compilation,
        AnalyzerConfigOptionsProvider optionsProvider
    )
    {
        if (
            optionsProvider.GlobalOptions.TryGetValue(
                "build_property.ParquetGeneratorFeatureLevel",
                out string? featureLevel
            ) && !string.IsNullOrWhiteSpace(featureLevel)
        )
        {
            if (
                Enum.TryParse(featureLevel, ignoreCase: true, out GeneratorFeatureLevel parsed)
                && Enum.IsDefined(typeof(GeneratorFeatureLevel), parsed)
            )
            {
                return new GeneratorConfiguration(parsed, GetGeneratorVersion());
            }

            return Invalid(featureLevel);
        }

        AttributeData? assemblyOptions = compilation
            .Assembly.GetAttributes()
            .FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == OptionsAttributeName
            );
        if (assemblyOptions is not null)
        {
            foreach (
                KeyValuePair<string, TypedConstant> namedArgument in assemblyOptions.NamedArguments
            )
            {
                if (namedArgument.Key == "FeatureLevel")
                {
                    if (
                        namedArgument.Value.Value is int value
                        && Enum.IsDefined(typeof(GeneratorFeatureLevel), value)
                    )
                    {
                        return new GeneratorConfiguration(
                            (GeneratorFeatureLevel)value,
                            GetGeneratorVersion()
                        );
                    }

                    return Invalid(namedArgument.Value.Value?.ToString() ?? "<missing>");
                }
            }
        }

        return Default;
    }

    private static GeneratorConfiguration Invalid(string featureLevel) =>
        new(
            GeneratorFeatureLevel.Level2CompoundPreview,
            GetGeneratorVersion(),
            new DiagnosticInfo(
                DiagnosticDescriptors.InvalidFeatureLevel,
                Location.None,
                new[] { featureLevel }
            )
        );

    private static string GetGeneratorVersion()
    {
        Assembly assembly = typeof(GeneratorConfiguration).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            string version = informational!;
            int metadataStart = version.IndexOf('+');
            return metadataStart >= 0 ? version.Substring(0, metadataStart) : version;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
