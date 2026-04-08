namespace FhirAugury.Parsing.Fhir;

/// <summary>
/// Type reference from a StructureDefinition element, including profile and target profile URLs.
/// </summary>
public record ElementTypeInfo(
    string Code,
    List<string>? Profiles,
    List<string>? TargetProfiles);
