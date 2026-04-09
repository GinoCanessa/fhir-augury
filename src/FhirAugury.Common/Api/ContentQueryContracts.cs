namespace FhirAugury.Common.Api;

/// <summary>Input for a directional cross-reference query.</summary>
public record CrossReferenceQuery
{
    /// <summary>The value to look up (e.g., a Jira key, GitHub issue ref, FHIR element path).</summary>
    public required string Value { get; init; }

    /// <summary>Optional source-type hint to narrow which xref tables are searched.</summary>
    public string? SourceType { get; init; }
}

/// <summary>A single cross-reference hit returned by a content query.</summary>
public record CrossReferenceHit
{
    /// <summary>Source system that owns the item containing the reference.</summary>
    public required string SourceType { get; init; }

    /// <summary>Content type within the source (e.g., "issue", "message", "page").</summary>
    public string ContentType { get; init; } = "";

    /// <summary>Item ID within the source that contains (or is the target of) the reference.</summary>
    public required string SourceId { get; init; }

    /// <summary>Title of the source item.</summary>
    public string? SourceTitle { get; init; }

    /// <summary>URL of the source item.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Target system of the cross-reference.</summary>
    public required string TargetType { get; init; }

    /// <summary>Target item ID.</summary>
    public required string TargetId { get; init; }

    /// <summary>Title of the target item (if known).</summary>
    public string? TargetTitle { get; init; }

    /// <summary>URL of the target item (if known).</summary>
    public string? TargetUrl { get; init; }

    /// <summary>Kind of link (e.g., "mentions", "linked_issue").</summary>
    public string LinkType { get; init; } = "mentions";

    /// <summary>Optional context snippet showing the reference in text.</summary>
    public string? Context { get; init; }

    /// <summary>Relevance score (higher = more relevant). Default 1.0.</summary>
    public double Score { get; init; } = 1.0;

    /// <summary>When the source or target item was last updated.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Sort order for query results.</summary>
public enum ResultSortOrder
{
    /// <summary>Sort by computed score (relevance × freshness). Default.</summary>
    Score,

    /// <summary>Sort by UpdatedAt descending (newest first).</summary>
    Date,
}

/// <summary>Response for refers-to, referred-by, and cross-referenced queries.</summary>
public record CrossReferenceQueryResponse
{
    /// <summary>The value that was queried.</summary>
    public required string Value { get; init; }

    /// <summary>Source-type filter applied (null if unfiltered).</summary>
    public string? SourceType { get; init; }

    /// <summary>Direction of the query: refers-to, referred-by, or cross-referenced.</summary>
    public required string Direction { get; init; }

    /// <summary>Total number of hits found.</summary>
    public int Total { get; init; }

    /// <summary>The cross-reference hits.</summary>
    public required List<CrossReferenceHit> Hits { get; init; }

    /// <summary>Warnings from partial failures.</summary>
    public List<string>? Warnings { get; init; }
}

/// <summary>Multi-value content search request.</summary>
public record ContentSearchRequest
{
    /// <summary>One or more search values/terms.</summary>
    public required List<string> Values { get; init; }

    /// <summary>Optional source filter (e.g., ["jira", "zulip"]).</summary>
    public List<string>? Sources { get; init; }

    /// <summary>Maximum results to return.</summary>
    public int? Limit { get; init; }
}

/// <summary>A single search hit from a content search.</summary>
public record ContentSearchHit
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

    /// <summary>Which search value produced this hit.</summary>
    public string? MatchedValue { get; init; }
}

/// <summary>Response from a content search endpoint.</summary>
public record ContentSearchResponse
{
    /// <summary>The values that were searched.</summary>
    public required List<string> Values { get; init; }

    /// <summary>Total number of hits.</summary>
    public int Total { get; init; }

    /// <summary>The search hits.</summary>
    public required List<ContentSearchHit> Hits { get; init; }

    /// <summary>Warnings from partial failures.</summary>
    public List<string>? Warnings { get; init; }
}

/// <summary>Full item detail returned by the content item endpoint.</summary>
public record ContentItemResponse
{
    public required string Source { get; init; }
    public string ContentType { get; init; } = "";
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Content { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public List<CommentInfo>? Comments { get; init; }

    /// <summary>Markdown snapshot of the item.</summary>
    public string? Snapshot { get; init; }
}
