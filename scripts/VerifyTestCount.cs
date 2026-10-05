using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

// A discovery regression must not turn the required test job green by running fewer tests.
// Lower this reviewed floor only with an explanation in the PR and the 0.1 gate matrix.
// The main project ran 1,752 tests after the behavioral cleanup in #670/#671.
const int MinimumExecuted = 1700;
const string TrxPath = "test/Parquet.SourceGenerator.Tests/TestResults/ci.trx";

if (
    !CountsMeetFloor(
        XDocument.Parse(
            "<TestRun><ResultSummary><Counters total=\"1700\" executed=\"1700\" /></ResultSummary></TestRun>"
        ),
        MinimumExecuted,
        out _
    )
    || CountsMeetFloor(
        XDocument.Parse(
            "<TestRun><ResultSummary><Counters total=\"1699\" executed=\"1699\" /></ResultSummary></TestRun>"
        ),
        MinimumExecuted,
        out _
    )
    || CountsMeetFloor(XDocument.Parse("<TestRun />"), MinimumExecuted, out _)
)
{
    Console.Error.WriteLine("Test-count gate positive controls failed.");
    return 1;
}

if (!File.Exists(TrxPath))
{
    Console.Error.WriteLine($"Missing TRX test report: {TrxPath}");
    return 1;
}

XDocument report;
try
{
    report = XDocument.Load(TrxPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not parse {TrxPath}: {ex.Message}");
    return 1;
}

if (!CountsMeetFloor(report, MinimumExecuted, out int executed))
{
    Console.Error.WriteLine(
        $"Test-count gate failed: {executed} executed tests; at least {MinimumExecuted} required. "
            + $"Check test discovery and the full-suite filter in CI ({TrxPath})."
    );
    return 1;
}

Console.WriteLine($"Test-count gate passed: {executed} executed tests (floor {MinimumExecuted}).");
return 0;

static bool CountsMeetFloor(XDocument report, int floor, out int executed)
{
    executed = 0;
    if (report.Root?.Name.LocalName != "TestRun")
        return false;

    XElement[] summaries = report
        .Descendants()
        .Where(element => element.Name.LocalName == "ResultSummary")
        .ToArray();
    if (summaries.Length != 1)
        return false;

    XElement[] counters = summaries[0]
        .Elements()
        .Where(element => element.Name.LocalName == "Counters")
        .ToArray();
    if (counters.Length != 1)
        return false;

    return int.TryParse(
            counters[0].Attribute("executed")?.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out executed
        )
        && int.TryParse(
            counters[0].Attribute("total")?.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int total
        )
        && total >= executed
        && executed >= floor;
}
