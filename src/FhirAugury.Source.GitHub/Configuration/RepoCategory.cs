namespace FhirAugury.Source.GitHub.Configuration;

/// <summary>
/// Categories of FHIR repositories, each with tailored discovery, tagging,
/// and indexing logic.
/// </summary>
public enum RepoCategory
{
    FhirCore,
    Utg,
    FhirExtensionsPack,
    Incubator,
    Ig,
}
