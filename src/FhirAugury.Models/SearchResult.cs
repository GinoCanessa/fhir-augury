namespace FhirAugury.Models;

/// <summary>A unified search result from any source.</summary>
public record SearchResult
{
    /// <summary>The source type (e.g., "jira", "zulip").</summary>
    public required string Source { get; init; }

    /// <summary>The source-specific identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The item title.</summary>
    public required string Title { get; init; }

    /// <summary>A text snippet showing the match context.</summary>
    public string? Snippet { get; init; }

    /// <summary>Raw relevance score from the search engine.</summary>
    public required double Score { get; init; }

    /// <summary>Normalized score (0.0–1.0) for cross-source ranking.</summary>
    public double? NormalizedScore { get; init; }

    /// <summary>URL to the item in its source system.</summary>
    public string? Url { get; init; }

    /// <summary>When the item was last updated.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
