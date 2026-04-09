namespace FhirAugury.Parsing.Fsh;

/// <summary>
/// The kind of canonical artifact definition found in a FSH file.
/// </summary>
public enum FshDefinitionKind
{
    /// <summary>Profile: constrains an existing resource type.</summary>
    Profile,

    /// <summary>Extension: defines a FHIR extension.</summary>
    Extension,

    /// <summary>Resource: defines a new resource type (FSH 3.0+).</summary>
    Resource,

    /// <summary>Logical: defines a logical model.</summary>
    Logical,

    /// <summary>CodeSystem: defines a code system.</summary>
    CodeSystem,

    /// <summary>ValueSet: defines a value set.</summary>
    ValueSet,

    /// <summary>Instance with Usage: #definition that defines a canonical artifact
    /// (e.g., OperationDefinition, SearchParameter, ConceptMap).</summary>
    DefinitionalInstance,
}
