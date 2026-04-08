namespace FhirAugury.Parsing.Fhir;

/// <summary>
/// Context declaration for an extension StructureDefinition.
/// Specifies where the extension can be used.
/// </summary>
public record ExtensionContext(string Type, string Expression);
