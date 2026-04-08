namespace FhirAugury.Parsing.Fhir;

/// <summary>
/// Classifies a StructureDefinition into a high-level artifact class based on
/// its kind, derivation, and FHIR type.
/// </summary>
public static class ArtifactClassifier
{
    public static string Classify(string kind, string? derivation, string? fhirType)
    {
        return kind switch
        {
            "primitivetype" or "primitive-type" => "PrimitiveType",
            "logical" => "LogicalModel",
            "complextype" or "complex-type" when derivation == "constraint" && fhirType == "Extension" => "Extension",
            "complextype" or "complex-type" => "ComplexType",
            "resource" when derivation == "constraint" => "Profile",
            "resource" => "Resource",
            _ => "Unknown",
        };
    }
}
