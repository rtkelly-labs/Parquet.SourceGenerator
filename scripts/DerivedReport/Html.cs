using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Parquet.SourceGenerator.Tools.DerivedReport;

/// <summary>
/// Renders a page component to one HTML document with the official HtmlRenderer — outside any web
/// host, no ASP.NET shared framework, just the Components.Web package. Output is encoded by Razor.
/// </summary>
internal static partial class Html
{
    public static async Task<string> RenderAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent
    >(IDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (
                await renderer.RenderComponentAsync<TComponent>(
                    ParameterView.FromDictionary(parameters)
                )
            ).ToHtmlString()
        );
        return "<!DOCTYPE html>\n" + html.Trim() + "\n";
    }

    /// <summary>Thousands-separated, culture-invariant: the same number always reads the same.</summary>
    public static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>An id from a title or path: lowercase, runs of anything else become "-".</summary>
    public static string Slug(string text) =>
        NonAlphanumeric().Replace(text.ToLowerInvariant(), "-");

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.None, 1000)]
    private static partial Regex NonAlphanumeric();
}

/// <summary>Splits Markdown-ish text on `backticks` into plain and code segments.</summary>
internal static class Inline
{
    public static IEnumerable<(string Text, bool IsCode)> Split(string text) =>
        text.Split('`').Select((part, i) => (part, i % 2 == 1)).Where(p => p.part.Length > 0);
}

/// <summary>report.css and report.js, read once from beside the entry script.</summary>
internal static class PageAssets
{
    private static readonly string Directory =
        AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? throw new InvalidOperationException(
            "Run as a file-based app: dotnet run scripts/DerivedReport/DerivedReport.cs"
        );

    public static string Css { get; } =
        File.ReadAllText(Path.Combine(Directory, "report.css")).Trim();

    public static string Js { get; } =
        File.ReadAllText(Path.Combine(Directory, "report.js")).Trim();
}
