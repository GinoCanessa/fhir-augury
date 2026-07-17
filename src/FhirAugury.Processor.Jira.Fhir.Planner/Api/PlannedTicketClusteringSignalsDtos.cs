using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Api;

/// <summary>
/// One <c>planned_ticket_repo_changes</c> projection (repo key +
/// file path) for a ticket in the workgroup. Mirrors
/// <see cref="PlannedTicketClusteringRepoChange"/> over the wire.
/// </summary>
public sealed record PlannedTicketClusteringRepoChangeDto(
    string RepoKey,
    string FilePath);

/// <summary>
/// One <c>planned_ticket_repo_impacts</c> projection (repo key +
/// affected file path) for a ticket in the workgroup. Mirrors
/// <see cref="PlannedTicketClusteringRepoImpact"/> over the wire.
/// </summary>
public sealed record PlannedTicketClusteringRepoImpactDto(
    string RepoKey,
    string AffectedFilePath);

/// <summary>
/// Per-ticket clustering signal sent over the wire. Mirrors
/// <see cref="PlannedTicketClusteringSignal"/> 1:1. The skill must
/// drop tickets with <c>HasPlannedTicket = false</c> before building
/// any PUT payload and must abort the whole workgroup when any
/// returned ticket has <c>HydrationStatus</c> either <c>null</c> or
/// not equal to <c>"resolved"</c>.
/// </summary>
public sealed record PlannedTicketClusteringSignalDto(
    string IssueKey,
    string? Title,
    string? Status,
    string? Specification,
    string? Type,
    string? HydrationStatus,
    bool HasPlannedTicket,
    string ResolutionSummary,
    string FeatureProposal,
    string DesignRationale,
    IReadOnlyList<string> Repos,
    IReadOnlyList<PlannedTicketClusteringRepoChangeDto> RepoChanges,
    IReadOnlyList<PlannedTicketClusteringRepoImpactDto> RepoImpacts);

/// <summary>
/// Workgroup-scoped envelope returned by the
/// <c>planned-ticket-clustering-signals</c> endpoint. Items are
/// ordered by <c>IssueKey</c> ascending. <c>WorkGroupDisplay</c>
/// follows the same two-tier fallback (topic row → most-recent self
/// hydration row → <c>null</c>) used by the other planner reads.
/// </summary>
public sealed record PlannedTicketClusteringSignalsDto(
    string WorkGroupClean,
    string? WorkGroupDisplay,
    IReadOnlyList<PlannedTicketClusteringSignalDto> Tickets);

public static class PlannedTicketClusteringSignalsDtoMapper
{
    public static PlannedTicketClusteringSignalsDto ToDto(PlannedTicketClusteringSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        return new PlannedTicketClusteringSignalsDto(
            signals.WorkGroupClean,
            signals.WorkGroupDisplay,
            signals.Tickets.Select(ToDto).ToArray());
    }

    private static PlannedTicketClusteringSignalDto ToDto(PlannedTicketClusteringSignal signal) => new(
        signal.IssueKey,
        signal.Title,
        signal.Status,
        signal.Specification,
        signal.Type,
        signal.HydrationStatus,
        signal.HasPlannedTicket,
        signal.ResolutionSummary,
        signal.FeatureProposal,
        signal.DesignRationale,
        signal.Repos.ToArray(),
        signal.RepoChanges.Select(ToDto).ToArray(),
        signal.RepoImpacts.Select(ToDto).ToArray());

    private static PlannedTicketClusteringRepoChangeDto ToDto(PlannedTicketClusteringRepoChange change) => new(
        change.RepoKey,
        change.FilePath);

    private static PlannedTicketClusteringRepoImpactDto ToDto(PlannedTicketClusteringRepoImpact impact) => new(
        impact.RepoKey,
        impact.AffectedFilePath);
}
