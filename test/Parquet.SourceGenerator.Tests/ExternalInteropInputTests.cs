using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;
using Xunit.Sdk;

namespace Parquet.SourceGenerator.Tests;

/// <summary>Positive and negative controls for the fail-closed ExternalInterop input (#566).</summary>
public sealed class ExternalInteropInputTests
{
    private const string Variable = "DUCKDB_INTEROP_INPUT";

    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in values)
        {
            map[key] = value;
        }

        return key => map.TryGetValue(key, out string? value) ? value : null;
    }

    [Fact]
    public void AnUnsetInputFailsWhenCiIsSet() =>
        Should
            .Throw<XunitException>(() =>
                ExternalInteropInput.Resolve(Variable, Env(("CI", "true")))
            )
            .Message.ShouldContain(Variable);

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    public void AnUnsetInputFailsWhenItIsExplicitlyRequired(string flag) =>
        Should.Throw<XunitException>(() =>
            ExternalInteropInput.Resolve(
                Variable,
                Env((ExternalInteropInput.RequireVariable, flag))
            )
        );

    [Fact]
    public void ABlankInputFailsWhenCiIsSet() =>
        Should.Throw<XunitException>(() =>
            ExternalInteropInput.Resolve(Variable, Env(("CI", "true"), (Variable, "  ")))
        );

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("")]
    public void AnUnsetInputIsToleratedForALocalRun(string? ci) =>
        ExternalInteropInput.Resolve(Variable, ci is null ? Env() : Env(("CI", ci))).ShouldBeNull();

    [Fact]
    public void ASuppliedInputIsReturnedWhetherOrNotCiIsSet()
    {
        ExternalInteropInput
            .Resolve(Variable, Env(("CI", "true"), (Variable, "/tmp/x.parquet")))
            .ShouldBe("/tmp/x.parquet");
        ExternalInteropInput
            .Resolve(Variable, Env((Variable, "/tmp/x.parquet")))
            .ShouldBe("/tmp/x.parquet");
    }
}
