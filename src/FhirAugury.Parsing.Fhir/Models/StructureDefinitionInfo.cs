namespace FhirAugury.Parsing.Fhir;

/// <summary>
/// Metadata extracted from a StructureDefinition (XML or JSON).
/// Maps to the fields described in proposals 02 (SD Source Indexing) and 03 (Element Indexing).
/// </summary>
public record StructureDefinitionInfo(
    string Url,
    string Name,
    string? Title,
    string? Status,
    string Kind,
    bool? IsAbstract,
    string? FhirType,
    string? BaseDefinition,
    string? Derivation,
    string? FhirVersion,
    string? Description,
    string? Publisher,
    string? WorkGroup,
    int? FhirMaturity,
    string? StandardsStatus,
    string? Category,
    List<ExtensionContext>? Contexts,
    List<ElementInfo> DifferentialElements);
