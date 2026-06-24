using System.Text.RegularExpressions;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>
/// Reads an artifact's or page's own declared owning work group directly from the
/// local repository clone. Pages carry a <c>[%wg &lt;code&gt;%]</c> "Responsible
/// Owner" marker in their HTML; artifacts carry a <c>structuredefinition-wg</c>
/// extension (added in a later phase). Best-effort: a missing file or absent
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
}
