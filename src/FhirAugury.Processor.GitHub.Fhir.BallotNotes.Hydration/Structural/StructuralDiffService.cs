using System.Globalization;
using FhirAugury.Parsing.Fhir;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Structural;

/// <summary>One structural delta to a StructureDefinition element over the window.</summary>
public sealed record StructuralChange(string SourcePath, string ElementPath, string ChangeKind, string Detail);

/// <summary>
/// Detects structural StructureDefinition changes over a since-commit window by
/// diffing parsed element differentials at <c>{SinceSha}</c> vs <c>{HeadSha}</c>.
/// The file set is driven from <c>git diff --name-status</c> so added, deleted,
/// modified, and renamed SD files are all covered (a missing side is treated as
/// empty). A delta is structural when an element is added/removed, or its
/// cardinality, types, is-modifier, is-summary, or must-support changes; pure
/// narrative/binding-text edits are excluded. This includes extension-stored
/// StructureDefinitions, which parse the same way (#10).
/// </summary>
public static class StructuralDiffService
{
    /// <summary>
    /// Returns the structural deltas for every StructureDefinition file changed in
    /// the window. Best-effort: unparseable sides contribute no elements.
    /// </summary>
    public static async Task<IReadOnlyList<StructuralChange>> DiffAsync(
        string clonePath,
        string sinceSha,
        string headSha,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        GitRunner.GitResult diff = await GitRunner.TryRunAsync(
            clonePath,
            ["diff", "--name-status", "-M", $"{sinceSha}..{headSha}"],
            ct).ConfigureAwait(false);
        if (diff.ExitCode != 0) return [];

        // Materialize the StructureDefinition entries in diff order and gather every
        // blob spec they need, so both sides of every SD file are read in a single
        // `git cat-file --batch` instead of a `git show` spawn per side.
        List<(string? OldPath, string? NewPath)> entries = [];
        List<string> specs = [];
        foreach ((string? oldPath, string? newPath) in ParseNameStatus(diff.StdOut))
        {
            string probe = newPath ?? oldPath ?? string.Empty;
            if (!IsStructureDefinitionCandidate(probe)) continue;

            entries.Add((oldPath, newPath));
            if (oldPath is not null) specs.Add($"{sinceSha}:{oldPath}");
            if (newPath is not null) specs.Add($"{headSha}:{newPath}");
        }
        if (entries.Count == 0) return [];

        IReadOnlyDictionary<string, BlobResult> blobs = await GitBlobBatchReader
            .ReadAsync(clonePath, specs, ct).ConfigureAwait(false);

        // Parse each distinct (format, blob) at most once per run; DiffAsync is
        // called a single time per run and iterates sequentially, so a plain map
        // (no concurrency) is sufficient.
        Dictionary<string, IReadOnlyList<ElementInfo>> parseMemo = new(StringComparer.Ordinal);

        List<StructuralChange> changes = [];
        foreach ((string? oldPath, string? newPath) in entries)
        {
            IReadOnlyList<ElementInfo> sinceElements = ResolveElements(blobs, sinceSha, oldPath, parseMemo, logger);
            IReadOnlyList<ElementInfo> headElements = ResolveElements(blobs, headSha, newPath, parseMemo, logger);

            DiffElements(newPath ?? oldPath ?? string.Empty, sinceElements, headElements, changes);
        }
        return changes;
    }

    private static void DiffElements(
        string sourcePath,
        IReadOnlyList<ElementInfo> since,
        IReadOnlyList<ElementInfo> head,
        List<StructuralChange> changes)
    {
        Dictionary<string, ElementInfo> sinceMap = ToMap(since);
        Dictionary<string, ElementInfo> headMap = ToMap(head);

        foreach (ElementInfo h in head)
        {
            string key = KeyOf(h);
            if (!sinceMap.TryGetValue(key, out ElementInfo? s))
            {
                changes.Add(new StructuralChange(sourcePath, h.Path, "Added", "element added"));
                continue;
            }

            if (s.MinCardinality != h.MinCardinality || !string.Equals(s.MaxCardinality, h.MaxCardinality, StringComparison.Ordinal))
            {
                changes.Add(new StructuralChange(sourcePath, h.Path, "Cardinality",
                    $"cardinality {Card(s)}→{Card(h)}"));
            }

            string sinceTypes = TypeCodes(s);
            string headTypes = TypeCodes(h);
            if (!string.Equals(sinceTypes, headTypes, StringComparison.Ordinal))
            {
                changes.Add(new StructuralChange(sourcePath, h.Path, "Type",
                    $"type {Display(sinceTypes)}→{Display(headTypes)}"));
            }

            if (Bool(s.IsModifier) != Bool(h.IsModifier))
            {
                changes.Add(new StructuralChange(sourcePath, h.Path, "Modifier",
                    $"isModifier {Lower(s.IsModifier)}→{Lower(h.IsModifier)}"));
            }

            if (Bool(s.IsSummary) != Bool(h.IsSummary))
            {
                changes.Add(new StructuralChange(sourcePath, h.Path, "Summary",
                    $"isSummary {Lower(s.IsSummary)}→{Lower(h.IsSummary)}"));
            }

            if (Bool(s.MustSupport) != Bool(h.MustSupport))
            {
                changes.Add(new StructuralChange(sourcePath, h.Path, "MustSupport",
                    $"mustSupport {Lower(s.MustSupport)}→{Lower(h.MustSupport)}"));
            }
        }

        foreach (ElementInfo s in since)
        {
            if (!headMap.ContainsKey(KeyOf(s)))
            {
                changes.Add(new StructuralChange(sourcePath, s.Path, "Removed", "element removed"));
            }
        }
    }

    private static Dictionary<string, ElementInfo> ToMap(IReadOnlyList<ElementInfo> elements)
    {
        Dictionary<string, ElementInfo> map = new(StringComparer.Ordinal);
        foreach (ElementInfo e in elements) map[KeyOf(e)] = e;
        return map;
    }

    private static string KeyOf(ElementInfo e) => string.IsNullOrEmpty(e.ElementId) ? e.Path : e.ElementId;

    private static bool Bool(bool? value) => value ?? false;

    private static string Lower(bool? value) => Bool(value) ? "true" : "false";

    private static string Card(ElementInfo e)
    {
        string min = e.MinCardinality?.ToString(CultureInfo.InvariantCulture) ?? "?";
        string max = string.IsNullOrEmpty(e.MaxCardinality) ? "?" : e.MaxCardinality;
        return $"{min}..{max}";
    }

    private static string TypeCodes(ElementInfo e)
    {
        if (e.Types.Count == 0) return string.Empty;
        List<string> codes = [.. e.Types.Select(t => t.Code).Where(c => !string.IsNullOrEmpty(c))];
        codes.Sort(StringComparer.Ordinal);
        return string.Join("|", codes);
    }

    private static string Display(string joined) => joined.Length == 0 ? "(none)" : joined.Replace("|", ", ");

    private static IReadOnlyList<ElementInfo> ParseElements(string content, string? path, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        string format = DetectFormat(path, content);
        StructureDefinitionInfo? sd = FhirContentParser.TryParseStructureDefinition(content, format, logger);
        return sd?.DifferentialElements ?? [];
    }

    /// <summary>
    /// Resolves one diff side to its differential elements from the pre-read blob
    /// batch, parsing each distinct (format, blob) at most once via <paramref name="parseMemo"/>.
    /// A null path or an absent/empty blob yields no elements, mirroring the prior
    /// <c>git show</c> path where a failed read produced an empty string.
    /// </summary>
    private static IReadOnlyList<ElementInfo> ResolveElements(
        IReadOnlyDictionary<string, BlobResult> blobs,
        string sha,
        string? path,
        Dictionary<string, IReadOnlyList<ElementInfo>> parseMemo,
        ILogger? logger)
    {
        if (path is null) return [];
        if (!blobs.TryGetValue($"{sha}:{path}", out BlobResult blob) || !blob.Found) return [];

        string content = blob.Text;
        if (string.IsNullOrWhiteSpace(content)) return [];

        // A found object always reports its SHA; parse without memoizing if it does not.
        if (blob.BlobSha is null) return ParseElements(content, path, logger);

        string key = $"{DetectFormat(path, content)}\u0000{blob.BlobSha}";
        if (parseMemo.TryGetValue(key, out IReadOnlyList<ElementInfo>? cached)) return cached;

        IReadOnlyList<ElementInfo> elements = ParseElements(content, path, logger);
        parseMemo[key] = elements;
        return elements;
    }

    private static string DetectFormat(string? path, string content)
    {
        string ext = path is null ? string.Empty : Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".json") return "json";
        if (ext == ".xml") return "xml";
        // Fall back to sniffing the content when the path is absent/ambiguous.
        string trimmed = content.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "json" : "xml";
    }

    private static bool IsStructureDefinitionCandidate(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".xml" && ext != ".json") return false;
        string file = Path.GetFileName(path).ToLowerInvariant();
        return file.Contains("structuredefinition");
    }

    /// <summary>
    /// Parses <c>git diff --name-status -M</c> output into (oldPath, newPath)
    /// pairs. Added → (null, new); Deleted → (old, null); Modified → (path, path);
    /// Renamed/Copied → (old, new).
    /// </summary>
    internal static IEnumerable<(string? OldPath, string? NewPath)> ParseNameStatus(string output)
    {
        if (string.IsNullOrEmpty(output)) yield break;

        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            char status = char.ToUpperInvariant(parts[0][0]);
            switch (status)
            {
                case 'A':
                    yield return (null, parts[1]);
                    break;
                case 'D':
                    yield return (parts[1], null);
                    break;
                case 'R':
                case 'C':
                    if (parts.Length >= 3) yield return (parts[1], parts[2]);
                    break;
                default: // M, T, etc.
                    yield return (parts[1], parts[1]);
                    break;
            }
        }
    }
}
