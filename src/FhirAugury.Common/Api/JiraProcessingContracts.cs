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

/// <summary>
/// Native SQL-LIKE criteria over raw nullable Jira label text, distinct from
/// the existing exact <see cref="JiraLocalProcessingFilter.Labels"/> filter.
/// Blank entries are ignored; other values are preserved and wrapped in
/// <c>%</c> for contains matching, retaining native wildcard and case behavior.
/// </summary>
public record JiraLabelTextFilter
{
    /// <summary>
    /// When active, require non-null stored label text matching at least one inclusion.
    /// Null, empty, or all-blank lists are inactive and add no restriction.
    /// </summary>
    public List<string>? Includes { get; init; }

    /// <summary>
    /// When active, allow null stored label text or non-null text matching no exclusion.
    /// Null, empty, or all-blank lists are inactive and add no restriction.
    /// </summary>
    public List<string>? Excludes { get; init; }
}

/// <summary>
/// Selection-tickets request: inherited exact filters and paging, plus
/// optional raw label-text criteria and an issue-key restriction.
/// </summary>
public record JiraLocalProcessingSelectionRequest : JiraLocalProcessingListRequest
{
    /// <summary>Label-text criteria ANDed with the inherited filters, without redefining exact Labels.</summary>
    public JiraLabelTextFilter? LabelText { get; init; }

    /// <summary>Optional exact issue keys. Null or empty adds no restriction.</summary>
    public List<string>? Keys { get; init; }
}

/// <summary>List-tickets response: paged results plus unpaged total.</summary>
public record JiraLocalProcessingListResponse(
    IReadOnlyList<JiraIssueSummaryEntry> Results,
    int Limit,
    int Offset,
    int Total)
{
    /// <summary>The source database generation represented by this page.</summary>
    public SourceReadProvenance? Provenance { get; init; }
}

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
