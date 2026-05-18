namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

/// <summary>
/// Per-ticket clustering input projection consumed by the
/// <c>topic-groupings</c> skill. Combines the analytic summary fields
/// from <c>prepared_tickets</c> with the partition / display fields
/// from <c>prepared_jira_hydration</c> self-rows, plus the
/// <c>prepared_ticket_related_jira</c> link edges the skill uses to
/// build linked / related subgraphs. Tickets that exist in hydration
/// but have no <c>prepared_tickets</c> row are still emitted (with
/// empty summary fields and no links) so the clustering skill knows
/// they exist; tickets with no hydration row are excluded because they
/// cannot be partitioned by <c>(Specification, Type)</c>.
/// </summary>
public sealed record PreparedTicketClusteringSignal(
    string TicketKey,
    string? Title,
    string? Status,
    string? Specification,
    string? Type,
    string RequestSummary,
    string CommentSummary,
    string LinkedTicketSummary,
    string RelatedTicketSummary,
    string RelatedZulipSummary,
    string RelatedGitHubSummary,
    bool HasPreparedTicket,
    IReadOnlyList<PreparedTicketClusteringLink> Links);

/// <summary>
/// One row of <c>prepared_ticket_related_jira</c> for a ticket in the
/// workgroup, normalized to the <c>"linked"</c> / <c>"related"</c>
/// vocabulary the clustering skill uses. <c>LinkType</c> values other
/// than the two canonical buckets are passed through verbatim so the
/// skill can decide how to treat them.
/// </summary>
public sealed record PreparedTicketClusteringLink(
    string AssociatedTicketKey,
    string LinkType,
    string Justification);

/// <summary>
/// Workgroup-scoped envelope returned by
/// <see cref="PreparerDatabase.GetClusteringSignalsAsync"/> and the
/// matching <c>prepared-ticket-clustering-signals</c> read endpoint.
/// <c>Tickets</c> is sorted by <c>TicketKey</c> ascending. The skill
/// drops hydration-only tickets (<c>HasPreparedTicket = false</c>)
/// before building any payload PUT back to the preparer, because the
/// grouping validator requires every referenced key to exist in
/// <c>prepared_tickets</c>.
/// </summary>
public sealed record PreparedTicketClusteringSignals(
    string WorkGroupClean,
    string? WorkGroupDisplay,
    IReadOnlyList<PreparedTicketClusteringSignal> Tickets);
