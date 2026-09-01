using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
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
    ILogger<HydrationSweeperHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        HydrationOptions hydration = options.Value.Hydration;
        if (!hydration.BackfillOnStartup)
        {
            logger.LogInformation("Planner hydration startup sweep disabled by configuration; skipping.");
            return;
        }

        logger.LogInformation("Planner hydration startup sweep beginning.");
        await sweeper.RunFullAsync(HydrationSweepReason.Startup, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
