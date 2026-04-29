using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public sealed class JiraTicketSyncService(
    IJiraTicketDiscoveryClient discoveryClient,
    JiraProcessingSourceTicketStore store,
    JiraProcessingFilterResolver filterResolver,
    IOptions<JiraProcessingOptions> optionsAccessor,
    ILogger<JiraTicketSyncService> logger)
{
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        ResolvedJiraProcessingFilters filters = filterResolver.Resolve(optionsAccessor.Value);
        IReadOnlyList<JiraIssueSummaryEntry> tickets = await discoveryClient.ListTicketsAsync(filters, ct);
        int upserted = 0;
        foreach (JiraIssueSummaryEntry ticket in tickets)
        {
            await store.UpsertAsync(ticket, filters.SourceTicketShape, false, ct);
            upserted++;
        }

        logger.LogInformation("Synced {TicketCount} Jira processing source tickets", upserted);
        return upserted;
    }
}
