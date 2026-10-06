using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parquet.SourceGenerator.ApiGates;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Unit tests and positive controls for the emitted API shape budget gate (#588, docs/51).
/// </summary>
public sealed class GeneratedApiBudgetGateTests
{
    private static string FindRepoRoot()
    {
        for (
            DirectoryInfo? dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            dir is not null;
            dir = dir.Parent
        )
        {
            if (IOFile.Exists(Path.Combine(dir.FullName, "Parquet.SourceGenerator.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Could not find repository root.");
    }

    [Fact]
    public void ParsesCheckedInBudgetsFile()
    {
        string budgetPath = Path.Combine(FindRepoRoot(), "src", "api", "emitted-api-budgets.txt");
        IOFile.Exists(budgetPath).ShouldBeTrue();

        string content = IOFile.ReadAllText(budgetPath);
        IReadOnlyDictionary<string, ApiShapeBudget> budgets = GeneratedApiBudgetGate.ParseBudgets(
            content
        );

        budgets.ContainsKey("flat").ShouldBeTrue();
        budgets.ContainsKey("sorted").ShouldBeTrue();
        budgets.ContainsKey("compound").ShouldBeTrue();
        budgets.ContainsKey("legacy").ShouldBeTrue();

        budgets["flat"].MaxMembers.ShouldBe(40);
        budgets["flat"].MaxParameters.ShouldBe(36);
        budgets["sorted"].MaxMembers.ShouldBe(40);
        budgets["sorted"].MaxParameters.ShouldBe(36);
        budgets["compound"].MaxMembers.ShouldBe(20);
        budgets["compound"].MaxParameters.ShouldBe(20);
        budgets["legacy"].MaxMembers.ShouldBe(10);
        budgets["legacy"].MaxParameters.ShouldBe(15);
    }

    [Theory]
    [InlineData("OrderEventParquetExtensions", "flat")]
    [InlineData("ScalarMetricParquetExtensions", "flat")]
    [InlineData("ArrowOrderParquetExtensions", "flat")]
    [InlineData("SortedShipmentParquetExtensions", "sorted")]
    [InlineData("NestedOrderParquetExtensions", "compound")]
    [InlineData("ListOrderParquetExtensions", "compound")]
    [InlineData("PocoOrderParquetExtensions", "compound")]
    [InlineData("LegacyRecordParquetLegacyExtensions", "legacy")]
    public void ClassifiesKnownGoldenModels(string modelStem, string expectedCategory)
    {
        GeneratedApiBudgetGate.ClassifyModel(modelStem).ShouldBe(expectedCategory);
    }

    [Fact]
    public void AllGoldenModelsSatisfyCheckedInBudgets()
    {
        string budgetPath = Path.Combine(FindRepoRoot(), "src", "api", "emitted-api-budgets.txt");
        IReadOnlyDictionary<string, ApiShapeBudget> budgets = GeneratedApiBudgetGate.ParseBudgets(
            IOFile.ReadAllText(budgetPath)
        );

        var measurements = new List<ApiShapeMeasurement>();
        foreach (GoldenEmission emission in GoldenCorpus.All)
        {
            string stem = emission.FileName.Substring(0, emission.FileName.Length - ".g.cs".Length);
            string shapeSummary = GeneratedApiBaseline.CreateShapeSummary(emission.Source);
            var (members, parameters) = GeneratedApiBudgetGate.ParseShapeSummary(shapeSummary);
            string category = GeneratedApiBudgetGate.ClassifyModel(stem);
            measurements.Add(new ApiShapeMeasurement(stem, category, members, parameters));
        }

        measurements.Count.ShouldBe(8);
        IReadOnlyList<string> errors = GeneratedApiBudgetGate.Verify(budgets, measurements);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void PositiveControlFailsWhenExaminedCountIsZero()
    {
        var budgets = new Dictionary<string, ApiShapeBudget> { ["flat"] = new("flat", 40, 36) };

        var ex = Should.Throw<InvalidOperationException>(() =>
            GeneratedApiBudgetGate.Verify(budgets, Array.Empty<ApiShapeMeasurement>())
        );
        ex.Message.ShouldContain("examined 0 models");
    }

    [Fact]
    public void PositiveControlFailsWhenMembersExceedBudget()
    {
        var budgets = new Dictionary<string, ApiShapeBudget> { ["flat"] = new("flat", 40, 36) };

        var measurements = new List<ApiShapeMeasurement>
        {
            new("LargeModel", "flat", members: 41, parameters: 30),
        };

        IReadOnlyList<string> errors = GeneratedApiBudgetGate.Verify(budgets, measurements);
        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("exceeded MEMBERS budget: 41 > 40");
    }

    [Fact]
    public void PositiveControlFailsWhenParametersExceedBudget()
    {
        var budgets = new Dictionary<string, ApiShapeBudget> { ["flat"] = new("flat", 40, 36) };

        var measurements = new List<ApiShapeMeasurement>
        {
            new("ParamHeavyModel", "flat", members: 30, parameters: 37),
        };

        IReadOnlyList<string> errors = GeneratedApiBudgetGate.Verify(budgets, measurements);
        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("exceeded PARAMETERS budget: 37 > 36");
    }

    [Fact]
    public void PositiveControlFailsWhenCategoryMissingBudget()
    {
        var budgets = new Dictionary<string, ApiShapeBudget> { ["flat"] = new("flat", 40, 36) };

        var measurements = new List<ApiShapeMeasurement>
        {
            new("UnknownModel", "unknown", members: 10, parameters: 10),
        };

        IReadOnlyList<string> errors = GeneratedApiBudgetGate.Verify(budgets, measurements);
        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("No budget defined for category 'unknown'");
    }
}
