using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Drives <c>scripts/TestResultsDiffraPayload.cs</c>, the shadow step that turns the test job's
/// TRX reports into Diffra <c>diffra/test-results@1</c> payloads and a collection document, and
/// <c>scripts/JsonSchemaCheck.cs</c>, which checks them against the schemas vendored under
/// <c>diffra/schemas/</c>.
/// </summary>
/// <remarks>
/// CI's own reports hold only passed tests, because the required-suite check fails the job on
/// anything else. The failed, timed-out, skipped and duplicate shapes are therefore only ever
/// exercised here.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TestResultsDiffraPayloadTests : IDisposable
{
    private const string Commit = "06d1f40000000000000000000000000000000000";
    private const string PayloadSchema = "diffra/schemas/test-results-payload.schema.json";
    private const string CollectionSchema = "diffra/schemas/collection-v1.schema.json";
    private const string Parser = "Example.Tests.ParserTests";

    private const string Report = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="t1" testName="Example.Tests.ParserTests.ParsesEmptyInput" duration="00:00:00.0015320" outcome="Passed" />
            <UnitTestResult testId="t2" testName="Example.Tests.ParserTests.RejectsBom" duration="00:00:00.0022100" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Expected: rejected
        Actual: accepted</Message>
                  <StackTrace>at Example.Tests.ParserTests.RejectsBom()</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testId="t3" testName="Example.Tests.ParserTests.Parses(input: &quot;a&quot;)" duration="00:00:00.0000001" outcome="Passed" />
            <UnitTestResult testId="t3" testName="Example.Tests.ParserTests.Parses(input: &quot;b&quot;)" duration="00:00:01" outcome="Passed" />
            <UnitTestResult testId="t4" testName="Example.Tests.ParserTests.Hangs" duration="00:01:00" outcome="Timeout" />
            <UnitTestResult testId="t5" testName="Example.Tests.ParserTests.NeedsNetwork" outcome="NotExecuted">
              <Output>
                <ErrorInfo>
                  <Message>requires network</Message>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testId="t6" testName="Example.Tests.ParserTests.Flaky" duration="00:00:00.0010000" outcome="Passed" />
            <UnitTestResult testId="t6" testName="Example.Tests.ParserTests.Flaky" duration="00:00:00.0020000" outcome="Failed" />
            <UnitTestResult testId="t7" testName="Display name" duration="00:00:00" outcome="Passed" />
          </Results>
          <TestDefinitions>
            <UnitTest name="Example.Tests.ParserTests.ParsesEmptyInput" id="t1"><TestMethod className="Example.Tests.ParserTests" name="ParsesEmptyInput" /></UnitTest>
            <UnitTest name="Example.Tests.ParserTests.RejectsBom" id="t2"><TestMethod className="Example.Tests.ParserTests" name="RejectsBom" /></UnitTest>
            <UnitTest name="Example.Tests.ParserTests.Parses" id="t3"><TestMethod className="Example.Tests.ParserTests" name="Parses" /></UnitTest>
            <UnitTest name="Example.Tests.ParserTests.Hangs" id="t4"><TestMethod className="Example.Tests.ParserTests" name="Hangs" /></UnitTest>
            <UnitTest name="Example.Tests.ParserTests.NeedsNetwork" id="t5"><TestMethod className="Example.Tests.ParserTests" name="NeedsNetwork" /></UnitTest>
            <UnitTest name="Example.Tests.ParserTests.Flaky" id="t6"><TestMethod className="Example.Tests.ParserTests" name="Flaky" /></UnitTest>
            <UnitTest name="Display name" id="t7"><TestMethod className="Example.Tests.ParserTests" name="Named" /></UnitTest>
            <UnitTest name="Example.Tests.ParserTests.NeverRan" id="t8"><TestMethod className="Example.Tests.ParserTests" name="NeverRan" /></UnitTest>
          </TestDefinitions>
        </TestRun>
        """;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tests-diffra-");

    private string OutputDirectory => Path.Combine(_root.FullName, "payload");

    private string CorePayload => Path.Combine(OutputDirectory, "test-results.core.json");

    private string Collection => Path.Combine(OutputDirectory, "collection.json");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task AReportBecomesAPayloadThatFitsTheSchemaAsync()
    {
        string report = WriteReport("core.trx", Report);

        ScriptResult result = await ConvertAsync("--commit", Commit, "--input", $"core={report}");

        result.ExitCode.ShouldBe(0, result.ToString());
        result.Stdout.ShouldContain("Examined 9 test result(s) from 1 of 1 TRX report(s)");

        using (JsonDocument payload = JsonDocument.Parse(System.IO.File.ReadAllText(CorePayload)))
        {
            JsonElement root = payload.RootElement;
            root.GetProperty("scope").GetString().ShouldBe("parquet-sourcegenerator");
            root.GetProperty("methodology").GetString().ShouldStartWith("diffra/test-results@1;");
            root.GetProperty("stratum").GetProperty("suite").GetString().ShouldBe("core");

            JsonElement results = root.GetProperty("metrics")
                .GetProperty("tests")
                .GetProperty("results");

            // 100 ns ticks become whole nanoseconds, as a decimal string.
            JsonElement passed = Value(results, $"{Parser}.ParsesEmptyInput");
            passed.GetProperty("tag").GetString().ShouldBe("success");
            passed.GetProperty("duration").GetString().ShouldBe("1532000");

            JsonElement failed = Value(results, $"{Parser}.RejectsBom");
            failed.GetProperty("tag").GetString().ShouldBe("error");
            failed.GetProperty("kind").GetString().ShouldBe("exception");
            failed.GetProperty("duration").GetString().ShouldBe("2210000");
            failed.GetProperty("message").GetString().ShouldStartWith("Expected: rejected");

            // Each row of a data-driven test is its own key, although the rows share a test id.
            Value(results, $"{Parser}.Parses(input: \"a\")")
                .GetProperty("duration")
                .GetString()
                .ShouldBe("100");
            Value(results, $"{Parser}.Parses(input: \"b\")")
                .GetProperty("duration")
                .GetString()
                .ShouldBe("1000000000");

            JsonElement timedOut = Value(results, $"{Parser}.Hangs");
            timedOut.GetProperty("kind").GetString().ShouldBe("timeout");
            timedOut.GetProperty("message").GetString().ShouldBe("Timeout");

            JsonElement skipped = Value(results, $"{Parser}.NeedsNetwork");
            skipped.GetProperty("tag").GetString().ShouldBe("skipped");
            skipped.GetProperty("reason").GetString().ShouldBe("requires network");
            skipped.TryGetProperty("duration", out _).ShouldBeFalse();

            // Two results under one key keep the worst outcome.
            JsonElement flaky = Value(results, $"{Parser}.Flaky");
            flaky.GetProperty("tag").GetString().ShouldBe("error");
            flaky.GetProperty("duration").GetString().ShouldBe("2000000");

            // A name that does not start with the class name is prefixed with it.
            Value(results, $"{Parser}.Display name")
                .GetProperty("duration")
                .GetString()
                .ShouldBe("0");

            // A definition with no result is planned and marked as not collected.
            JsonElement plan = root.GetProperty("plan").GetProperty("tests");
            plan.GetProperty("keys").GetArrayLength().ShouldBe(9);
            plan.GetProperty("failed")
                .GetProperty($"{Parser}.NeverRan")
                .GetString()
                .ShouldNotBeNullOrEmpty();
            results.TryGetProperty($"{Parser}.NeverRan", out _).ShouldBeFalse();
        }

        using (JsonDocument collection = JsonDocument.Parse(System.IO.File.ReadAllText(Collection)))
        {
            JsonElement root = collection.RootElement;
            root.GetProperty("status").GetString().ShouldBe("complete");
            root.GetProperty("subject").GetProperty("commit").GetString().ShouldBe(Commit);

            JsonElement stratum = root.GetProperty("metrics")[0].GetProperty("strata")[0];
            stratum.GetProperty("status").GetString().ShouldBe("collected");
            stratum
                .GetProperty("evidenceId")
                .GetString()
                .ShouldBe(
                    "sha256:"
                        + Convert.ToHexString(
                            SHA256.HashData(System.IO.File.ReadAllBytes(CorePayload))
                        ),
                    StringCompareShould.IgnoreCase
                );
        }

        ScriptResult valid = await CheckAsync();
        valid.ExitCode.ShouldBe(0, valid.ToString());
        valid.Stdout.ShouldContain("Examined 2 document(s); 0 invalid.");

        // Negative control: Diffra's own invalid example, a field outside its tag.
        string text = System.IO.File.ReadAllText(CorePayload);
        string broken = text.Replace(
            "\"tag\": \"success\",",
            "\"tag\": \"success\", \"message\": \"ok\",",
            StringComparison.Ordinal
        );
        broken.ShouldNotBe(text);
        System.IO.File.WriteAllText(CorePayload, broken);

        ScriptResult invalid = await CheckAsync();
        invalid.ExitCode.ShouldBe(1, invalid.ToString());
        invalid.Stdout.ShouldContain("Examined 2 document(s); 1 invalid.");
    }

    [Fact]
    public async Task AMissingReportLeavesTheCollectionIncompleteAsync()
    {
        string report = WriteReport("core.trx", Report);
        string missing = Path.Combine(_root.FullName, "external.trx");

        ScriptResult result = await ConvertAsync(
            "--commit",
            Commit,
            "--input",
            $"core={report}",
            "--input",
            $"external={missing}"
        );

        result.ExitCode.ShouldBe(0, result.ToString());
        result.Stdout.ShouldContain("from 1 of 2 TRX report(s)");
        System
            .IO.File.Exists(Path.Combine(OutputDirectory, "test-results.external.json"))
            .ShouldBeFalse();

        string text = System.IO.File.ReadAllText(Collection);
        using (JsonDocument collection = JsonDocument.Parse(text))
        {
            JsonElement root = collection.RootElement;
            root.GetProperty("status").GetString().ShouldBe("incomplete");

            JsonElement external = root.GetProperty("metrics")[0].GetProperty("strata")[1];
            external.GetProperty("stratum").GetProperty("suite").GetString().ShouldBe("external");
            external.GetProperty("status").GetString().ShouldBe("missing");
            external.GetProperty("reason").GetString().ShouldNotBeNullOrEmpty();
            external.TryGetProperty("evidenceId", out _).ShouldBeFalse();
        }

        ScriptResult valid = await CheckAsync();
        valid.ExitCode.ShouldBe(0, valid.ToString());

        // Negative control: a collection with a missing required stratum cannot claim complete.
        string broken = text.Replace(
            "\"status\": \"incomplete\"",
            "\"status\": \"complete\"",
            StringComparison.Ordinal
        );
        broken.ShouldNotBe(text);
        System.IO.File.WriteAllText(Collection, broken);

        ScriptResult invalid = await CheckAsync();
        invalid.ExitCode.ShouldBe(1, invalid.ToString());
    }

    [Fact]
    public async Task NoConvertibleReportWritesNothingAsync()
    {
        string unreadable = WriteReport("core.trx", "<TestRun><Results>");
        string empty = WriteReport("abi.trx", "<TestRun><Results /></TestRun>");
        string missing = Path.Combine(_root.FullName, "external.trx");

        ScriptResult result = await ConvertAsync(
            "--commit",
            Commit,
            "--input",
            $"core={unreadable}",
            "--input",
            $"abi={empty}",
            "--input",
            $"external={missing}"
        );

        // A collection document needs one collected stratum, so there is nothing valid to write.
        result.ExitCode.ShouldBe(1, result.ToString());
        result.Stderr.ShouldContain("core: failed");
        result.Stderr.ShouldContain("abi: failed");
        result.Stderr.ShouldContain("external: missing");
        Directory.Exists(OutputDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AShortCommitIsAUsageErrorAsync()
    {
        string report = WriteReport("core.trx", Report);

        ScriptResult result = await ConvertAsync("--commit", "06d1f4", "--input", $"core={report}");

        result.ExitCode.ShouldBe(2, result.ToString());
        Directory.Exists(OutputDirectory).ShouldBeFalse();
    }

    private static JsonElement Value(JsonElement results, string key) =>
        results.GetProperty(key).GetProperty("value");

    private string WriteReport(string name, string content)
    {
        string path = Path.Combine(_root.FullName, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private Task<ScriptResult> ConvertAsync(params string[] arguments) =>
        RunAsync(
            "scripts/TestResultsDiffraPayload.cs",
            [OutputDirectory, "--repository", "rtkelly-labs/Parquet.SourceGenerator", .. arguments]
        );

    private Task<ScriptResult> CheckAsync() =>
        RunAsync(
            "scripts/JsonSchemaCheck.cs",
            ["--schema", PayloadSchema, CorePayload, "--schema", CollectionSchema, Collection]
        );

    private static async Task<ScriptResult> RunAsync(string script, string[] arguments)
    {
        string homeDotnetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet"
        );
        string homeDotnet = Path.Combine(homeDotnetDir, "dotnet");

        var psi = new ProcessStartInfo
        {
            FileName = System.IO.File.Exists(homeDotnet) ? homeDotnet : "dotnet",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--");
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        psi.Environment["DOTNET_BUILD_SERVER_DISABLE"] = "1";
        if (Directory.Exists(homeDotnetDir))
        {
            psi.Environment["DOTNET_ROOT"] = homeDotnetDir;
            psi.Environment["PATH"] =
                homeDotnetDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        using var process = Process.Start(psi);
        process.ShouldNotBeNull();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (
                System.IO.File.Exists(
                    Path.Combine(dir.FullName, "scripts", "TestResultsDiffraPayload.cs")
                )
            )
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root containing scripts/TestResultsDiffraPayload.cs"
        );
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public override string ToString() =>
            $"Exit {ExitCode}\nStdout:\n{Stdout}\nStderr:\n{Stderr}";
    }
}
