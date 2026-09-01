namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

/// <summary>
/// Classifies window-changed files into hydration units, reproducing the
/// FhirCore (<c>HL7/fhir</c>) bucketing the <c>orchestrate-notes</c> skill does
/// in shell: a single <c>datatypes</c> unit for anything under
/// <c>source/datatypes/</c>, <c>source/datatypes.html</c>, or a resolved
/// datatype own-page; one <c>Page</c> unit per top-level <c>source/&lt;stem&gt;.html</c>
/// that is not owned by a datatype; and one <c>Artifact</c> unit per
/// <c>source/&lt;name&gt;/</c> folder. Datatype own-pages are routed into the
/// datatypes unit, never double-dispatched as pages.
/// </summary>
public static class BallotNotesUnitGrouper
{
    /// <summary>
    /// Groups <paramref name="changedPaths"/> into units. When
    /// <paramref name="isFhirCore"/> is <c>false</c> the datatypes unit is never
    /// produced. <paramref name="datatypeOwnedPages"/> is the per-window set of
    /// datatype own-pages (clone-root-relative <c>source/&lt;stem&gt;.html</c>).
    /// </summary>
    public static IReadOnlyList<HydrationUnit> Group(
        IReadOnlyList<string> changedPaths,
        bool isFhirCore,
        IReadOnlySet<string> datatypeOwnedPages)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(datatypeOwnedPages);

        List<string> datatypeFiles = [];
        Dictionary<string, List<string>> artifacts = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> pages = new(StringComparer.Ordinal);

        foreach (string raw in changedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path = raw.Replace('\\', '/').Trim();

            if (isFhirCore && IsDatatypeFile(path, datatypeOwnedPages))
            {
                datatypeFiles.Add(path);
                continue;
            }

            if (TryGetTopLevelPage(path, out string pageName))
            {
                Add(pages, pageName, path);
                continue;
            }

            if (TryGetArtifact(path, out string artifactName))
            {
                Add(artifacts, artifactName, path);
                continue;
            }

            // Out-of-scope path (not under source/) — ignored for grouping.
        }

        List<HydrationUnit> units = [];

        if (isFhirCore && datatypeFiles.Count > 0)
        {
            units.Add(new HydrationUnit
            {
                Type = "DataType",
                Name = "datatypes",
                ChangedPaths = datatypeFiles,
            });
        }

        foreach ((string name, List<string> files) in artifacts.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            units.Add(new HydrationUnit { Type = "Artifact", Name = name, ChangedPaths = files });
        }

        foreach ((string name, List<string> files) in pages.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            units.Add(new HydrationUnit { Type = "Page", Name = name, ChangedPaths = files });
        }

        return units;
    }

    private static bool IsDatatypeFile(string path, IReadOnlySet<string> datatypeOwnedPages)
        => path.StartsWith("source/datatypes/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "source/datatypes.html", StringComparison.OrdinalIgnoreCase)
            || datatypeOwnedPages.Contains(path);

    /// <summary>
    /// Matches a top-level narrative page <c>source/&lt;stem&gt;.html</c> (no nested
    /// folder). Resource-intro files inside <c>source/&lt;resource&gt;/</c> are not
    /// pages (they belong to the artifact).
    /// </summary>
    private static bool TryGetTopLevelPage(string path, out string pageName)
    {
        pageName = string.Empty;
        if (!path.StartsWith("source/", StringComparison.OrdinalIgnoreCase)) return false;
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return false;

        string remainder = path["source/".Length..];
        if (remainder.Contains('/')) return false; // nested → not a top-level page

        pageName = remainder[..^".html".Length];
        return pageName.Length > 0;
    }

    /// <summary>
    /// Matches an artifact file <c>source/&lt;name&gt;/...</c>; the artifact name is
    /// the first path segment under <c>source/</c>.
    /// </summary>
    private static bool TryGetArtifact(string path, out string artifactName)
    {
        artifactName = string.Empty;
        if (!path.StartsWith("source/", StringComparison.OrdinalIgnoreCase)) return false;

        string remainder = path["source/".Length..];
        int slash = remainder.IndexOf('/');
        if (slash <= 0) return false; // no nested folder → handled as a page or ignored

        artifactName = remainder[..slash];
        return artifactName.Length > 0;
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string path)
    {
        if (!map.TryGetValue(key, out List<string>? list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(path);
    }
}
