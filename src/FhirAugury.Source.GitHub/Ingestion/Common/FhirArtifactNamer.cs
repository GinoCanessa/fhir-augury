namespace FhirAugury.Source.GitHub.Ingestion.Common;

/// <summary>
/// Extracts canonical name and URL from a FHIR resource file.
/// Thin wrapper over <see cref="FhirResourceIdentifier"/> for consistent naming.
/// </summary>
public static class FhirArtifactNamer
{
    /// <summary>Naming result for a FHIR artifact file.</summary>
    public record ArtifactName(string ResourceType, string? CanonicalName, string? CanonicalUrl);

    /// <summary>
    /// Attempts to extract naming information from a FHIR artifact file.
    /// Returns null if the file is not a recognized FHIR artifact.
    /// </summary>
    public static ArtifactName? TryGetName(string filePath, string? content)
    {
        FhirResourceIdentifier.IdentificationResult? result = FhirResourceIdentifier.TryIdentify(filePath, content);

        if (result?.ResourceType is null)
            return null;

        return new ArtifactName(result.ResourceType, result.Name, result.Url);
    }
}
