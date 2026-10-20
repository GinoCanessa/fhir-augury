using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Filtering;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public sealed record JiraTicketDiscoveryBatch(
    IReadOnlyList<JiraIssueSummaryEntry> Tickets,
    SourceReadProvenance? Provenance = null);

public sealed record JiraTicketDiscoveryItem(
    JiraIssueSummaryEntry Ticket,
    SourceReadProvenance? Provenance = null);

public interface IJiraTicketDiscoveryClient
{
    Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(
        ResolvedJiraProcessingFilters filters,
        CancellationToken ct);

    async Task<JiraTicketDiscoveryBatch> ListTicketsWithProvenanceAsync(
        ResolvedJiraProcessingFilters filters,
        CancellationToken ct)
        => new(await ListTicketsAsync(filters, ct), null);

    Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsForModeAsync(
        ResolvedJiraProcessingFilters filters,
        bool runBacked,
        CancellationToken ct)
        => ListTicketsAsync(filters, ct);

    async Task<JiraTicketDiscoveryBatch> ListTicketsForModeWithProvenanceAsync(
        ResolvedJiraProcessingFilters filters,
        bool runBacked,
        CancellationToken ct)
        => new(await ListTicketsForModeAsync(filters, runBacked, ct), null);

    Task<JiraIssueSummaryEntry?> GetTicketAsync(
        string key,
        string sourceTicketShape,
        CancellationToken ct);

    async Task<JiraTicketDiscoveryItem?> GetTicketWithProvenanceAsync(
        string key,
        string sourceTicketShape,
        CancellationToken ct)
    {
        JiraIssueSummaryEntry? ticket =
            await GetTicketAsync(key, sourceTicketShape, ct);
        return ticket is null
            ? null
            : new JiraTicketDiscoveryItem(ticket, null);
    }

    Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct);
}
