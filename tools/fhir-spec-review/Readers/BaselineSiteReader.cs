using System.Collections.Frozen;
using FhirAugury.Tools.FhirSpecReview.SpecReview;

namespace FhirAugury.Tools.FhirSpecReview.Readers;

/// <summary>Presence sets harvested from a published baseline site's top level.</summary>
/// <param name="SanitizedEntities">Sanitized names of top-level entries (folders + file stems).</param>
/// <param name="PageFileNames">Top-level HTML page file names (original casing).</param>
internal sealed record BaselinePresence(
    FrozenSet<string> SanitizedEntities,
    FrozenSet<string> PageFileNames);

/// <summary>
/// Enumerates a published baseline site (e.g. a rendered <c>fhir-r5</c> site)
/// into presence sets used to detect pages/artifacts that existed in the
/// published release but were removed from the current build. Advisory only.
/// </summary>
internal sealed class BaselineSiteReader
{
    private readonly string _sitePath;

    public BaselineSiteReader(string sitePath)
    {
        _sitePath = sitePath;
    }

    public bool Exists => Directory.Exists(_sitePath);

    public BaselinePresence Load()
    {
        HashSet<string> sanitized = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> pageNames = new(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_sitePath))
        {
            return new BaselinePresence(FrozenSet<string>.Empty, FrozenSet<string>.Empty);
        }

        foreach (string dir in Directory.EnumerateDirectories(_sitePath))
        {
            AddSanitized(sanitized, Path.GetFileName(dir));
        }

        foreach (string file in Directory.EnumerateFiles(_sitePath))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                pageNames.Add(name);
            }
            AddSanitized(sanitized, Path.GetFileNameWithoutExtension(name));
        }

        return new BaselinePresence(
            sanitized.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            pageNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    private static void AddSanitized(HashSet<string> set, string raw)
    {
        SanitizedKeyword key = KeywordSanitizer.Sanitize(raw);
        if (key.Clean.Length > 0) set.Add(key.Clean);
    }
}
