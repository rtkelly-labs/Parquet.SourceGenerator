#pragma warning disable CA1510, CA1865

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Parquet.SourceGenerator.ApiGates;

/// <summary>Which backend has a member the other lacks.</summary>
public enum ParityDirection
{
    /// <summary>The modern (Parquet.Net 6.x) backend emits the member and the legacy one does not.</summary>
    ModernOnly,

    /// <summary>The legacy (Parquet.Net 4.x/5.x) backend emits the member and the modern one does not.</summary>
    LegacyOnly,
}

/// <summary>Whether an allowlisted difference is a gap to close or a recorded owner decision.</summary>
public enum ParityEntryKind
{
    /// <summary>A gap to close. The entry is removed by the change that closes it.</summary>
    Temporary,

    /// <summary>A recorded owner decision that the member is modern-only. The legacy backend must not emit it.</summary>
    Permanent,
}

/// <summary>One line of the parity allowlist (<c>src/api/legacy-parity-allowlist.txt</c>).</summary>
public sealed class ParityAllowlistEntry
{
    /// <summary>The token that stands for a whole model the legacy backend does not emit.</summary>
    public const string WholeModel = "*";

    /// <summary>Temporary or permanent, from the section the entry sits in.</summary>
    public ParityEntryKind Kind { get; }

    /// <summary>The tracking issue of the section, for example <c>#494</c>.</summary>
    public string Tracking { get; }

    /// <summary>The reason given by the section header.</summary>
    public string Reason { get; }

    /// <summary>Which backend has the member.</summary>
    public ParityDirection Direction { get; }

    /// <summary>The golden model the difference belongs to.</summary>
    public string Model { get; }

    /// <summary>The normalised API line, or <see cref="WholeModel"/>.</summary>
    public string Line { get; }

    /// <summary>1-based line number in the allowlist file, for error messages.</summary>
    public int LineNumber { get; }

    /// <summary>Initializes a new <see cref="ParityAllowlistEntry"/>.</summary>
    public ParityAllowlistEntry(
        ParityEntryKind kind,
        string tracking,
        string reason,
        ParityDirection direction,
        string model,
        string line,
        int lineNumber
    )
    {
        Kind = kind;
        Tracking = tracking;
        Reason = reason;
        Direction = direction;
        Model = model;
        Line = line;
        LineNumber = lineNumber;
    }

    /// <summary>The entry as it is written in the allowlist file.</summary>
    public string Describe() =>
        (Direction == ParityDirection.ModernOnly ? "modern-only " : "legacy-only ")
        + Model
        + ": "
        + Line;
}

/// <summary>
/// The emitted API of one golden model through both backends, already normalised with
/// <see cref="BackendParityGate.Normalize"/>.
/// </summary>
public sealed class ParityModelSurface
{
    /// <summary>The model's type name, for example <c>OrderEvent</c>.</summary>
    public string Model { get; }

    /// <summary>The modern backend's normalised API lines.</summary>
    public IReadOnlyList<string> Modern { get; }

    /// <summary>
    /// The legacy backend's normalised API lines, or <see langword="null"/> when the legacy
    /// generator emitted no source for the model at all.
    /// </summary>
    public IReadOnlyList<string>? Legacy { get; }

    /// <summary>Initializes a new <see cref="ParityModelSurface"/>.</summary>
    public ParityModelSurface(
        string model,
        IReadOnlyList<string> modern,
        IReadOnlyList<string>? legacy
    )
    {
        Model = model;
        Modern = modern;
        Legacy = legacy;
    }
}

/// <summary>The outcome of <see cref="BackendParityGate.Verify"/>.</summary>
public sealed class ParityGateResult
{
    /// <summary>Every violation. Empty means the gate passed.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>How many models were compared.</summary>
    public int ModelsExamined { get; }

    /// <summary>How many API lines (both backends, all models) were compared.</summary>
    public int LinesExamined { get; }

    /// <summary>How many differences were found, all of them allowlisted when the gate passes.</summary>
    public int Differences { get; }

    /// <summary>Initializes a new <see cref="ParityGateResult"/>.</summary>
    public ParityGateResult(
        IReadOnlyList<string> errors,
        int modelsExamined,
        int linesExamined,
        int differences
    )
    {
        Errors = errors;
        ModelsExamined = modelsExamined;
        LinesExamined = linesExamined;
        Differences = differences;
    }
}

/// <summary>
/// The legacy/modern API parity gate (#493, docs/guides/legacy-support.md § Enforcement). Every
/// difference between the two backends' emitted API for a golden model must be listed in the
/// allowlist, and every allowlist entry must still be a real difference, so the list can only
/// shrink. A permanent (modern-only by decision) entry additionally asserts the member is absent
/// from the legacy surface, never present-and-throwing.
/// </summary>
public static class BackendParityGate
{
    private const string LegacyExtensionsSuffix = "ParquetLegacyExtensions";
    private const string ModernExtensionsSuffix = "ParquetExtensions";

    private static readonly Regex SectionHeader = new(
        @"^\[(?<kind>temporary|permanent) (?<tracking>#[0-9]+)\] (?<reason>\S.*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    private static readonly Regex EntryLine = new(
        @"^(?<direction>modern-only|legacy-only) (?<model>[A-Za-z_][A-Za-z0-9_]*): (?<line>\S.*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    /// <summary>
    /// Normalises one rendered API line so the two backends become comparable: the namespace
    /// prefix is removed and the legacy extension class name <c>&lt;Model&gt;ParquetLegacyExtensions</c>
    /// becomes the modern <c>&lt;Model&gt;ParquetExtensions</c>. Nothing else is rewritten, so a
    /// difference in a member name, a parameter or a return type survives normalisation.
    /// </summary>
    public static string Normalize(string apiLine, string modelNamespace)
    {
        if (apiLine is null)
        {
            throw new ArgumentNullException(nameof(apiLine));
        }

        string line = apiLine.Trim();
        if (!string.IsNullOrEmpty(modelNamespace))
        {
            line = line.Replace(modelNamespace + ".", string.Empty);
        }

        return line.Replace(LegacyExtensionsSuffix, ModernExtensionsSuffix);
    }

    /// <summary>
    /// The member lines of a rendered <c>.api.txt</c> (comments and the <c>#nullable</c> header
    /// dropped), normalised, de-duplicated and sorted ordinally.
    /// </summary>
    public static IReadOnlyList<string> NormalizeApi(string apiText, string modelNamespace)
    {
        if (apiText is null)
        {
            throw new ArgumentNullException(nameof(apiText));
        }

        var lines = new SortedSet<string>(StringComparer.Ordinal);
        using (var reader = new StringReader(apiText))
        {
            string? raw;
            while ((raw = reader.ReadLine()) is not null)
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add(Normalize(trimmed, modelNamespace));
            }
        }

        return new List<string>(lines);
    }

    /// <summary>
    /// Parses the allowlist. Entries sit under a section header
    /// <c>[temporary #N] reason</c> or <c>[permanent #N] reason</c>; an entry is
    /// <c>modern-only Model: line</c> or <c>legacy-only Model: line</c>. A permanent entry must be
    /// modern-only: a legacy-only member is never a decision, it is a second surface.
    /// </summary>
    public static IReadOnlyList<ParityAllowlistEntry> ParseAllowlist(string content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var entries = new List<ParityAllowlistEntry>();
        ParityEntryKind? kind = null;
        string tracking = string.Empty;
        string reason = string.Empty;
        int lineNumber = 0;

        using (var reader = new StringReader(content))
        {
            string? raw;
            while ((raw = reader.ReadLine()) is not null)
            {
                lineNumber++;
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                Match header = SectionHeader.Match(trimmed);
                if (header.Success)
                {
                    kind =
                        header.Groups["kind"].Value == "permanent"
                            ? ParityEntryKind.Permanent
                            : ParityEntryKind.Temporary;
                    tracking = header.Groups["tracking"].Value;
                    reason = header.Groups["reason"].Value;
                    continue;
                }

                Match entry = EntryLine.Match(trimmed);
                if (!entry.Success)
                {
                    throw new FormatException(
                        "Parity allowlist line "
                            + lineNumber.ToString(CultureInfo.InvariantCulture)
                            + " is neither a section header nor an entry: '"
                            + raw
                            + "'"
                    );
                }

                if (kind is null)
                {
                    throw new FormatException(
                        "Parity allowlist line "
                            + lineNumber.ToString(CultureInfo.InvariantCulture)
                            + " is an entry outside any [temporary #N] / [permanent #N] section."
                    );
                }

                ParityDirection direction =
                    entry.Groups["direction"].Value == "legacy-only"
                        ? ParityDirection.LegacyOnly
                        : ParityDirection.ModernOnly;
                if (kind == ParityEntryKind.Permanent && direction == ParityDirection.LegacyOnly)
                {
                    throw new FormatException(
                        "Parity allowlist line "
                            + lineNumber.ToString(CultureInfo.InvariantCulture)
                            + " is a permanent legacy-only entry. Only modern-only members can be "
                            + "permanent; a legacy-only member is a gap to close."
                    );
                }

                entries.Add(
                    new ParityAllowlistEntry(
                        kind.Value,
                        tracking,
                        reason,
                        direction,
                        entry.Groups["model"].Value,
                        entry.Groups["line"].Value.Trim(),
                        lineNumber
                    )
                );
            }
        }

        return entries;
    }

    /// <summary>
    /// Compares each model's two surfaces and checks every difference against the allowlist.
    /// Throws when no model is examined, so the gate cannot pass on nothing.
    /// </summary>
    public static ParityGateResult Verify(
        IReadOnlyList<ParityModelSurface> models,
        IReadOnlyList<ParityAllowlistEntry> allowlist
    )
    {
        if (models is null)
        {
            throw new ArgumentNullException(nameof(models));
        }

        if (allowlist is null)
        {
            throw new ArgumentNullException(nameof(allowlist));
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                "Backend parity gate examined 0 models; positive control failed."
            );
        }

        var errors = new List<string>();
        var differences = new HashSet<string>(StringComparer.Ordinal);
        var modernLines = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var legacyLines = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        int linesExamined = 0;

        foreach (ParityModelSurface model in models)
        {
            if (modernLines.ContainsKey(model.Model))
            {
                errors.Add("Model '" + model.Model + "' is examined twice.");
                continue;
            }

            if (model.Modern.Count == 0)
            {
                errors.Add(
                    "Model '"
                        + model.Model
                        + "' has an empty modern surface; the gate would compare nothing."
                );
            }

            var modern = new HashSet<string>(model.Modern, StringComparer.Ordinal);
            modernLines[model.Model] = modern;
            linesExamined += modern.Count;

            if (model.Legacy is null)
            {
                legacyLines[model.Model] = null;
                differences.Add(
                    Key(ParityDirection.ModernOnly, model.Model, ParityAllowlistEntry.WholeModel)
                );
                continue;
            }

            var legacy = new HashSet<string>(model.Legacy, StringComparer.Ordinal);
            legacyLines[model.Model] = legacy;
            linesExamined += legacy.Count;

            foreach (string line in modern)
            {
                if (!legacy.Contains(line))
                {
                    differences.Add(Key(ParityDirection.ModernOnly, model.Model, line));
                }
            }

            foreach (string line in legacy)
            {
                if (!modern.Contains(line))
                {
                    differences.Add(Key(ParityDirection.LegacyOnly, model.Model, line));
                }
            }
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParityAllowlistEntry entry in allowlist)
        {
            string key = Key(entry.Direction, entry.Model, entry.Line);
            string where =
                "Parity allowlist line "
                + entry.LineNumber.ToString(CultureInfo.InvariantCulture)
                + " ("
                + (entry.Kind == ParityEntryKind.Permanent ? "permanent " : "temporary ")
                + entry.Tracking
                + ", '"
                + entry.Describe()
                + "')";

            if (!listed.Add(key))
            {
                errors.Add(where + " is a duplicate.");
                continue;
            }

            if (!modernLines.ContainsKey(entry.Model))
            {
                errors.Add(
                    where
                        + " names a model the gate did not examine. Remove it, or add the model to the parity corpus."
                );
                continue;
            }

            if (differences.Contains(key))
            {
                continue;
            }

            bool presentOnLegacy =
                entry.Direction == ParityDirection.ModernOnly
                && legacyLines[entry.Model] is { } legacySet
                && (
                    entry.Line == ParityAllowlistEntry.WholeModel || legacySet.Contains(entry.Line)
                );
            if (entry.Kind == ParityEntryKind.Permanent && presentOnLegacy)
            {
                errors.Add(
                    where
                        + " is a permanent modern-only member, but the legacy backend now emits it. "
                        + "A permanent entry is a decision that the member is absent from legacy, not present-and-throwing."
                );
            }
            else
            {
                errors.Add(
                    where
                        + " no longer corresponds to a difference between the backends. The allowlist "
                        + "can only shrink: remove the entry."
                );
            }
        }

        var unlisted = new List<string>();
        foreach (string difference in differences)
        {
            if (!listed.Contains(difference))
            {
                unlisted.Add(difference);
            }
        }

        unlisted.Sort(StringComparer.Ordinal);
        foreach (string difference in unlisted)
        {
            errors.Add(
                "Unlisted backend difference: '"
                    + difference
                    + "'. Bring the backends to parity, or add the line to the allowlist under a section naming its reason and tracking issue."
            );
        }

        return new ParityGateResult(errors, models.Count, linesExamined, differences.Count);
    }

    private static string Key(ParityDirection direction, string model, string line) =>
        (direction == ParityDirection.ModernOnly ? "modern-only " : "legacy-only ")
        + model
        + ": "
        + line;
}
