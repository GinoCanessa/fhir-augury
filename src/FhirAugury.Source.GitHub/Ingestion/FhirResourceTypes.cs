using System.Collections.Frozen;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Known FHIR definitional/conformance resource types used for
/// filename-based tag extraction from XML files.
/// </summary>
public static class FhirResourceTypes
{
    /// <summary>
    /// Resource types that commonly appear as filename prefixes in FHIR repositories
    /// (e.g., valueset-example.xml, structuredefinition-patient.xml).
    /// Clinical resource types are excluded to avoid false positives.
    /// </summary>
    public static readonly FrozenSet<string> FilenamePrefixTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StructureDefinition", "ValueSet", "CodeSystem", "ConceptMap",
            "SearchParameter", "OperationDefinition", "CapabilityStatement",
            "ImplementationGuide", "NamingSystem", "CompartmentDefinition",
            "StructureMap", "GraphDefinition", "ExampleScenario",
            "TestScript", "TestPlan", "Bundle", "Questionnaire",
            "Library", "Measure", "PlanDefinition", "ActivityDefinition",
            "ActorDefinition", "Requirements", "SubscriptionTopic",
            "ObservationDefinition", "SpecimenDefinition",
            "MessageDefinition", "EventDefinition"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to extract a FHIR resource type from a filename.
    /// Returns the canonical (ProperCase) type name if the prefix before
    /// the first dash matches a known resource type, otherwise null.
    /// </summary>
    public static string? TryGetFromFilename(string filename)
    {
        int dashIndex = filename.IndexOf('-');
        if (dashIndex <= 0) return null;

        string prefix = filename[..dashIndex];
        return FilenamePrefixTypes.TryGetValue(prefix, out string? canonical)
            ? canonical : null;
    }
}
