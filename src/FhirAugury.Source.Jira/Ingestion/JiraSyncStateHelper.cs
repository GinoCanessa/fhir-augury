namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Helpers for encoding/decoding project-scoped sync state keys.
/// SubSource format: "{project}:{runType}" (e.g., "FHIR:full", "FHIR-I:incremental").
/// </summary>
public static class JiraSyncStateHelper
{
    /// <summary>Builds a SubSource key from project and run type.</summary>
    public static string SyncKey(string project, string runType)
        => $"{project}:{runType}";

    /// <summary>
    /// Parses a SubSource key into project and run type.
    /// Falls back to ("FHIR", subSource) for legacy rows without a colon.
    /// </summary>
    public static (string Project, string RunType) ParseSyncKey(string subSource)
    {
        int colon = subSource.IndexOf(':');
        return colon >= 0
            ? (subSource[..colon], subSource[(colon + 1)..])
            : ("FHIR", subSource);
    }
}
