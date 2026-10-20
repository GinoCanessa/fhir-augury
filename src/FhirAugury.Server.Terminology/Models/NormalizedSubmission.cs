namespace FhirAugury.Server.Terminology.Models;

/// <summary>
/// FHIR-version-agnostic projection of a submitted CodeSystem/ValueSet,
/// produced by <c>SubmissionNormalizer</c>. The matching pipeline
/// consumes this — never <c>Hl7.Fhir.*</c> types directly.
/// </summary>
public sealed class NormalizedSubmission
{
    /// <summary>"CodeSystem" or "ValueSet".</summary>
    public required string Kind { get; init; }

    /// <summary>"R4" or "R5".</summary>
    public required string FhirVersion { get; init; }

    public string? CanonicalUrl { get; init; }
    public string? CanonicalUrlNormalized { get; init; }
    public string? Version { get; init; }
    public string? Title { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Purpose { get; init; }

    /// <summary>Flattened concept list. For CodeSystem the entire tree;
    /// for ValueSet the union of <c>compose.include[*].concept</c>.</summary>
    public List<NormalizedConcept> Concepts { get; init; } = [];
}

/// <summary>One flattened concept from a submission.</summary>
public sealed record NormalizedConcept(
    string SystemUrl,
    string Code,
    string? Display,
    string? DisplayNormalized);
