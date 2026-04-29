using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Filtering;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public interface IJiraTicketDiscoveryClient
{
    Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct);
    Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct);
    Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct);
}
