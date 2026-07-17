namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;

/// <summary>
/// One row of <c>planned_ticket_repo_changes</c> for a ticket in the
/// workgroup, projected onto the minimal repo / file-path shape the
/// <c>planner-topic-groupings</c> skill needs for tier-1 clustering
/// (same-repo + intersecting file paths).
/// </summary>
public sealed record PlannedTicketClusteringRepoChange(
    string RepoKey,
    string FilePath);

/// <summary>
/// One row of <c>planned_ticket_repo_impacts</c> for a ticket in the
/// workgroup. The clustering skill uses
/// <c>AffectedFilePath</c> as the tier-3 cross-repo signal: two
/// tickets that touch the same <c>AffectedFilePath</c> even across
/// different <c>RepoKey</c> values join the same Topic.
/// </summary>
public sealed record PlannedTicketClusteringRepoImpact(
    string RepoKey,
    string AffectedFilePath);

/// <summary>
/// Per-ticket clustering input projection consumed by the
/// <c>planner-topic-groupings</c> skill. Combines the analytic prose
/// fields from <c>planned_tickets</c> with the partition / display
/// fields drawn from either the <c>planned_jira_hydration</c> self-row
/// (when present) or the <c>jira_processing_source_tickets</c>
/// fallback (when the hydration self-row is missing), plus the repo
/// / file-path / affected-file-path inputs the four-tier clustering
/// hierarchy requires.
/// <para>
/// <c>HydrationStatus</c> is <c>null</c> when no
/// <c>planned_jira_hydration</c> self-row exists at all for the
/// ticket. The per-workgroup skill treats either a <c>null</c> value
/// or anything other than <c>"resolved"</c> as an abort signal for
/// the whole workgroup (Open Question 3).
/// </para>
/// <para>
/// <c>HasPlannedTicket</c> is <c>false</c> when the ticket exists in
/// <c>jira_processing_source_tickets</c> or
/// <c>planned_jira_hydration</c> but has no <c>planned_tickets</c>
/// row. The skill must drop those keys before building any PUT
/// payload — <c>tools/ticket-site/PlannerDbTrimmer</c> strips topic
/// members whose <c>TicketKey</c> is not in <c>planned_tickets</c>
/// and then drops the orphan topic rows, so leaving them in produces
/// silently-empty topics.
/// </para>
/// </summary>
public sealed record PlannedTicketClusteringSignal(
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
    IReadOnlyList<PlannedTicketClusteringRepoChange> RepoChanges,
    IReadOnlyList<PlannedTicketClusteringRepoImpact> RepoImpacts);

/// <summary>
/// Workgroup-scoped envelope returned by
/// <c>PlannerDatabase.GetClusteringSignalsAsync</c> and the matching
/// <c>planned-ticket-clustering-signals</c> read endpoint.
/// <c>Tickets</c> is sorted by <c>IssueKey</c> ascending.
/// <c>WorkGroupDisplay</c> follows the two-tier fallback used by the
/// other planner reads (topic row → most-recent self hydration row →
/// <c>null</c>).
/// </summary>
public sealed record PlannedTicketClusteringSignals(
    string WorkGroupClean,
    string? WorkGroupDisplay,
    IReadOnlyList<PlannedTicketClusteringSignal> Tickets);
