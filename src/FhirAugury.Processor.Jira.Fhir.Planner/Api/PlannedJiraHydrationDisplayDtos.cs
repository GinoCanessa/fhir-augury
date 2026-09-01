namespace FhirAugury.Processor.Jira.Fhir.Planner.Api;

public sealed record PlannedJiraHydrationDisplayDto(
    string IssueKey,
    string JiraKey,
    string? Title,
    string? Status,
    string? Type,
    string? Priority,
    string? Resolution,
    string? ResolutionDescriptionPlain,
    string? WorkGroup,
    string? WorkGroupClean,
    string? Specification,
    DateTimeOffset? UpdatedAt,
    string? Url,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

public sealed record PlannedJiraHydrationDisplayResponse(
    string WorkGroupClean,
    IReadOnlyList<PlannedJiraHydrationDisplayDto> Results);
