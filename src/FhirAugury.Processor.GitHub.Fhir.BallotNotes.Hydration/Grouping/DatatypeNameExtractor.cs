namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

/// <summary>
/// Derives the set of datatype names covered by the consolidated DataType unit
/// from its window-changed paths:
/// <list type="bullet">
///   <item><c>source/datatypes/&lt;name&gt;.xml</c> → <c>&lt;name&gt;</c> (variant files
///   carrying a <c>-</c>, code systems, value sets, and spreadsheets are skipped,
///   mirroring the grouper's datatype-file convention).</item>
///   <item>a datatype own-page <c>source/&lt;stem&gt;.html</c> → reverse
///   <see cref="DatatypePageMap.ResolveStem"/> (the <c>metadatatypes</c> cluster
///   and <c>references</c>→<c>Reference</c>).</item>
///   <item>an aggregate-only change (<c>source/datatypes.html</c> with no
///   per-datatype files) → the datatype names enumerated from HEAD, so the unit
///   never collapses to an empty owner set.</item>
/// </list>
/// </summary>
public static class DatatypeNameExtractor
{
    private const string DatatypesPrefix = "source/datatypes/";
    private const string AggregatePage = "source/datatypes.html";

    /// <summary>
    /// Extracts the distinct datatype names from <paramref name="changedPaths"/>.
    /// <paramref name="headDatatypeNames"/> is consulted only for the aggregate-only
    /// case and is evaluated lazily.
    /// </summary>
    public static IReadOnlyList<string> Extract(
        IReadOnlyList<string> changedPaths,
        Func<IReadOnlyList<string>> headDatatypeNames)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(headDatatypeNames);

        List<string> names = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        bool sawAggregatePage = false;

        foreach (string raw in changedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path = raw.Replace('\\', '/').Trim();

            if (string.Equals(path, AggregatePage, StringComparison.OrdinalIgnoreCase))
            {
                sawAggregatePage = true;
                continue;
            }

            if (path.StartsWith(DatatypesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddPerDatatypeFile(path, names, seen);
                continue;
            }

            if (TryGetOwnPageStem(path, out string stem))
            {
                foreach (string name in DatatypePageMap.ReverseStem(stem))
                {
                    AddDistinct(name, names, seen);
                }
            }
        }

        if (names.Count == 0 && sawAggregatePage)
        {
            foreach (string name in headDatatypeNames())
            {
                AddDistinct(name, names, seen);
            }
        }

        return names;
    }

    private static void AddPerDatatypeFile(string path, List<string> names, HashSet<string> seen)
    {
        string remainder = path[DatatypesPrefix.Length..];
        if (remainder.Contains('/')) return; // nested → not a top-level datatype file
        if (!remainder.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return;

        string stem = remainder[..^".xml".Length];
        if (stem.Length == 0 || stem.Contains('-')) return; // code systems / value sets / variants
        AddDistinct(stem, names, seen);
    }

    private static bool TryGetOwnPageStem(string path, out string stem)
    {
        stem = string.Empty;
        if (!path.StartsWith("source/", StringComparison.OrdinalIgnoreCase)) return false;
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return false;

        string remainder = path["source/".Length..];
        if (remainder.Contains('/')) return false;

        stem = remainder[..^".html".Length];
        return stem.Length > 0;
    }

    private static void AddDistinct(string name, List<string> names, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (seen.Add(name.Trim())) names.Add(name.Trim());
    }
}
