using System;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Parquet.SourceGenerator.Diagnostics;

namespace Parquet.SourceGenerator.Models;

/// <summary>
/// Value-equatable project configuration carried through the incremental pipeline.
/// </summary>
internal sealed record GeneratorConfiguration(
    bool FlatOnly,
    string GeneratorVersion,
    DiagnosticInfo? ConfigurationDiagnostic = null
)
{
    public static GeneratorConfiguration Default { get; } =
        new(FlatOnly: false, GetGeneratorVersion());

    public static GeneratorConfiguration From(AnalyzerConfigOptionsProvider optionsProvider)
    {
        bool flatOnly = false;

        if (
            optionsProvider.GlobalOptions.TryGetValue(
                "build_property.ParquetGeneratorFlatOnly",
                out string? flatOnlyProp
            ) && !string.IsNullOrWhiteSpace(flatOnlyProp)
        )
        {
            if (bool.TryParse(flatOnlyProp, out bool parsedBool))
            {
                flatOnly = parsedBool;
            }
        }
        else if (
            optionsProvider.GlobalOptions.TryGetValue(
                "build_property.ParquetGeneratorFeatureLevel",
                out string? featureLevel
            ) && !string.IsNullOrWhiteSpace(featureLevel)
        )
        {
            if (string.Equals(featureLevel, "Level1Flat", StringComparison.OrdinalIgnoreCase))
            {
                flatOnly = true;
            }
        }

        return new GeneratorConfiguration(flatOnly, GetGeneratorVersion());
    }

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
