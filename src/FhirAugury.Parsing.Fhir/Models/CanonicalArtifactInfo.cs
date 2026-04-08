namespace FhirAugury.Parsing.Fhir;

/// <summary>
/// Metadata extracted from a canonical artifact (CodeSystem, ValueSet, ConceptMap,
/// SearchParameter, OperationDefinition, NamingSystem, CapabilityStatement).
/// Maps to the fields described in proposal 04 (Canonical Artifact Indexing).
/// </summary>
public record CanonicalArtifactInfo(
    string ResourceType,
    string Url,
    string Name,
    string? Title,
    string? Version,
    string? Status,
    string? Description,
    string? Publisher,
    string? WorkGroup,
    int? FhirMaturity,
    string? StandardsStatus,
    Dictionary<string, object?> TypeSpecificData);
