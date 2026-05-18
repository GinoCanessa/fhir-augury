using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Hosting;

/// <summary>
/// Runs a full hydration sweep (Specification backfill + per-ticket
/// hydrate-all-unresolved) at preparer-service startup, before any
/// downstream hosted service begins to drain the queue. If
/// <see cref="HydrationOptions.BackfillOnStartup"/> is false, the
/// sweep is skipped — the per-ticket hydration path inside
/// <c>FhirTicketPrepHandler</c> still functions as before.
/// </summary>
/// <remarks>
/// Registering this service ahead of <c>AddJiraProcessing</c> in
/// <c>Program.cs</c> guarantees that <see cref="IHostedService.StartAsync"/>
/// runs before <c>ProcessingHostedService</c> /
/// <c>JiraTicketSyncWorker</c> (the host invokes hosted services in
/// registration order).
/// </remarks>
public sealed class HydrationSweeperHostedService(
    PreparedHydrationSweeper sweeper,
    IOptions<PreparerServiceOptions> options,
    ILogger<HydrationSweeperHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        HydrationOptions hydration = options.Value.Hydration;
        if (!hydration.BackfillOnStartup)
        {
            logger.LogInformation("Hydration startup sweep disabled by configuration; skipping.");
            return;
        }

        logger.LogInformation("Hydration startup sweep beginning.");
        await sweeper.RunFullAsync(HydrationSweepReason.Startup, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
