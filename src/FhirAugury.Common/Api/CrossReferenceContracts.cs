namespace FhirAugury.Common.Api;

/// <summary>A cross-reference from one source item to another.</summary>
public record SourceCrossReference(
    string SourceType,
    string SourceId,
    string TargetType,
    string TargetId,
    string LinkType,
    string? Context,
    string? SourceContentType,
    string? TargetTitle,
    string? TargetUrl);

/// <summary>Cross-reference response from a source service.</summary>
public record CrossReferenceResponse(
    string Source,
    string Id,
    string? Direction,
    List<SourceCrossReference> References);

/// <summary>A related item found via cross-source resolution.</summary>
public record RelatedItem
{
    public required string Source { get; init; }
    public string ContentType { get; init; } = "";
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Snippet { get; init; }
    public string? Url { get; init; }
    public double RelevanceScore { get; init; }
    public string? Relationship { get; init; }
    public string? Context { get; init; }
}

/// <summary>Response from a find-related endpoint.</summary>
public record FindRelatedResponse(
    string SeedSource,
    string SeedId,
    string? SeedTitle,
    List<RelatedItem> Items);
