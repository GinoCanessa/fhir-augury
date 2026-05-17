namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing.Hydration;

/// <summary>
/// Subset of <c>ItemResponse</c> shape returned by Source.Jira / Source.GitHub.
/// Only the fields the hydrator reads are typed; the rest stays loose.
/// </summary>
internal sealed record OrchestratorItemResponse
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

internal sealed record OrchestratorZulipThreadResponse
{
    public int? StreamId { get; init; }
    public string? Stream { get; init; }
    public string? Topic { get; init; }
    public string? Url { get; init; }
    public int? MessageCount { get; init; }
    public DateTimeOffset? FirstMessageAt { get; init; }
    public DateTimeOffset? LastMessageAt { get; init; }
    public string? FirstMessageExcerpt { get; init; }
}

internal sealed record OrchestratorGitHubRepoResponse
{
    public string? FullName { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Url { get; init; }
}
