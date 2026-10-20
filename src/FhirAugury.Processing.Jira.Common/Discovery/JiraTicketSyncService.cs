using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public sealed class JiraTicketSyncService(
    IJiraTicketDiscoveryClient discoveryClient,
    JiraProcessingSourceTicketStore store,
    AuthoringRunStore authoringStore,
    JiraAuthoringRunCoordinator runCoordinator,
    JiraProcessingFilterResolver filterResolver,
    IOptions<JiraProcessingOptions> optionsAccessor,
    ILogger<JiraTicketSyncService> logger)
{
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        ResolvedJiraProcessingFilters filters = filterResolver.Resolve(optionsAccessor.Value);
        string processorKind = runCoordinator.ProcessorKind;
        string mode = (await authoringStore.EnsureProcessorModeAsync(processorKind, ct: ct)).Mode;
        if (string.Equals(mode, AuthoringStatusValues.ProcessorModes.CuttingOver, StringComparison.Ordinal))
        {
            logger.LogInformation("Skipped Jira sync while authoring cutover is in progress");
            return 0;
        }

        bool runBacked = string.Equals(
            mode,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            StringComparison.Ordinal);
        JiraTicketDiscoveryBatch discovery =
            await discoveryClient.ListTicketsForModeWithProvenanceAsync(
                filters,
                runBacked,
                ct);
        int upserted = 0;
        foreach (JiraIssueSummaryEntry ticket in discovery.Tickets)
        {
            await store.UpsertAsync(
                ticket,
                filters.SourceTicketShape,
                false,
                discovery.Provenance,
                ct);
            upserted++;
        }

        logger.LogInformation("Synced {TicketCount} Jira processing source tickets", upserted);
        if (runBacked)
        {
            await runCoordinator.CreateScheduledRunAsync(ct: ct);
        }
        return upserted;
    }
}
