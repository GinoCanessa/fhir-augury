using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Api;

/// <summary>
/// Per-ticket display projection over a <c>prepared_jira_hydration</c>
/// self-row. Mirrors the columns the hydration sweeper already writes —
/// no derived fields. Consumed by the <c>index-prepared-db</c> skill.
/// </summary>
public sealed record PreparedJiraHydrationDisplayDto(
    string TicketKey,
    string JiraKey,
    string? Title,
    string? Status,
    string? Type,
    string? Specification,
    string? WorkGroup,
    string? Url,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset HydratedAt,
    string HydrationStatus,
    string? HydrationReason);

/// <summary>
/// Workgroup-scoped envelope for the per-ticket hydration display
/// projection. <c>WorkGroupDisplay</c> mirrors the heading the
/// <c>prepared-ticket-groupings</c> endpoint uses; <c>Items</c> is
/// empty (200 OK) when no rows match the requested workgroup.
/// </summary>
public sealed record PreparedJiraHydrationListResponse(
    string WorkGroupClean,
    string? WorkGroupDisplay,
    IReadOnlyList<PreparedJiraHydrationDisplayDto> Items);

public static class PreparedJiraHydrationDisplayDtoMapper
{
    public static PreparedJiraHydrationDisplayDto ToDto(PreparedJiraHydrationRow row) => new(
        row.TicketKey,
        row.JiraKey,
        row.Title,
        row.Status,
        row.Type,
        row.Specification,
        row.WorkGroup,
        row.Url,
        row.UpdatedAt,
        row.HydratedAt,
        row.HydrationStatus,
        row.HydrationReason);
}
