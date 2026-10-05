using System;
using Xunit.Sdk;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Resolves the fixture an <c>ExternalInterop</c> test reads. These tests need a file produced by
/// an external engine, so a developer's plain <c>dotnet test</c> has nothing to read and the test
/// returns. Where the fixture is expected, returning is a silent pass over nothing (#566), so the
/// test fails instead: when <c>CI</c> is set (GitHub Actions sets <c>CI=true</c>), or when
/// <c>PARQUET_REQUIRE_EXTERNAL_INTEROP</c> is <c>1</c> or <c>true</c> for a local run that wants
/// the same strictness. Same stance as the coverage and IL gates, which fail when they examine
/// nothing (docs/internals/ci-gate-matrix.md).
/// </summary>
internal static class ExternalInteropInput
{
    public const string RequireVariable = "PARQUET_REQUIRE_EXTERNAL_INTEROP";

    /// <summary>The fixture path, or null when it is unset and nothing requires it.</summary>
    public static string? Resolve(string variable) =>
        Resolve(variable, Environment.GetEnvironmentVariable);

    public static string? Resolve(string variable, Func<string, string?> getEnvironmentVariable)
    {
        string? path = getEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (
            IsTruthy(getEnvironmentVariable("CI"))
            || IsTruthy(getEnvironmentVariable(RequireVariable))
        )
        {
            throw new XunitException(
                $"{variable} is unset but external interop input is required (CI or {RequireVariable} is set). "
                    + "The test would otherwise pass without reading a file. Supply the fixture, or filter Category=ExternalInterop out of this run."
            );
        }

        return null;
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.Ordinal);
}
