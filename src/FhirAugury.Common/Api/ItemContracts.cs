namespace FhirAugury.Common.Api;

/// <summary>Full item detail from a source service.</summary>
public record ItemResponse
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
}

/// <summary>A comment on an item.</summary>
public record CommentInfo(
    string? Id,
    string Author,
    string Body,
    DateTimeOffset? CreatedAt,
    string? Url);

/// <summary>Lightweight item summary for list endpoints.</summary>
public record ItemSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>Paginated list of item summaries.</summary>
public record ItemListResponse(
    int Total,
    List<ItemSummary> Items);

/// <summary>Markdown snapshot of an item.</summary>
public record SnapshotResponse(
    string Id,
    string Source,
    string Markdown,
    string? Url,
    string? ContentType);

/// <summary>Raw content of an item in a specified format.</summary>
public record ContentResponse(
    string Id,
    string Source,
    string Content,
    string Format,
    string? Url,
    Dictionary<string, string>? Metadata,
    string? ContentType);
