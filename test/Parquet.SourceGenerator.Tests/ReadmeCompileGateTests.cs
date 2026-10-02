using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using IOFile = System.IO.File;

namespace Parquet.SourceGenerator.Tests;

/// <summary>
/// Positive controls for <c>scripts/CheckReadme.cs</c> (#596), which compiles the C# examples in the
/// READMEs against the generator. The gate has to fail on an example that uses a removed member and
/// on a README that has nothing to compile, or "README examples use only the stable surface" holds
/// only until the next rename. The real READMEs are compiled by the CI step, not here.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReadmeCompileGateTests : IDisposable
{
    private const string Model = """
        ```csharp compile-file
        using Parquet.SourceGenerator;

        [ParquetSerializable]
        public partial record Item
        {
            [ParquetColumn("id", Order = 1)]
            public int Id { get; init; }
        }
        ```

        """;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "parquet-readme-gate-" + Guid.NewGuid().ToString("N")
    );

    public ReadmeCompileGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
    }

    [Fact]
    public async Task ExamplesThatUseOnlyTheStableSurfaceCompileAsync()
    {
        ScriptResult result = await RunAsync(
            Model
                + """
                ```csharp compile
                using var stream = new MemoryStream();
                Item[] items = await ItemParquet.From(stream).ToArrayAsync();
                await items.WriteParquetAsync(stream);
                ```
                """
        );

        result.ExitCode.ShouldBe(0, result.Describe());
        result.Stdout.ShouldContain("README examples compile: 2 block(s)");
    }

    [Fact]
    public async Task AnExampleThatUsesARemovedMemberFailsAndNamesTheReadmeLineAsync()
    {
        // ToListAsync was removed from the generated reader (#479): a README that still shows it
        // must fail the build, and the error must point at the README line, not generated code.
        ScriptResult result = await RunAsync(
            Model
                + """
                ```csharp compile
                using var stream = new MemoryStream();
                List<Item> items = await ItemParquet.From(stream).ToListAsync();
                ```
                """
        );

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("README examples do not compile");
        result.Stderr.ShouldContain("ToListAsync");
        result.Stderr.ShouldContain("fixture.md(");
    }

    [Fact]
    public async Task AReadmeWithNothingToCompileFailsBecauseItExaminedNothingAsync()
    {
        ScriptResult result = await RunAsync("# A README\n\n```csharp\nvar x = 1;\n```\n");

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("examined nothing");
    }

    [Fact]
    public async Task AFenceThatNeverClosesFailsAsync()
    {
        ScriptResult result = await RunAsync("# A README\n\n```csharp compile\nvar x = 1;\n");

        result.ExitCode.ShouldBe(1, result.Describe());
        result.Stderr.ShouldContain("a code fence never closes");
    }

    [Fact]
    public async Task TwoReadmesWithTheSameFileNameAreBothCompiledAsync()
    {
        string good =
            Model
            + "```csharp compile\nusing var s = new MemoryStream();\nItem[] i = await ItemParquet.From(s).ToArrayAsync();\n```\n";
        string broken =
            Model
            + "```csharp compile\nusing var s = new MemoryStream();\nList<Item> i = await ItemParquet.From(s).ToListAsync();\n```\n";

        // The broken one is second: if its generated file replaced the first's, or the first's
        // replaced it, one of the two would go uncompiled and this would pass.
        (await RunAsync(good, broken)).ExitCode.ShouldBe(1);
        (await RunAsync(broken, good)).ExitCode.ShouldBe(1);
        (await RunAsync(good, good)).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task AFourBacktickFenceMayContainAThreeBacktickLineAsync()
    {
        // CommonMark: a longer opener closes only on an equally long fence, so a fence-like line
        // inside it is content. The raw string literal puts such a line inside the C# block.
        const string fourTicks = "````";
        string readme =
            Model
            + fourTicks
            + "csharp compile\nvar text = \"\"\"\n```\n\"\"\";\n"
            + fourTicks
            + "\n";

        ScriptResult result = await RunAsync(readme);

        result.ExitCode.ShouldBe(0, result.Describe());
        result.Stdout.ShouldContain("2 compiled block(s)");
    }

    [Theory]
    [InlineData("README.md", 9)]
    [InlineData("PACKAGE_README.md", 4)]
    public void TheRealReadmesKeepTheirCompiledExamples(string readme, int atLeast)
    {
        // A README edit that drops the `compile` marker would silently stop gating the example.
        string text = IOFile.ReadAllText(Path.Combine(FindRepoRoot(), readme));
        int compiled = text.Split('\n')
            .Count(l => l.TrimEnd() is "```csharp compile" or "```csharp compile-file");

        compiled.ShouldBeGreaterThanOrEqualTo(atLeast, $"{readme} compiled-example count");
    }

    private async Task<ScriptResult> RunAsync(params string[] readmeTexts)
    {
        // Each fixture gets its own directory but the same file name, as two READMEs in different
        // folders would.
        var readmes = new System.Collections.Generic.List<string>();
        for (int i = 0; i < readmeTexts.Length; i++)
        {
            string directory = Path.Combine(_dir, "d" + i);
            Directory.CreateDirectory(directory);
            string readme = Path.Combine(directory, "fixture.md");
            await IOFile.WriteAllTextAsync(readme, readmeTexts[i]);
            readmes.Add(readme);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "run", "scripts/CheckReadme.cs", "--" })
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (string readme in readmes)
        {
            psi.ArgumentList.Add("--readme");
            psi.ArgumentList.Add(readme);
        }

        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        psi.Environment["DOTNET_BUILD_SERVER_DISABLE"] = "1";

        using Process? process = Process.Start(psi);
        process.ShouldNotBeNull();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (IOFile.Exists(Path.Combine(dir.FullName, "scripts", "CheckReadme.cs")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find repository root containing scripts/CheckReadme.cs"
        );
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Describe() => $"Stdout:\n{Stdout}\nStderr:\n{Stderr}";
    }
}
