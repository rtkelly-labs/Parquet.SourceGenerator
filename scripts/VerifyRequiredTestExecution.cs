using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

// Require evidence that the 0.1 contract suites actually executed. Counts may change when
// redundant cases are removed; named behavioral suites and whole-project execution may not.
const string CoreReport = "ci-core.trx";
const string ExternalReport = "ci-external.trx";

const string MainResults = "test/Parquet.SourceGenerator.Tests/TestResults/";
const string AbiResults = "test/Parquet.SourceGenerator.AbiMatrix/TestResults/";

string fixture = """
    <TestRun>
      <TestDefinitions><UnitTest id="1"><TestMethod className="ContractTest" /></UnitTest></TestDefinitions>
      <Results><UnitTestResult testId="1" outcome="Passed" /></Results>
      <ResultSummary><Counters total="1" executed="1" passed="1" /></ResultSummary>
    </TestRun>
    """;
XDocument control = XDocument.Parse(fixture);
if (
    !Verify(control, ["ContractTest"], out _)
    || Verify(control, ["MissingContractTest"], out _)
    || Verify(
        XDocument.Parse(fixture.Replace("outcome=\"Passed\"", "outcome=\"Skipped\"")),
        ["ContractTest"],
        out _
    )
)
{
    Console.Error.WriteLine("Required-suite gate positive controls failed.");
    return 1;
}

bool valid = true;
valid &= VerifyFile(
    Path.Combine(MainResults, CoreReport),
    [
        "Parquet.SourceGenerator.Tests.CiGateIntegrityTests",
        "Parquet.SourceGenerator.Tests.GoldenCodeGenRegressionTests",
        "Parquet.SourceGenerator.Tests.TestDataIntegrationTests",
        "Parquet.SourceGenerator.Tests.ColumnEncodingTests",
        "Parquet.SourceGenerator.Tests.UnifiedBatchTests",
        "Parquet.SourceGenerator.Tests.VersionAndSchemaEvolutionMatrixTests",
        "Parquet.SourceGenerator.Tests.Security.DecompressionGuardReadPathTests",
        "Parquet.SourceGenerator.Tests.Security.HostileParquetTests",
    ]
);
valid &= VerifyFile(
    Path.Combine(AbiResults, CoreReport),
    [
        "Parquet.SourceGenerator.AbiMatrix.OrderEventAbiExecutionTests",
        "Parquet.SourceGenerator.AbiMatrix.NestedOrderAbiExecutionTests",
    ]
);
valid &= VerifyFile(
    Path.Combine(MainResults, ExternalReport),
    ["Parquet.SourceGenerator.Tests.PyArrowInteropTests"]
);
return valid ? 0 : 1;

static bool VerifyFile(string path, string[] classes)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Missing required test report: {path}");
        return false;
    }

    try
    {
        bool success = Verify(XDocument.Load(path), classes, out string reason);
        if (!success)
            Console.Error.WriteLine($"{path}: {reason}");
        else
            Console.WriteLine($"{path}: all tests ran and {classes.Length} required suites passed.");
        return success;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not read {path}: {ex.Message}");
        return false;
    }
}

static bool Verify(XDocument report, string[] classes, out string reason)
{
    reason = "missing or invalid test counters";
    if (report.Root?.Name.LocalName != "TestRun" || classes.Length == 0)
        return false;

    XElement[] counters = report
        .Descendants()
        .Where(element => element.Name.LocalName == "Counters")
        .ToArray();
    if (counters.Length != 1)
        return false;

    if (
        !ReadCount(counters[0], "total", out int total)
        || !ReadCount(counters[0], "executed", out int executed)
        || !ReadCount(counters[0], "passed", out int passed)
        || total == 0
        || total != executed
        || total != passed
    )
        return false;

    var classById = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (XElement test in report.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
    {
        string? id = test.Attribute("id")?.Value;
        string? className = test
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TestMethod")
            ?.Attribute("className")
            ?.Value;
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(className))
            classById[id] = className;
    }

    var passedClasses = new HashSet<string>(StringComparer.Ordinal);
    foreach (
        XElement result in report.Descendants().Where(e => e.Name.LocalName == "UnitTestResult")
    )
    {
        string? id = result.Attribute("testId")?.Value;
        if (
            result.Attribute("outcome")?.Value == "Passed"
            && id is not null
            && classById.TryGetValue(id, out string? className)
        )
            passedClasses.Add(className);
    }

    string[] missing = classes.Where(name => !passedClasses.Contains(name)).ToArray();
    if (missing.Length > 0)
    {
        reason = "required suites did not execute successfully: " + string.Join(", ", missing);
        return false;
    }

    reason = string.Empty;
    return true;
}

static bool ReadCount(XElement counters, string name, out int value) =>
    int.TryParse(
        counters.Attribute(name)?.Value,
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out value
    );
