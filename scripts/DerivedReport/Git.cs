using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

namespace Parquet.SourceGenerator.Tools.DerivedReport;

internal static class Git
{
    /// <summary>
    /// Unified diff of two files outside any repository, with the file named by its path inside
    /// the tree rather than the temporary directory it was read from.
    /// </summary>
    public static string DiffFiles(string before, string after, string relative)
    {
        // --no-index exits 1 when the files differ; only a higher code is a failure.
        string output = Run(
            null,
            allowedExitCode: 1,
            "diff",
            "--no-index",
            "--no-color",
            "--unified=3",
            "--src-prefix=base/",
            "--dst-prefix=head/",
            before,
            after
        );

        // Only the header, before the first hunk: inside a hunk, a removed "-- x" line reads
        // "--- x" and an added "++ x" line reads "+++ x", and rewriting those would corrupt it.
        var lines = output.Split('\n');
        for (
            int i = 0;
            i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal);
            i++
        )
        {
            if (lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                lines[i] = $"diff --git base/{relative} head/{relative}";
            }
            else if (lines[i].StartsWith("--- ", StringComparison.Ordinal))
            {
                lines[i] = before == "/dev/null" ? "--- /dev/null" : $"--- base/{relative}";
            }
            else if (lines[i].StartsWith("+++ ", StringComparison.Ordinal))
            {
                lines[i] = after == "/dev/null" ? "+++ /dev/null" : $"+++ head/{relative}";
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>Runs git in <paramref name="repo"/> and returns its standard output.</summary>
    public static string Run(string? repo, int allowedExitCode, params string[] arguments)
    {
        var args = new List<string>();
        if (repo is not null)
        {
            args.Add("-C");
            args.Add(repo);
        }

        // Pinned output: no pager, colour, rename detection or locale-dependent quoting, so the same
        // commits give the same text on every runner.
        args.Add("-c");
        args.Add("core.quotepath=false");
        args.Add("-c");
        args.Add("color.ui=false");
        args.AddRange(arguments);

        var result = Cli.Wrap("git")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync()
            .GetAwaiter()
            .GetResult();

        if (result.ExitCode != 0 && result.ExitCode != allowedExitCode)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited {result.ExitCode}: {result.StandardError}"
            );
        }

        return result.StandardOutput;
    }
}
