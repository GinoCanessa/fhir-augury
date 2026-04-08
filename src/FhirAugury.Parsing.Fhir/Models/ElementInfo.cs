namespace FhirAugury.Parsing.Fhir;

/// <summary>
/// Element-level data extracted from a StructureDefinition's differential.
/// Maps to the fields described in proposal 03 (Element-Level Indexing).
/// </summary>
public record ElementInfo(
    string ElementId,
    string Path,
    string Name,
    string? Short,
    string? Definition,
    string? Comment,
    int? MinCardinality,
    string? MaxCardinality,
    List<ElementTypeInfo> Types,
    string? BindingStrength,
    string? BindingValueSet,
    string? SliceName,
    bool? IsModifier,
    bool? IsSummary,
    string? FixedValue,
    string? PatternValue,
    int FieldOrder);
