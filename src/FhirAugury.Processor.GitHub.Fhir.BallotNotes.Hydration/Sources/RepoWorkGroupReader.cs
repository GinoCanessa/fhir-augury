using System.Text.RegularExpressions;
using FhirAugury.Parsing.Fhir;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>
/// Reads an artifact's or page's own declared owning work group directly from the
/// local repository clone. Pages carry a <c>[%wg &lt;code&gt;%]</c> "Responsible
/// Owner" marker in their HTML; artifacts carry a <c>structuredefinition-wg</c>
/// extension on their StructureDefinition. Best-effort: a missing file or absent
/// marker yields <c>null</c>.
/// </summary>
public static partial class RepoWorkGroupReader
{
    // Matches the page "Responsible Owner" marker, e.g.
    //   <td id="wg"><a href="[%wg fhir%]">[%wgt fhir%]</a> Work Group</td>
    // capturing the canonical work group code.
    [GeneratedRegex(@"\[%wg\s+([A-Za-z0-9_-]+)\s*%\]", RegexOptions.IgnoreCase)]
    private static partial Regex PageMarkerPattern();

    /// <summary>
    /// Returns the canonical work group code declared by the page
    /// <c>source/&lt;stem&gt;.html</c> via its <c>[%wg &lt;code&gt;%]</c> marker, or
    /// <c>null</c> when the file or marker is absent.
    /// </summary>
    public static string? ReadPageMarker(string clonePath, string stem)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || string.IsNullOrWhiteSpace(stem)) return null;

        string path = Path.Combine(clonePath, "source", $"{stem}.html");
        if (!File.Exists(path)) return null;

        try
        {
            string html = File.ReadAllText(path);
            Match match = PageMarkerPattern().Match(html);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the canonical work group code declared by the unit's
    /// StructureDefinition (its <c>structuredefinition-wg</c> extension), reading
    /// the first SD among <paramref name="resolvedFiles"/> that carries one, or
    /// <c>null</c> when none do.
    /// </summary>
    public static string? ReadArtifactWg(
        string clonePath,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || resolvedFiles is null) return null;

        foreach (StructureDefinitionInfo sd in ParseStructureDefinitions(clonePath, resolvedFiles, logger))
        {
            if (!string.IsNullOrWhiteSpace(sd.WorkGroup)) return sd.WorkGroup;
        }
        return null;
    }

    /// <summary>
    /// Returns the base resource name of the unit's StructureDefinition, derived
    /// from the last path segment of its <c>baseDefinition</c> canonical (e.g.
    /// <c>.../StructureDefinition/Patient</c> → <c>Patient</c>), or <c>null</c>
    /// when there is no base or it cannot be read.
    /// </summary>
    public static string? ReadBaseResourceName(
        string clonePath,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || resolvedFiles is null) return null;

        foreach (StructureDefinitionInfo sd in ParseStructureDefinitions(clonePath, resolvedFiles, logger))
        {
            if (string.IsNullOrWhiteSpace(sd.BaseDefinition)) continue;
            string baseName = sd.BaseDefinition.TrimEnd('/');
            int slash = baseName.LastIndexOf('/');
            if (slash >= 0 && slash < baseName.Length - 1) baseName = baseName[(slash + 1)..];
            if (!string.IsNullOrWhiteSpace(baseName)) return baseName;
        }
        return null;
    }

    private static IEnumerable<StructureDefinitionInfo> ParseStructureDefinitions(
        string clonePath, IReadOnlyList<ResolvedSourceFile> resolvedFiles, ILogger? logger)
    {
        foreach (ResolvedSourceFile file in resolvedFiles)
        {
            if (!IsStructureDefinitionFile(file.Path)) continue;

            string absolute = Path.Combine(clonePath, file.Path);
            if (!File.Exists(absolute)) continue;

            StructureDefinitionInfo? sd = FhirContentParser.TryParseStructureDefinition(absolute, logger);
            if (sd is not null) yield return sd;
        }
    }

    private static bool IsStructureDefinitionFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".xml" && ext != ".json") return false;
        return Path.GetFileName(path).ToLowerInvariant().Contains("structuredefinition");
    }
}
