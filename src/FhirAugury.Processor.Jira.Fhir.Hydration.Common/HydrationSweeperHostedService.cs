using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// Runs a full hydration sweep (Specification backfill + per-ticket
/// hydrate-all-unresolved) at processor-service startup, before any
/// downstream hosted service begins to drain the queue. If
/// <see cref="HydrationOptions.BackfillOnStartup"/> is false, the
/// sweep is skipped — the per-ticket hydration path inside the
/// per-service handler still functions as before.
/// </summary>
/// <remarks>
/// Registering this service ahead of <c>AddJiraProcessing</c> in the
/// host's <c>Program.cs</c> guarantees that
/// <see cref="IHostedService.StartAsync"/> runs before the processing
/// queue worker (the host invokes hosted services in registration
/// order). This is the shared, options-driven hosted service intended
/// for any new processor service; existing services that already have
/// their own hosted service wrapper (e.g. the preparer) continue to
/// use that wrapper to preserve their service-options binding.
/// </remarks>
public sealed class HydrationSweeperHostedService(
    HydrationSweeper sweeper,
    IOptions<HydrationOptions> options,
    ILogger<HydrationSweeperHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.BackfillOnStartup)
        {
            logger.LogInformation("Hydration startup sweep disabled by configuration; skipping.");
            return;
        }

        logger.LogInformation("Hydration startup sweep beginning.");
        await sweeper.RunFullAsync(HydrationSweepReason.Startup, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
