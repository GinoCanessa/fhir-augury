using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
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
    ILogger<HydrationSweeperHostedService> logger,
    PreparerDatabase? database = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (database is not null)
        {
            await database.RecoverInterruptedMaintenanceLeasesAsync(cancellationToken);
        }

        HydrationOptions hydration = options.Value.Hydration;
        if (!hydration.BackfillOnStartup)
        {
            logger.LogInformation("Hydration startup sweep disabled by configuration; skipping.");
            return;
        }
        PreparerMaintenanceLease? lease = database is null
            ? null
            : await database.TryAcquireMaintenanceLeaseAsync(
                "startup-hydration",
                cancellationToken);
        if (database is not null && lease is null)
        {
            logger.LogInformation("Hydration startup sweep deferred while an authoring run owns the mutation fence.");
            return;
        }

        logger.LogInformation("Hydration startup sweep beginning.");
        try
        {
            await sweeper.RunFullAsync(
                HydrationSweepReason.Startup,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (database is not null && lease is not null)
            {
                await database.ReleaseMaintenanceLeaseAsync(
                    lease,
                    CancellationToken.None);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
