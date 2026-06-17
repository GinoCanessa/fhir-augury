namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

/// <summary>
/// The datatype → narrative own-page map, ported from the <c>orchestrate-notes</c>
/// / <c>notes-datatype</c> skills. Most datatypes render into the consolidated
/// <c>source/datatypes.html</c> page; a subset ship their own
/// <c>source/&lt;stem&gt;.html</c>. Keep these overrides identical to the
/// <c>notes-datatype</c> skill's "Datatype-page map" section.
/// </summary>
public static class DatatypePageMap
{
    /// <summary>
    /// Datatypes whose own-page is the consolidated <c>metadatatypes.html</c>.
    /// </summary>
    private static readonly HashSet<string> s_metaDataTypesCluster = new(StringComparer.OrdinalIgnoreCase)
    {
        "ContactDetail",
        "DataRequirement",
        "Expression",
        "ParameterDefinition",
        "RelatedArtifact",
        "TriggerDefinition",
        "UsageContext",
        "Contributor",
    };

    /// <summary>
    /// Resolves a datatype name to its candidate own-page stem. Default is the
    /// lowercase datatype name; <c>Reference</c> → <c>references</c> and the
    /// MetaDataTypes cluster → <c>metadatatypes</c>.
    /// </summary>
    public static string ResolveStem(string datatypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datatypeName);
        string name = datatypeName.Trim();

        if (string.Equals(name, "Reference", StringComparison.OrdinalIgnoreCase))
        {
            return "references";
        }

        if (s_metaDataTypesCluster.Contains(name))
        {
            return "metadatatypes";
        }

        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Computes the set of <c>source/&lt;stem&gt;.html</c> own-pages for the given
    /// datatype names, keeping only those that exist at HEAD per the supplied
    /// <paramref name="headFileExists"/> predicate (clone-root-relative path).
    /// </summary>
    public static IReadOnlySet<string> ComputeOwnedPages(
        IEnumerable<string> datatypeNames,
        Func<string, bool> headFileExists)
    {
        ArgumentNullException.ThrowIfNull(datatypeNames);
        ArgumentNullException.ThrowIfNull(headFileExists);

        HashSet<string> owned = new(StringComparer.OrdinalIgnoreCase);
        foreach (string datatype in datatypeNames)
        {
            if (string.IsNullOrWhiteSpace(datatype))
            {
                continue;
            }

            string page = $"source/{ResolveStem(datatype)}.html";
            if (headFileExists(page))
            {
                owned.Add(page);
            }
        }

        return owned;
    }
}
