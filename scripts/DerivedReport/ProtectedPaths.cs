namespace Parquet.SourceGenerator.Tools.DerivedReport;

/// <summary>The paths listed in .github/protected-paths.txt: CI, build and tooling.</summary>
internal sealed class ProtectedPaths(IReadOnlyList<string> entries)
{
    public const string File = ".github/protected-paths.txt";

    public static ProtectedPaths Load(string repo)
    {
        string path = Path.Combine(repo, File);
        return new ProtectedPaths(
            System.IO.File.Exists(path)
                ? System
                    .IO.File.ReadAllLines(path)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToList()
                : []
        );
    }

    /// <summary>An entry ending in "/" covers everything under it; any other must match exactly.</summary>
    public bool Matches(string path) =>
        entries.Any(e =>
            e.EndsWith('/') ? path.StartsWith(e, StringComparison.Ordinal) : path == e
        );
}
