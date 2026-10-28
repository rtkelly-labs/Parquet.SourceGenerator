using System.Globalization;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// The positive control for ci.yml's <c>test-net11</c> job. The projects target net10.0 and that
/// job rolls them forward onto the .NET 11 runtime; it sets <c>EXPECTED_DOTNET_MAJOR</c>, so a
/// roll-forward that silently stayed on .NET 10 fails here instead of passing the whole suite on
/// the wrong runtime. Without the variable (every other run) there is nothing to check.
/// </summary>
public sealed class RuntimeVersionTests
{
    [Fact]
    public void TestsRunOnTheRuntimeMajorTheJobAskedFor()
    {
        string? expected = Environment.GetEnvironmentVariable("EXPECTED_DOTNET_MAJOR");
        if (string.IsNullOrEmpty(expected))
        {
            return;
        }

        Environment.Version.Major.ShouldBe(
            int.Parse(expected, CultureInfo.InvariantCulture),
            $"tests ran on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"
        );
    }
}
