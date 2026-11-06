using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>
/// <c>PARQAPI001</c> — fails the build when a golden generated file emits a public member that its
/// companion <c>.api.txt</c> catalogue does not list.
/// </summary>
/// <remarks>
/// <para><b>Why an analyzer rather than an MSBuild target.</b> The rule needs to parse C#, render
/// the same signature grammar the baselines use, and diff it. An MSBuild target could only shell
/// out to something that does that, which is a second executable, a second build ordering problem
/// and a second place for the renderer to live. An analyzer fed the catalogues as
/// <c>AdditionalFiles</c> is also the exact shape of the mechanism already guarding the shipped
/// surface in this repository — <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c> with
/// <c>PublicAPI.Shipped.txt</c> — so the two governed surfaces are enforced the same way and fail
/// the same way.</para>
///
/// <para><b>Why it cannot reach a consumer.</b> Three independent reasons, any one of which is
/// sufficient. The analyzer lives in <c>tools/Parquet.SourceGenerator.ApiGates</c>, which is
/// <c>IsPackable=false</c>. The two generator packages build their nupkg payload from
/// <c>$(TargetPath)</c> alone, so no transitive analyzer is packed. And the rule short-circuits
/// unless the compilation was handed <c>.api.txt</c> additional files, which only this
/// repository's test project supplies. A consumer's own models are in none of this repository's
/// baselines, so a gate that ran in their compilation would fail every build — hence the
/// belt-and-braces.</para>
///
/// <para><b>Direction.</b> The build error fires on <i>additions</i>: a member present in the
/// emitted source and absent from the catalogue. Removals are drift in the other direction, are
/// not "something entering a surface", and are already caught by
/// <c>GoldenCodeGenRegressionTests</c>, which compares the two files byte for byte.</para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmittedApiGateAnalyzer : DiagnosticAnalyzer
{
    private const string GeneratedSuffix = ".g.cs";
    private const string BaselineSuffix = ".api.txt";
    private const int MaxMembersInMessage = 12;

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(ApiGateDiagnostics.UncataloguedEmittedApi);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        ImmutableArray<AdditionalText> additionalFiles = context.Options.AdditionalFiles;

        var baselines = new Dictionary<string, AdditionalText>(StringComparer.Ordinal);
        foreach (AdditionalText file in additionalFiles)
        {
            if (file.Path.EndsWith(BaselineSuffix, StringComparison.Ordinal))
            {
                baselines[Stem(file.Path, BaselineSuffix)] = file;
            }
        }

        // No catalogues supplied: this is not a compilation the contract governs. Consumers of the
        // shipped packages always take this path.
        if (baselines.Count == 0)
        {
            return;
        }

        ApiLedger ledger = ApiLedger.Read(FindLedger(additionalFiles), context.CancellationToken);

        foreach (AdditionalText golden in additionalFiles)
        {
            if (!golden.Path.EndsWith(GeneratedSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string stem = Stem(golden.Path, GeneratedSuffix);
            if (!baselines.TryGetValue(stem, out AdditionalText? baseline))
            {
                continue;
            }

            SourceText? emitted = golden.GetText(context.CancellationToken);
            if (emitted is null)
            {
                continue;
            }

            ImmutableHashSet<string> catalogued = ApiCatalogue.Read(
                baseline,
                context.CancellationToken
            );

            var uncatalogued = new List<string>();
            foreach (string line in SplitRendered(GeneratedApiBaseline.Create(emitted.ToString())))
            {
                if (!catalogued.Contains(line) && !ledger.IsExempt(line))
                {
                    uncatalogued.Add(line);
                }
            }

            if (uncatalogued.Count == 0)
            {
                continue;
            }

            uncatalogued.Sort(StringComparer.Ordinal);
            context.ReportDiagnostic(
                Diagnostic.Create(
                    ApiGateDiagnostics.UncataloguedEmittedApi,
                    ExternalFileLocation(baseline.Path),
                    FileName(baseline.Path),
                    uncatalogued.Count,
                    FileName(golden.Path),
                    Summarize(uncatalogued)
                )
            );
        }
    }

    private static AdditionalText? FindLedger(ImmutableArray<AdditionalText> additionalFiles)
    {
        foreach (AdditionalText file in additionalFiles)
        {
            if (FileName(file.Path).Equals("LEDGER.md", StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitRendered(string rendered)
    {
        foreach (string line in rendered.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed[0] != '#')
            {
                yield return trimmed;
            }
        }
    }

    private static string Summarize(List<string> members)
    {
        IEnumerable<string> shown =
            members.Count > MaxMembersInMessage ? members.Take(MaxMembersInMessage) : members;
        string joined = string.Join("; ", shown.Select(member => "'" + member + "'"));
        return members.Count > MaxMembersInMessage
            ? joined
                + "; and "
                + (members.Count - MaxMembersInMessage).ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                )
                + " more"
            : joined;
    }

    private static string Stem(string path, string suffix) =>
        FileName(path).Substring(0, FileName(path).Length - suffix.Length);

    private static readonly char[] PathSeparators = { '/', '\\' };

    private static string FileName(string path)
    {
        int slash = path.LastIndexOfAny(PathSeparators);
        return slash < 0 ? path : path.Substring(slash + 1);
    }

    private static Location ExternalFileLocation(string path) =>
        Location.Create(
            path,
            new TextSpan(0, 0),
            new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0))
        );
}
