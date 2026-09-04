using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Processing;

public sealed class PlannedTicketRunPostProcessor(
    PlannerDatabase database,
    AuthoringRunStore authoringStore,
    SqliteReviewSnapshotReconciler snapshotReconciler,
    JiraAuthoringRunCoordinator coordinator,
    OrchestratorWorkGroupCatalogFetcher workGroupFetcher,
    IPlannedTicketGroupingDispatcher groupingDispatcher,
    IOptions<PlannerServiceOptions> optionsAccessor,
    ILogger<PlannedTicketRunPostProcessor> logger)
    : BackgroundService
{
    private readonly PlannerServiceOptions _options = optionsAccessor.Value;

    public async Task<AuthoringSnapshotDescriptor?> FinalizeRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        AuthoringRunRecord run = await authoringStore.GetRunAsync(runId, ct)
            ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
        if (!run.DatabaseOnly && await database.CountLegacyUnverifiedAsync(ct) > 0)
        {
            await authoringStore.SupersedeRunAsync(
                runId,
                "Canonical snapshot blocked until all legacy-unverified rows are revalidated.",
                ct: ct);
            throw new InvalidOperationException(
                "A complete legacy revalidation run is required before the first canonical snapshot.");
        }

        IReadOnlyList<PlannedTicketRunPartition> partitions =
            await database.GetRunPartitionsAsync(runId, ct);
        List<AuthoringFinalizationStage> stages =
        [
            new(
                "workgroup-catalog",
                "",
                AuthoringResultHasher.HashNormalizedUtf8("workgroup-catalog-v1"),
                async (lease, cancellationToken) =>
                {
                    IReadOnlyList<HydrationWorkGroupRow> workGroups =
                        await workGroupFetcher.FetchAsync(cancellationToken);
                    await database.SaveWorkGroupCatalogForRunAsync(
                        workGroups,
                        runId,
                        lease.StageId,
                        lease.LeaseId,
                        AuthoringResultHasher.HashNormalizedUtf8("workgroup-catalog-v1"),
                        cancellationToken);
                }),
        ];
        stages.AddRange(partitions.Select(partition => new AuthoringFinalizationStage(
            "grouping",
            partition.PartitionKey,
            partition.InputFingerprint,
            (lease, cancellationToken) => groupingDispatcher.ReplaceGroupingAsync(
                runId,
                partition,
                lease,
                cancellationToken))));

        AuthoringRunFinalizer finalizer = new(authoringStore);
        return await finalizer.FinalizeAsync(
            runId,
            stages,
            run.DatabaseOnly
                ? null
                : async cancellationToken =>
                {
                    IReadOnlyList<AuthoringRunItemRecord> items =
                        await authoringStore.GetRunItemsAsync(runId, cancellationToken);
                    IReadOnlyDictionary<string, long> counts =
                        await database.GetSnapshotTableCountsAsync(cancellationToken);
                    SqliteReviewSnapshotWriter writer = new(database.OpenConnection, authoringStore);
                    return await writer.WriteAsync(
                        new SqliteReviewSnapshotRequest(
                            coordinator.ProcessorKind,
                            runId,
                            Path.GetFullPath(_options.SnapshotDirectory),
                            _options.SnapshotSchemaVersion,
                            items.Count(item =>
                                item.Status is AuthoringStatusValues.Items.Complete or AuthoringStatusValues.Items.Superseded),
                            items.Count(item => item.AcceptedReceiptId is not null),
                            counts,
                            new PlannedTicketSnapshotSanitizer(runId)),
                        cancellationToken);
                },
            ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ReconcileSnapshotsOnStartup)
        {
            await snapshotReconciler.ReconcileAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string mode = (await authoringStore.EnsureProcessorModeAsync(
                    coordinator.ProcessorKind,
                    ct: stoppingToken)).Mode;
                if (mode == AuthoringStatusValues.ProcessorModes.RunBacked)
                {
                    IReadOnlyList<string> runs =
                        await database.ListRunsReadyForFinalizationAsync(
                            coordinator.ProcessorKind,
                            stoppingToken);
                    foreach (string runId in runs)
                    {
                        await FinalizeRunAsync(runId, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Planned-ticket run finalization pass failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

public interface IPlannedTicketGroupingDispatcher
{
    Task ReplaceGroupingAsync(
        string runId,
        PlannedTicketRunPartition partition,
        AuthoringRunStageLease lease,
        CancellationToken ct);
}

public sealed class UnconfiguredPlannedTicketGroupingDispatcher
    : IPlannedTicketGroupingDispatcher
{
    public Task ReplaceGroupingAsync(
        string runId,
        PlannedTicketRunPartition partition,
        AuthoringRunStageLease lease,
        CancellationToken ct)
        => throw new InvalidOperationException(
            $"No grouping dispatcher is configured for partition '{partition.PartitionKey}'.");
}
