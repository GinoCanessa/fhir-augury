namespace FhirAugury.Source.Jira.Api;

/// <summary>
/// HTTP request body for the flexible query endpoint (POST /api/v1/query).
/// Mirrors the fields that were in the protobuf JiraQueryRequest.
/// </summary>
public record JiraQueryRequest
{
    public List<string> Statuses { get; init; } = [];
    public List<string> Resolutions { get; init; } = [];
    public List<string> WorkGroups { get; init; } = [];
    public List<string> Specifications { get; init; } = [];
    public List<string> Projects { get; init; } = [];
    public List<string> ExcludeProjects { get; init; } = [];
    public List<string> Types { get; init; } = [];
    public List<string> Priorities { get; init; } = [];
    public List<string> Labels { get; init; } = [];
    public List<string> Assignees { get; init; } = [];
    public List<string> Reporters { get; init; } = [];
    public List<string> InPersonRequesters { get; init; } = [];
    public DateTimeOffset? CreatedAfter { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }
    public DateTimeOffset? UpdatedAfter { get; init; }
    public DateTimeOffset? UpdatedBefore { get; init; }
    public string? Query { get; init; }
    public string? SortBy { get; init; }
    public string? SortOrder { get; init; }
    public int Limit { get; init; }
    public int Offset { get; init; }
}

/// <summary>Jira issue link between two keys.</summary>
public record JiraIssueLinkEntry(string SourceKey, string TargetKey, string LinkType);

/// <summary>Jira comment detail.</summary>
public record JiraCommentEntry(string Id, string IssueKey, string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>Jira issue summary for list/query endpoints.</summary>
public record JiraIssueSummaryEntry
{
    public required string Key { get; init; }
    public string ProjectKey { get; init; } = "";
    public required string Title { get; init; }
    public string Type { get; init; } = "";
    public string Status { get; init; } = "";
    public string Priority { get; init; } = "";
    public string WorkGroup { get; init; } = "";
    public string Specification { get; init; } = "";
    public string? Url { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Spec artifact entry for the spec-artifacts endpoint.</summary>
public record SpecArtifactEntry(
    string Family,
    string SpecKey,
    string SpecName,
    string? GitUrl,
    string? PublishedUrl,
    string? DefaultWorkgroup);

/// <summary>Issue numbers response.</summary>
public record IssueNumbersResponse(List<int> IssueNumbers);

/// <summary>Sync status for a single Jira project.</summary>
public record JiraProjectStatus(
    string Project,
    DateTimeOffset? LastSyncAt,
    int ItemsIngested,
    string Status);

// ── Local-processing endpoints (api/v1/local-processing) ──────────

/// <summary>Shared filter shape for the local-processing list and random endpoints.</summary>
public record JiraLocalProcessingFilter
{
    public List<string> Projects { get; init; } = [];
    public List<string> Specifications { get; init; } = [];
    public List<string> Types { get; init; } = [];
    public List<string> Priorities { get; init; } = [];
    public List<string> Statuses { get; init; } = [];
    public List<string> ChangeCategories { get; init; } = [];
    public List<string> ChangeImpacts { get; init; } = [];
    public List<string> RelatedArtifacts { get; init; } = [];
    public List<string> WorkGroups { get; init; } = [];
    public List<string> Reporters { get; init; } = [];
    public List<string> Labels { get; init; } = [];

    /// <summary>
    /// Optional filter on the local-processing flag.
    /// null/omitted -> no filter.
    /// true  -> ProcessedLocallyAt IS NOT NULL.
    /// false -> ProcessedLocallyAt IS NULL.
    /// </summary>
    public bool? ProcessedLocally { get; init; }
}

/// <summary>List-tickets request: filter + paging.</summary>
public record JiraLocalProcessingListRequest : JiraLocalProcessingFilter
{
    public int? Limit { get; init; }
    public int? Offset { get; init; }
}

/// <summary>List-tickets response: paged results plus unpaged total.</summary>
public record JiraLocalProcessingListResponse(
    IReadOnlyList<JiraIssueSummaryEntry> Results,
    int Limit,
    int Offset,
    int Total);

/// <summary>Set-processed request for a single Jira issue key.</summary>
public record JiraLocalProcessingSetRequest
{
    public required string Key { get; init; }
    public bool? ProcessedLocally { get; init; }
}

/// <summary>Set-processed response.</summary>
public record JiraLocalProcessingSetResponse(
    string Key,
    bool PreviousValue,
    bool NewValue);

/// <summary>Clear-all-processed response.</summary>
public record JiraLocalProcessingClearResponse(int RowsAffected);
