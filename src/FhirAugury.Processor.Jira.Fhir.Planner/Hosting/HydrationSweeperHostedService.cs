using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Hosting;

/// <summary>
/// Runs a full planner-side hydration sweep at startup, ahead of the
/// processing queue worker. Mirrors the preparer hosted service's
/// shape (taking <see cref="IOptions{PlannerServiceOptions}"/> so the
/// existing service-options idiom carries over).
/// </summary>
public sealed class HydrationSweeperHostedService(
    PlannedHydrationSweeper sweeper,
    IOptions<PlannerServiceOptions> options,
    ILogger<HydrationSweeperHostedService> logger,
    PlannerDatabase? database = null) : IHostedService
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
            logger.LogInformation("Planner hydration startup sweep disabled by configuration; skipping.");
            return;
        }

        PlannerMaintenanceLease? lease = database is null
            ? null
            : await database.TryAcquireMaintenanceLeaseAsync("startup-hydration", cancellationToken);
        if (database is not null && lease is null)
        {
            logger.LogInformation("Planner hydration startup sweep deferred while an authoring run owns the mutation fence.");
            return;
        }

        logger.LogInformation("Planner hydration startup sweep beginning.");
        try
        {
            await sweeper.RunFullAsync(HydrationSweepReason.Startup, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (database is not null && lease is not null)
            {
                await database.ReleaseMaintenanceLeaseAsync(lease, CancellationToken.None);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
