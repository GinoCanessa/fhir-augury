namespace FhirAugury.Common.Api;

/// <summary>A single search result item from any source.</summary>
public record SearchResult
{
    public required string Source { get; init; }
    public string ContentType { get; init; } = "";
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Snippet { get; init; }
    public required double Score { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>Response from a search endpoint (source-level or unified).</summary>
public record SearchResponse(
    string Query,
    int Total,
    List<SearchResult> Results,
    List<string>? Warnings);
