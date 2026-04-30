using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Processing;

/// <summary>
/// Background service that discovers planner-completed tickets and mirrors them into the
/// applier-side <c>applied_ticket_queue_items</c> table. Polls on the
/// <c>ProcessingServiceOptions.SyncSchedule</c> cadence (the same cadence that gates the
/// queue runner — discovery does not have its own MinSyncAge; that's reserved for repo
/// baseline refresh in Phase 4).
/// </summary>
public sealed class PlannerWorkQueue : BackgroundService
{
    private readonly PlannerReadOnlyDatabase _planner;
    private readonly AppliedTicketQueueItemStore _store;
    private readonly ProcessingLifecycleService _lifecycle;
    private readonly ILogger<PlannerWorkQueue> _logger;
    private readonly IOptions<ProcessingServiceOptions> _processingOptions;
    private readonly IOptions<JiraProcessingOptions> _jiraOptions;
    private readonly IOptions<ApplierOptions> _applierOptions;

    public PlannerWorkQueue(
        PlannerReadOnlyDatabase planner,
        AppliedTicketQueueItemStore store,
        ProcessingLifecycleService lifecycle,
        ILogger<PlannerWorkQueue> logger,
        IOptions<ProcessingServiceOptions> processingOptions,
        IOptions<JiraProcessingOptions> jiraOptions,
        IOptions<ApplierOptions> applierOptions)
    {
        _planner = planner;
        _store = store;
        _lifecycle = lifecycle;
        _logger = logger;
        _processingOptions = processingOptions;
        _jiraOptions = jiraOptions;
        _applierOptions = applierOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = ParsePositiveTimeSpan(_processingOptions.Value.SyncSchedule, TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_lifecycle.IsRunning)
                {
                    await PollOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PlannerWorkQueue poll failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Single-poll surface, exposed for testing.</summary>
    public async Task<PollSummary> PollOnceAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyCollection<string>? typeFilter = _jiraOptions.Value.TicketTypesToProcess;
        IReadOnlyList<PlannerCompletedTicketView> tickets = _planner.ListCompletedPlannedTickets(typeFilter);
        string sourceTicketShape = _jiraOptions.Value.SourceTicketShape;

        int inserted = 0, unchanged = 0, updated = 0, stale = 0, skipped = 0;
        foreach (PlannerCompletedTicketView ticket in tickets)
        {
            ct.ThrowIfCancellationRequested();
            AppliedTicketQueueItemUpsertResult result = await _store.UpsertFromPlannerAsync(
                ticket.Key,
                sourceTicketShape,
                ticket.CompletionId,
                ticket.CompletedAt,
                now,
                ct);
            switch (result)
            {
                case AppliedTicketQueueItemUpsertResult.Inserted: inserted++; break;
                case AppliedTicketQueueItemUpsertResult.Unchanged: unchanged++; break;
                case AppliedTicketQueueItemUpsertResult.UpdatedPlanReference: updated++; break;
                case AppliedTicketQueueItemUpsertResult.MarkedStale: stale++; break;
                case AppliedTicketQueueItemUpsertResult.SkippedInProgress: skipped++; break;
            }
        }

        if (inserted + updated + stale > 0)
        {
            _logger.LogInformation(
                "PlannerWorkQueue poll: inserted={Inserted} updated={Updated} stale={Stale} unchanged={Unchanged} skipped-in-progress={Skipped} (filter={Filter}, plannerDb={PlannerDb})",
                inserted, updated, stale, unchanged, skipped,
                typeFilter is null ? "(all types)" : string.Join(",", typeFilter),
                _applierOptions.Value.PlannerDatabasePath);
        }

        return new PollSummary(inserted, unchanged, updated, stale, skipped);
    }

    private static TimeSpan ParsePositiveTimeSpan(string value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, out TimeSpan parsed) && parsed > TimeSpan.Zero ? parsed : fallback;

    public readonly record struct PollSummary(int Inserted, int Unchanged, int UpdatedPlanReference, int MarkedStale, int SkippedInProgress);
}
