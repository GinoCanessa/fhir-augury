namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common.Internal;

/// <summary>
/// Subset of <c>ItemResponse</c> shape returned by Source.Jira / Source.GitHub.
/// Only the fields the hydrator reads are typed; the rest stays loose.
/// </summary>
internal sealed record OrchestratorItemResponse
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Content { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public OrchestratorSourceReadProvenance? Provenance { get; init; }
    public OrchestratorItemPeopleResponse? People { get; init; }
}

internal sealed record OrchestratorSourceReadProvenance
{
    public string? Source { get; init; }
    public long? ContentRevision { get; init; }
    public bool? IsStable { get; init; }
    public Dictionary<string, DateTimeOffset?>? ProjectLastSuccessfulRefreshAt { get; init; }
}

internal sealed record OrchestratorItemPeopleResponse
{
    public string? Reporter { get; init; }
    public string? Assignee { get; init; }
    public IReadOnlyList<string>? InPersonRequesters { get; init; }
    public int? PublicDisplayNamePolicyVersion { get; init; }
}

internal sealed record OrchestratorGitHubRepoResponse
{
    public string? FullName { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Url { get; init; }
}
