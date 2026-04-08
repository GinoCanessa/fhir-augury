namespace FhirAugury.Parsing.Fsh;

/// <summary>
/// A canonical artifact definition extracted from a FSH file.
/// Maps to the same database tables as FHIR XML/JSON artifacts:
/// Profile/Extension/Resource/Logical → github_structure_definitions
/// CodeSystem/ValueSet/DefinitionalInstance → github_canonical_artifacts
/// </summary>
public record FshDefinitionInfo(
    FshDefinitionKind Kind,
    string Name,
    string? Id,
    string? Parent,
    string? Title,
    string? Description,
    string? InstanceOf,
    string? Usage,
    string? ExplicitUrl,
    string? ExplicitStatus,
    string? ExplicitVersion,
    int StartLine,
    int EndLine);
