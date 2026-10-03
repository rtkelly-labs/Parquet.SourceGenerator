using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

// Count completed test results, not source attributes or log lines. A skipped test
// cannot satisfy the floor. Exact report paths prevent stale or duplicate runs
// from being added together.
try
{
    if (args.Length == 1 && args[0] == "--self-test")
    {
        RunPositiveControls();
        return 0;
    }

    if (args.Length != 3)
        throw new InvalidDataException(
            "Usage: dotnet run scripts/TestCountGate.cs -- <floors.json> <core.trx> <external.trx>"
        );

    using JsonDocument floors = JsonDocument.Parse(File.ReadAllText(args[0]));
    foreach (
        var group in new[] { (Name: "core", Path: args[1]), (Name: "external", Path: args[2]) }
    )
    {
        int minimum = floors.RootElement.GetProperty(group.Name).GetInt32();
        int passed = CheckReport(XDocument.Load(group.Path), minimum);
        Console.WriteLine($"{group.Name}: {passed} passed test results; minimum {minimum}.");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"::error::Test count gate failed: {ex.Message}");
    return 1;
}

static int CheckReport(XDocument report, int minimum)
{
    if (minimum < 1)
        throw new InvalidDataException("The test floor must be positive.");

    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    XElement root = report.Root ?? throw new InvalidDataException("Missing TRX root.");
    if (root.Name != ns + "TestRun")
        throw new InvalidDataException("Expected a namespaced TRX TestRun.");

    XElement counters =
        root.Element(ns + "ResultSummary")?.Element(ns + "Counters")
        ?? throw new InvalidDataException("Missing TRX result counters.");
    int total = ReadCount(counters, "total");
    int executed = ReadCount(counters, "executed");
    int passed = ReadCount(counters, "passed");
    XElement[] results =
        root.Element(ns + "Results")?.Elements(ns + "UnitTestResult").ToArray()
        ?? throw new InvalidDataException("Missing TRX test results.");

    if (
        results.Length != total
        || executed != total
        || passed != total
        || results.Any(result => (string?)result.Attribute("outcome") != "Passed")
    )
        throw new InvalidDataException(
            $"Incomplete or unsuccessful run: total={total}, executed={executed}, passed={passed}, results={results.Length}. Skipped tests do not count."
        );

    string[] executionIds = results
        .Select(result => (string?)result.Attribute("executionId") ?? string.Empty)
        .ToArray();
    if (
        executionIds.Any(string.IsNullOrWhiteSpace)
        || executionIds.Distinct(StringComparer.Ordinal).Count() != total
    )
        throw new InvalidDataException("Missing or duplicate TRX execution IDs.");

    if (passed < minimum)
        throw new InvalidDataException($"Only {passed} passed tests; minimum is {minimum}.");
    return passed;
}

static int ReadCount(XElement counters, string name)
{
    if (
        !int.TryParse(
            (string?)counters.Attribute(name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value
        )
    )
        throw new InvalidDataException($"Invalid TRX counter: {name}.");
    return value;
}

static XDocument Fixture(int total, int executed, int passed, params string[] outcomes)
{
    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    return new XDocument(
        new XElement(
            ns + "TestRun",
            new XElement(
                ns + "ResultSummary",
                new XElement(
                    ns + "Counters",
                    new XAttribute("total", total),
                    new XAttribute("executed", executed),
                    new XAttribute("passed", passed)
                )
            ),
            new XElement(
                ns + "Results",
                outcomes.Select(
                    (outcome, index) =>
                        new XElement(
                            ns + "UnitTestResult",
                            new XAttribute(
                                "executionId",
                                index.ToString(CultureInfo.InvariantCulture)
                            ),
                            new XAttribute("outcome", outcome)
                        )
                )
            )
        )
    );
}

static void RunPositiveControls()
{
    if (CheckReport(Fixture(2, 2, 2, "Passed", "Passed"), 2) != 2)
        throw new InvalidOperationException("A complete run at the floor must pass.");
    if (CheckReport(Fixture(3, 3, 3, "Passed", "Passed", "Passed"), 2) != 3)
        throw new InvalidOperationException("A complete run above the floor must pass.");

    XDocument duplicate = Fixture(2, 2, 2, "Passed", "Passed");
    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    duplicate.Descendants(ns + "UnitTestResult").Last().SetAttributeValue("executionId", "0");
    XDocument invalidCounter = Fixture(1, 1, 1, "Passed");
    invalidCounter.Descendants(ns + "Counters").Single().SetAttributeValue("total", "invalid");

    var rejected = new[]
    {
        (Report: Fixture(1, 1, 1, "Passed"), Minimum: 2),
        (Report: Fixture(0, 0, 0), Minimum: 1),
        (Report: Fixture(2, 1, 1, "Passed", "NotExecuted"), Minimum: 1),
        (Report: Fixture(2, 2, 1, "Passed", "Failed"), Minimum: 1),
        (Report: Fixture(2, 2, 2, "Passed", "NotExecuted"), Minimum: 1),
        (Report: Fixture(2, 2, 2, "Passed"), Minimum: 1),
        (Report: Fixture(1, 1, 1, "Passed"), Minimum: 0),
        (Report: new XDocument(new XElement("TestRun")), Minimum: 1),
        (Report: new XDocument(new XElement(ns + "TestRun")), Minimum: 1),
        (Report: duplicate, Minimum: 1),
        (Report: invalidCounter, Minimum: 1),
    };
    foreach (var control in rejected)
    {
        bool failed = false;
        try
        {
            CheckReport(control.Report, control.Minimum);
        }
        catch (InvalidDataException)
        {
            failed = true;
        }
        if (!failed)
            throw new InvalidOperationException("An invalid report passed a positive control.");
    }
    Console.WriteLine($"Test count gate controls: 2 accepted, {rejected.Length} rejected.");
}
