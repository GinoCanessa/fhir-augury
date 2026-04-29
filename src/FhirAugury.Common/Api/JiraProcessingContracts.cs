namespace FhirAugury.Common.Api;

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

/// <summary>Shared filter shape for the local-processing list and random endpoints.</summary>
public record JiraLocalProcessingFilter
{
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? Projects { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? Specifications { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? Types { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? Priorities { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? Statuses { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? ChangeCategories { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? ChangeImpacts { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? RelatedArtifacts { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? WorkGroups { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? Reporters { get; init; }
    /// <summary>Source filter list using the null-as-default, empty-as-explicit-all convention.</summary>
    public List<string>? Labels { get; init; }

    /// <summary>Optional filter on the local-processing flag.</summary>
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
