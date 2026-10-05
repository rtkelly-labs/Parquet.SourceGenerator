using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Parquet.SourceGenerator.Tests;

public class ApiModelExtractionTests
{
    [Fact]
    public void ExtractedApiModelContainsPublicTypesAndDocumentation()
    {
        string repoRoot = FindRepoRoot();
        string artifactFile = Path.Combine(repoRoot, "artifacts", "api-model.json");
        string? tempOut = null;

        if (!System.IO.File.Exists(artifactFile))
        {
            tempOut = Path.Combine(
                repoRoot,
                "temp",
                "parq-api-test-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(tempOut);
            var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments =
                        $"run scripts/ExportApiDocs.cs -- --repo \"{repoRoot}\" --out \"{tempOut}\"",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            )!;
            process.WaitForExit();
            process.ExitCode.ShouldBe(0);
            artifactFile = Path.Combine(tempOut, "api-model.json");
        }

        try
        {
            System.IO.File.Exists(artifactFile).ShouldBeTrue();
            string json = System.IO.File.ReadAllText(artifactFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            root.GetProperty("projectName").GetString().ShouldBe("Parquet.SourceGenerator");

            var namespaces = root.GetProperty("namespaces").EnumerateArray().ToList();
            namespaces.ShouldNotBeEmpty();

            // Verify internal containing types are NOT exposed (e.g. NullableReadShape, LaneKind)
            var allTypes = namespaces
                .SelectMany(n => n.GetProperty("types").EnumerateArray())
                .ToList();
            allTypes
                .Any(t => t.GetProperty("name").GetString() == "NullableReadShape")
                .ShouldBeFalse();
            allTypes.Any(t => t.GetProperty("name").GetString() == "LaneKind").ShouldBeFalse();

            var sgNs = namespaces.FirstOrDefault(n =>
                n.GetProperty("name").GetString() == "Parquet.SourceGenerator"
            );
            sgNs.ValueKind.ShouldNotBe(JsonValueKind.Undefined);

            var types = sgNs.GetProperty("types").EnumerateArray().ToList();
            types.ShouldNotBeEmpty();

            var colAttr = types.FirstOrDefault(t =>
                t.GetProperty("name").GetString() == "ParquetColumnAttribute"
            );
            colAttr.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
            colAttr.GetProperty("kind").GetString().ShouldBe("Class");
            colAttr.GetProperty("summary").GetString().ShouldNotBeNullOrWhiteSpace();

            var hierarchy = colAttr
                .GetProperty("inheritanceHierarchy")
                .EnumerateArray()
                .Select(h => h.GetString())
                .ToList();
            hierarchy.ShouldContain("object");
            hierarchy.ShouldContain("System.Attribute");
            hierarchy.Last().ShouldBe("Parquet.SourceGenerator.ParquetColumnAttribute");

            var props = colAttr.GetProperty("properties").EnumerateArray().ToList();
            var nameProp = props.FirstOrDefault(p => p.GetProperty("name").GetString() == "Name");
            nameProp.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
            nameProp.GetProperty("returnType").GetString().ShouldBe("string?");
            nameProp.GetProperty("summary").GetString().ShouldNotBeNullOrWhiteSpace();

            var ctors = colAttr.GetProperty("constructors").EnumerateArray().ToList();
            ctors.Count.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            if (tempOut != null && Directory.Exists(tempOut))
            {
                try
                {
                    Directory.Delete(tempOut, recursive: true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (
            dir != null
            && !System.IO.File.Exists(Path.Combine(dir, "Parquet.SourceGenerator.slnx"))
            && !System.IO.File.Exists(Path.Combine(dir, "Directory.Build.props"))
        )
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir
            ?? throw new InvalidOperationException(
                "Could not find repository root containing Parquet.SourceGenerator.sln"
            );
    }
}
