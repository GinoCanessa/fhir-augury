using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Api;

/// <summary>
/// Per-ticket clustering-input projection. Mirrors
/// <see cref="PreparedTicketClusteringSignal"/> over the wire so the
/// <c>topic-groupings</c> skill can read summaries + links without
/// touching <c>fhir-augury-cli</c>. <c>HasPreparedTicket</c> is
/// <c>false</c> when the ticket exists in
/// <c>prepared_jira_hydration</c> but has no <c>prepared_tickets</c>
/// row yet — the skill must drop those keys before building any PUT
/// payload (the grouping validator rejects unknown ticket keys).
/// </summary>
public sealed record PreparedTicketClusteringSignalDto(
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
    IReadOnlyList<PreparedTicketClusteringLinkDto> Links);

/// <summary>
/// One <c>prepared_ticket_related_jira</c> edge for a ticket in the
/// workgroup. <c>LinkType</c> is passed through verbatim
/// (typically <c>"linked"</c> or <c>"related"</c>); the clustering
/// skill is responsible for interpreting non-canonical values.
/// </summary>
public sealed record PreparedTicketClusteringLinkDto(
    string AssociatedTicketKey,
    string LinkType,
    string Justification);

/// <summary>
/// Workgroup-scoped envelope returned by the
/// <c>prepared-ticket-clustering-signals</c> endpoint. Items are
/// ordered by <c>TicketKey</c> ascending. <c>WorkGroupDisplay</c>
/// follows the same resolution rules as the grouping / hydration
/// read endpoints (topic row → most-recent self hydration row →
/// <c>null</c>).
/// </summary>
public sealed record PreparedTicketClusteringSignalsDto(
    string WorkGroupClean,
    string? WorkGroupDisplay,
    IReadOnlyList<PreparedTicketClusteringSignalDto> Tickets);

public static class PreparedTicketClusteringSignalsDtoMapper
{
    public static PreparedTicketClusteringSignalsDto ToDto(PreparedTicketClusteringSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        return new PreparedTicketClusteringSignalsDto(
            signals.WorkGroupClean,
            signals.WorkGroupDisplay,
            signals.Tickets.Select(ToDto).ToArray());
    }

    private static PreparedTicketClusteringSignalDto ToDto(PreparedTicketClusteringSignal signal) => new(
        signal.TicketKey,
        signal.Title,
        signal.Status,
        signal.Specification,
        signal.Type,
        signal.RequestSummary,
        signal.CommentSummary,
        signal.LinkedTicketSummary,
        signal.RelatedTicketSummary,
        signal.RelatedZulipSummary,
        signal.RelatedGitHubSummary,
        signal.HasPreparedTicket,
        signal.Links.Select(ToDto).ToArray());

    private static PreparedTicketClusteringLinkDto ToDto(PreparedTicketClusteringLink link) => new(
        link.AssociatedTicketKey,
        link.LinkType,
        link.Justification);
}
