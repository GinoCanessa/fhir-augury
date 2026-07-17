using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Discovery;

/// <summary>
/// Hosted feeder that periodically refreshes the local processing work-item queue
/// by calling <see cref="JiraTicketSyncService.SyncAsync"/> on the cadence
/// configured by <see cref="ProcessingServiceOptions.SyncSchedule"/>. Gated on the
/// shared <see cref="ProcessingLifecycleService.IsRunning"/> flag, so
/// <c>POST /processing/stop</c> halts the feeder along with the queue runner and
/// <c>StartProcessingOnStartup = false</c> suppresses the startup pass.
/// </summary>
public sealed class JiraTicketSyncWorker(
    JiraTicketSyncService syncService,
    ProcessingLifecycleService lifecycle,
    IOptions<ProcessingServiceOptions> optionsAccessor,
    ILogger<JiraTicketSyncWorker> logger)
    : BackgroundService
{
    private readonly ProcessingServiceOptions _options = optionsAccessor.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TimeSpan.TryParse(_options.SyncSchedule, out TimeSpan interval) || interval <= TimeSpan.Zero)
        {
            logger.LogWarning("SyncSchedule '{Schedule}' is not a positive TimeSpan; Jira ticket sync feeder disabled.", _options.SyncSchedule);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (lifecycle.IsRunning)
            {
                try
                {
                    int upserted = await syncService.SyncAsync(stoppingToken).ConfigureAwait(false);
                    logger.LogDebug("Jira ticket sync pass upserted {TicketCount} tickets", upserted);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Jira ticket sync pass failed; will retry on next interval.");
                }
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
