namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;

/// <summary>
/// One owning work group: a canonical HL7 work group <see cref="Code"/> (e.g.
/// <c>fhir</c>) together with its resolved human-readable
/// <see cref="DisplayName"/> (e.g. <c>FHIR Infrastructure (FHIR-I)</c>). A single
/// unit (artifact / page) carries exactly one; the consolidated datatypes
/// surface may carry several.
/// </summary>
public readonly record struct WorkGroupRef(string Code, string DisplayName)
{
    /// <summary>The sentinel owner used when no source in the chain resolves.</summary>
    public static WorkGroupRef Unknown => new(string.Empty, "(unknown)");

    /// <summary>
    /// Distinct (case-insensitive), order-preserving, <c>;</c>-joined display
    /// names. Empty / whitespace entries are dropped.
    /// </summary>
    public static string JoinNames(IEnumerable<WorkGroupRef> refs)
        => Join(refs.Select(static r => r.DisplayName));

    /// <summary>
    /// Distinct (case-insensitive), order-preserving, <c>;</c>-joined canonical
    /// codes. Empty / whitespace entries are dropped.
    /// </summary>
    public static string JoinCodes(IEnumerable<WorkGroupRef> refs)
        => Join(refs.Select(static r => r.Code));

    private static string Join(IEnumerable<string> values)
    {
        List<string> ordered = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (seen.Add(value)) ordered.Add(value);
        }
        return string.Join(";", ordered);
    }
}
