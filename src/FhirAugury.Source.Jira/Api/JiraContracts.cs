namespace FhirAugury.Source.Jira.Api;

/// <summary>
/// HTTP request body for the flexible query endpoint (POST /api/v1/query).
/// Mirrors the fields that were in the protobuf JiraQueryRequest.
/// </summary>
public record JiraQueryRequest
{
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Statuses { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Resolutions { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? WorkGroups { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Specifications { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Projects { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? ExcludeProjects { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Types { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Priorities { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Labels { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Assignees { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? Reporters { get; init; }
    /// <summary>
    /// Source filter list using the null-as-default, empty-as-explicit-all convention; Jira query filters have no per-field default, so null and [] both add no SQL predicate. See docs/source-filter-conventions.md.
    /// </summary>
    public List<string>? InPersonRequesters { get; init; }
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

/// <summary>Issue numbers response.</summary>
public record IssueNumbersResponse(List<int> IssueNumbers);

/// <summary>Sync status for a single Jira project.</summary>
public record JiraProjectStatus(
    string Project,
    DateTimeOffset? LastSyncAt,
    int ItemsIngested,
    string Status);

/// <summary>Update payload for <c>PUT /api/v1/projects/{key}</c>.</summary>
public record JiraProjectUpdateRequest(bool Enabled, int? BaselineValue = null);

/// <summary>
/// Per-work-group projection returned by <c>GET /api/v1/work-groups</c>.
/// Joins the Jira-side index (<c>jira_index_workgroups</c>) with the
/// canonical HL7 catalog (<c>hl7_workgroups</c>). Catalog-side fields
/// (<see cref="WorkGroupCode"/>, <see cref="WorkGroupDefinition"/>,
/// <see cref="WorkGroupRetired"/>) remain nullable when the HL7 join
/// is empty; <see cref="WorkGroupNameClean"/> is always populated —
/// the server falls back to
/// <see cref="FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(string?)"/>
/// over <see cref="Name"/> when the catalog row is absent.
/// </summary>
public record JiraWorkGroupSummaryEntry
{
    public required string Name { get; init; }
    public required int IssueCount { get; init; }
    public int IssueCountSubmitted { get; init; }
    public int IssueCountTriaged { get; init; }
    public int IssueCountWaitingForInput { get; init; }
    public int IssueCountNoChange { get; init; }
    public int IssueCountChangeRequired { get; init; }
    public int IssueCountPublished { get; init; }
    public int IssueCountApplied { get; init; }
    public int IssueCountDuplicate { get; init; }
    public int IssueCountClosed { get; init; }
    public int IssueCountBalloted { get; init; }
    public int IssueCountWithdrawn { get; init; }
    public int IssueCountDeferred { get; init; }
    public int IssueCountOther { get; init; }
    public string? WorkGroupCode { get; init; }
    public string? WorkGroupDefinition { get; init; }
    public required string WorkGroupNameClean { get; init; }
    public bool? WorkGroupRetired { get; init; }
}

/// <summary>
/// Envelope returned by <c>GET /api/v1/work-groups</c>. The
/// <see cref="CatalogJoinDegraded"/> flag signals that the HL7
/// catalog join was empty or partial (at least one row has a null
/// <see cref="JiraWorkGroupSummaryEntry.WorkGroupCode"/>). Downstream
/// orchestrators surface this as a "proceed with warning" banner
/// rather than aborting (see D2/D4 in the source feature request).
/// </summary>
public record JiraWorkGroupListResponse(
    bool CatalogJoinDegraded,
    List<JiraWorkGroupSummaryEntry> Items);
