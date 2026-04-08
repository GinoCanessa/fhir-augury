namespace FhirAugury.Parsing.Fsh;

/// <summary>
/// Parsed sushi-config.yaml metadata needed for canonical URL construction
/// and IG project identification.
/// </summary>
public record SushiConfig(
    string? Id,
    string? Canonical,
    string? Name,
    string? Title,
    string? FhirVersion,
    string? Status,
    List<string> PathResource,
    List<string> AdditionalResource);
