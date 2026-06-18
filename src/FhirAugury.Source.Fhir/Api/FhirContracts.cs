namespace FhirAugury.Source.Fhir.Api;

/// <summary>A FHIR release package available in the spec database.</summary>
public record ReleaseInfo(
    int Key,
    string ShortName,
    string FhirVersion,
    string PackageId,
    string PackageVersion,
    string? Title);

/// <summary>
/// Envelope that echoes the resolved release alongside a release-scoped result,
/// so callers always see which release answered their request.
/// </summary>
public record FhirReleaseResponse<T>(ReleaseInfo Release, T Result);

/// <summary>High-level artifact counts for the spec database (used by /stats).</summary>
public record FhirSpecCounts(
    int Releases,
    int Structures,
    int CodeSystems,
    int ValueSets,
    int Operations,
    int SearchParameters);
