#pragma warning disable CA1510, CA1846, CA1861, CA1865

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>
/// A shape budget for an emitted model category.
/// </summary>
public sealed class ApiShapeBudget
{
    /// <summary>The category name (e.g. flat, sorted, compound, legacy).</summary>
    public string Category { get; }

    /// <summary>Maximum allowed public members in emitted code.</summary>
    public int MaxMembers { get; }

    /// <summary>Maximum allowed parameter slots across emitted members.</summary>
    public int MaxParameters { get; }

    /// <summary>Initializes a new <see cref="ApiShapeBudget"/>.</summary>
    public ApiShapeBudget(string category, int maxMembers, int maxParameters)
    {
        Category = category;
        MaxMembers = maxMembers;
        MaxParameters = maxParameters;
    }
}

/// <summary>
/// A measured member and parameter count for a generated model.
/// </summary>
public sealed class ApiShapeMeasurement
{
    /// <summary>The name or stem of the generated model.</summary>
    public string ModelName { get; }

    /// <summary>The shape category of the model.</summary>
    public string Category { get; }

    /// <summary>Measured member count.</summary>
    public int Members { get; }

    /// <summary>Measured parameter count.</summary>
    public int Parameters { get; }

    /// <summary>Initializes a new <see cref="ApiShapeMeasurement"/>.</summary>
    public ApiShapeMeasurement(string modelName, string category, int members, int parameters)
    {
        ModelName = modelName;
        Category = category;
        Members = members;
        Parameters = parameters;
    }
}

/// <summary>
/// Gates emitted code generation against checked-in member and parameter budgets
/// (<c>src/api/emitted-api-budgets.txt</c>).
/// </summary>
public static class GeneratedApiBudgetGate
{
    /// <summary>
    /// Parses a budgets catalogue file content.
    /// </summary>
    public static IReadOnlyDictionary<string, ApiShapeBudget> ParseBudgets(string content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var budgets = new Dictionary<string, ApiShapeBudget>(StringComparer.OrdinalIgnoreCase);
        using (var reader = new StringReader(content))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = trimmed.Split(
                    new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries
                );
                if (parts.Length < 3)
                {
                    throw new FormatException(
                        "Invalid budget entry line in emitted-api-budgets.txt: '" + line + "'"
                    );
                }

                string category = parts[0];
                int maxMembers = -1;
                int maxParams = -1;

                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].StartsWith("MEMBERS=", StringComparison.Ordinal))
                    {
                        if (
                            int.TryParse(
                                parts[i].Substring("MEMBERS=".Length),
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int val
                            )
                        )
                        {
                            maxMembers = val;
                        }
                    }
                    else if (parts[i].StartsWith("PARAMETERS=", StringComparison.Ordinal))
                    {
                        if (
                            int.TryParse(
                                parts[i].Substring("PARAMETERS=".Length),
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int val
                            )
                        )
                        {
                            maxParams = val;
                        }
                    }
                }

                if (maxMembers < 0 || maxParams < 0)
                {
                    throw new FormatException(
                        "Missing MEMBERS or PARAMETERS in budget entry: '" + line + "'"
                    );
                }

                budgets[category] = new ApiShapeBudget(category, maxMembers, maxParams);
            }
        }

        if (budgets.Count == 0)
        {
            throw new InvalidOperationException(
                "Emitted API budgets catalogue contains 0 budget definitions."
            );
        }

        return budgets;
    }

    /// <summary>
    /// Maps a model file stem or type name to its shape category.
    /// </summary>
    public static string ClassifyModel(string modelStem)
    {
        if (string.IsNullOrEmpty(modelStem))
        {
            throw new ArgumentException("Model stem cannot be null or empty", nameof(modelStem));
        }

        if (modelStem.StartsWith("LegacyRecord", StringComparison.Ordinal))
        {
            return "legacy";
        }

        if (
            modelStem.StartsWith("NestedOrder", StringComparison.Ordinal)
            || modelStem.StartsWith("ListOrder", StringComparison.Ordinal)
            || modelStem.StartsWith("PocoOrder", StringComparison.Ordinal)
        )
        {
            return "compound";
        }

        if (modelStem.StartsWith("SortedShipment", StringComparison.Ordinal))
        {
            return "sorted";
        }

        return "flat";
    }

    /// <summary>
    /// Extracts member and parameter counts from a shape summary string.
    /// </summary>
    public static (int Members, int Parameters) ParseShapeSummary(string shapeSummary)
    {
        if (shapeSummary is null)
        {
            throw new ArgumentNullException(nameof(shapeSummary));
        }

        int members = -1;
        int parameters = -1;

        string[] tokens = shapeSummary.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries
        );

        foreach (string token in tokens)
        {
            if (token.StartsWith("MEMBERS=", StringComparison.Ordinal))
            {
                if (
                    int.TryParse(
                        token.Substring("MEMBERS=".Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int val
                    )
                )
                {
                    members = val;
                }
            }
            else if (token.StartsWith("PARAMETERS=", StringComparison.Ordinal))
            {
                if (
                    int.TryParse(
                        token.Substring("PARAMETERS=".Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int val
                    )
                )
                {
                    parameters = val;
                }
            }
        }

        if (members < 0 || parameters < 0)
        {
            throw new FormatException(
                "Could not parse MEMBERS and PARAMETERS from shape summary: '" + shapeSummary + "'"
            );
        }

        return (members, parameters);
    }

    /// <summary>
    /// Verifies that all measurements satisfy their category budgets.
    /// Fails closed if 0 measurements are provided (positive control requirement).
    /// </summary>
    public static IReadOnlyList<string> Verify(
        IReadOnlyDictionary<string, ApiShapeBudget> budgets,
        IReadOnlyList<ApiShapeMeasurement> measurements
    )
    {
        if (budgets is null)
        {
            throw new ArgumentNullException(nameof(budgets));
        }

        if (measurements is null)
        {
            throw new ArgumentNullException(nameof(measurements));
        }

        if (measurements.Count == 0)
        {
            throw new InvalidOperationException(
                "Generated API budget gate examined 0 models; positive control failed."
            );
        }

        var errors = new List<string>();

        foreach (ApiShapeMeasurement measurement in measurements)
        {
            ApiShapeBudget? budget = null;
            if (budgets.TryGetValue(measurement.Category, out ApiShapeBudget? b1))
            {
                budget = b1;
            }
            else if (budgets.TryGetValue(measurement.ModelName, out ApiShapeBudget? b2))
            {
                budget = b2;
            }

            if (budget is null)
            {
                errors.Add(
                    "No budget defined for category '"
                        + measurement.Category
                        + "' (model: '"
                        + measurement.ModelName
                        + "')."
                );
                continue;
            }

            if (measurement.Members > budget.MaxMembers)
            {
                errors.Add(
                    "Model '"
                        + measurement.ModelName
                        + "' ("
                        + measurement.Category
                        + ") exceeded MEMBERS budget: "
                        + measurement.Members.ToString(CultureInfo.InvariantCulture)
                        + " > "
                        + budget.MaxMembers.ToString(CultureInfo.InvariantCulture)
                        + "."
                );
            }

            if (measurement.Parameters > budget.MaxParameters)
            {
                errors.Add(
                    "Model '"
                        + measurement.ModelName
                        + "' ("
                        + measurement.Category
                        + ") exceeded PARAMETERS budget: "
                        + measurement.Parameters.ToString(CultureInfo.InvariantCulture)
                        + " > "
                        + budget.MaxParameters.ToString(CultureInfo.InvariantCulture)
                        + "."
                );
            }
        }

        return errors;
    }
}
