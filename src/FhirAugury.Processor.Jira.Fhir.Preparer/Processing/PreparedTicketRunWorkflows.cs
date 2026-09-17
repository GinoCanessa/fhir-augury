using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketRunWorkflowRegistry(
    AuthoringRunStore authoringStore,
    PreparerDatabase database,
    OrchestratorHydrationFetcher hydrationFetcher,
    PreparedTicketPublicationReconciliationPlanner reconciliationPlanner,
    PreparedTicketGroupingDeltaDispatcher groupingDeltaDispatcher,
    PreparedTicketSnapshotMaterializer snapshotMaterializer,
    PreparedTicketPublicationRecoveryService recoveryService,
    IOptions<PreparerServiceOptions> optionsAccessor,
    ILogger<PreparedTicketRunWorkflowRegistry> logger,
    PreparedTicketReconciliationGroupingStageAdapter?
        reconciliationGroupingStageAdapter = null)
{
    private readonly PreparerServiceOptions _options = optionsAccessor.Value;
    private readonly PreparedTicketReconciliationGroupingStageAdapter?
        _reconciliationGroupingStageAdapter =
            reconciliationGroupingStageAdapter;

    public bool HandlesFinalization(AuthoringRunRecord run)
        => string.Equals(
            run.Purpose,
            PreparedTicketPublicationReconciliationContract.Purpose,
            StringComparison.Ordinal);

    public async Task<AuthoringSnapshotDescriptor>
        FinalizeReconciliationAsync(
            AuthoringRunRecord run,
            CancellationToken ct = default)
    {
        if (!HandlesFinalization(run))
        {
            throw new InvalidOperationException(
                $"Run '{run.Id}' is not a publication reconciliation.");
        }
        if (run.Status == AuthoringStatusValues.Runs.Completed)
        {
            return run.SnapshotId is null
                ? throw new InvalidOperationException(
                    $"Completed reconciliation '{run.Id}' has no snapshot.")
                : await authoringStore.GetSnapshotDescriptorAsync(
                    run.SnapshotId,
                    ct)
                  ?? throw new InvalidOperationException(
                      $"Completed reconciliation '{run.Id}' has no ready snapshot.");
        }
        if (await database.GetPendingPublicationReconciliationAsync(
                run.Id,
                ct) is not null)
        {
            return await recoveryService.RecoverAsync(run.Id, ct);
        }

        PreparedTicketPublicationReconciliationStatusResult status =
            await reconciliationPlanner.GetStatusAsync(run.Id, ct);
        if (status.InvalidatedTicketKeys.Count != 0)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .RevisionInvalidation,
                $"Frozen Jira revisions changed: {string.Join(", ", status.InvalidatedTicketKeys)}",
                status.InvalidatedTicketKeys);
        }
        if (!await authoringStore.AllItemsCompleteAsync(run.Id, ct))
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .StagingMismatch,
                "All revised reconciliation items must be complete before promotion.");
        }
        PreparedTicketPublicationGroupingDelta delta =
            await groupingDeltaDispatcher.PrepareAsync(run.Id, ct);

        await authoringStore.MarkRunFinalizingAsync(run.Id, ct: ct);
        await ExecuteGroupingStagesAsync(run.Id, delta, ct);
        delta = await groupingDeltaDispatcher.PrepareAsync(run.Id, ct);
        if (delta.Impacts.Any(impact => !impact.Complete))
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .GroupingImpactMismatch,
                "The complete grouping-impact closure must be staged before promotion.");
        }

        PreparedTicketPublicationReconciliationProof proof =
            await groupingDeltaDispatcher.CreateProofAsync(
                run.Id,
                ct: ct);
        PreparedTicketPublicationCandidateSnapshot candidate =
            await snapshotMaterializer.MaterializeReconciliationCandidateAsync(
                run,
                proof,
                ct: ct);
        string safeProcessor = string.Concat(
            run.ProcessorKind.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-'));
        string finalPath = Path.Combine(
            Path.GetFullPath(_options.SnapshotDirectory),
            $"{safeProcessor}-{run.Id}.db");
        _ = await database.PromotePublicationReconciliationAsync(
            run.Id,
            finalPath,
            ct);
        logger.LogInformation(
            "Canonical reconciliation {RunId} promoted; publishing verified snapshot {Sha256}",
            run.Id,
            candidate.Sha256);
        return await recoveryService.RecoverAsync(run.Id, ct);
    }

    private async Task ExecuteGroupingStagesAsync(
        string runId,
        PreparedTicketPublicationGroupingDelta delta,
        CancellationToken ct)
    {
        foreach (PreparedTicketPublicationReconciliationGroupingImpact impact
                 in delta.Impacts)
        {
            AuthoringRunStageRecord stage =
                await authoringStore.EnsureRunStageAsync(
                    runId,
                    PreparerDatabase
                        .PublicationReconciliationGroupingStageName,
                    impact.PartitionKey,
                    delta.OverlayCorpusFingerprint,
                    ct: ct);
            if (string.Equals(
                    stage.Status,
                    AuthoringStatusValues.Stages.Complete,
                    StringComparison.Ordinal))
            {
                _ = await groupingDeltaDispatcher.RequireStageReceiptAsync(
                    runId,
                    stage.Id,
                    stageLeaseId: null,
                    partitionKey: impact.PartitionKey,
                    inputFingerprint: delta.OverlayCorpusFingerprint,
                    ct: ct);
                continue;
            }

            AuthoringRunStageLease? lease =
                await authoringStore.TryStartRunStageAsync(
                    stage.Id,
                    ct: ct);
            if (lease is null)
            {
                AuthoringRunStageRecord current =
                    (await authoringStore.GetRunStagesAsync(runId, ct))
                    .Single(value => value.Id == stage.Id);
                if (string.Equals(
                        current.Status,
                        AuthoringStatusValues.Stages.Complete,
                        StringComparison.Ordinal))
                {
                    _ = await groupingDeltaDispatcher
                        .RequireStageReceiptAsync(
                            runId,
                            stage.Id,
                            stageLeaseId: null,
                            partitionKey: impact.PartitionKey,
                            inputFingerprint:
                                delta.OverlayCorpusFingerprint,
                            ct: ct);
                    continue;
                }
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageAlreadyInProgress,
                    $"Reconciliation grouping stage for partition '{impact.PartitionKey}' is already in progress.");
            }

            try
            {
                _ = await groupingDeltaDispatcher.DispatchStageAsync(
                    runId,
                    impact.PartitionKey,
                    delta.OverlayCorpusFingerprint,
                    lease,
                    (workItem, stageLease, cancellationToken) =>
                        _reconciliationGroupingStageAdapter is null
                            ? throw new InvalidOperationException(
                                $"No reconciliation grouping worker is configured for partition '{workItem.PartitionKey}'.")
                            : _reconciliationGroupingStageAdapter
                                .ReplaceGroupingAsync(
                                    workItem,
                                    stageLease,
                                    cancellationToken),
                    ct);
                await authoringStore.CompleteRunStageAsync(
                    stage.Id,
                    lease.LeaseId,
                    ct: ct);
            }
            catch (AuthoringConflictException ex)
                when (ex.Code == AuthoringConflictCode.StageLeaseLost)
            {
                throw;
            }
            catch (Exception ex)
            {
                await authoringStore.FailRunStageAsync(
                    stage.Id,
                    lease.LeaseId,
                    ex.Message,
                    ct: CancellationToken.None);
                throw;
            }
        }
    }

    public async Task<AuthoringReceiptAcceptance> AcceptResultAsync(
        AuthoringRunRecord run,
        AuthoringRunItemRecord item,
        PreparedTicketAuthoringResultRequest request,
        string operationToken,
        CancellationToken ct)
    {
        if (run.Purpose !=
            PreparedTicketPublicationReconciliationContract.Purpose)
        {
            return await authoringStore.AcceptResultAsync(
                request.Submission,
                operationToken,
                async (connection, cancellationToken) =>
                {
                    await JiraProcessingSourceTicketStore
                        .EnsureCurrentSourceRevisionAsync(
                            connection,
                            request.Payload.Key,
                            item.ItemKind,
                            request.Submission.ObservedSourceRevision,
                            cancellationToken);
                    await database.SavePreparedTicketForAuthoringAsync(
                        connection,
                        request.Payload,
                        request.Submission.ContentHash,
                        run.Id,
                        item.Id,
                        request.Submission.OperationId,
                        cancellationToken);
                },
                ct: ct);
        }

        HydrationBatch hydration = await CaptureHydrationAsync(
            request.Payload,
            ct);
        return await authoringStore.AcceptResultWithReceiptAsync(
            request.Submission,
            operationToken,
            async (connection, receiptId, cancellationToken) =>
            {
                await JiraProcessingSourceTicketStore
                    .EnsureCurrentSourceRevisionAsync(
                        connection,
                        request.Payload.Key,
                        item.ItemKind,
                        request.Submission.ObservedSourceRevision,
                        cancellationToken);
                await database.StagePublicationReconciliationTicketAsync(
                    connection,
                    run.Id,
                    item.Id,
                    request.Submission.OperationId,
                    receiptId,
                    request.Submission.ObservedSourceRevision,
                    request.Submission.ContentHash,
                    request.Payload,
                    hydration,
                    ct: cancellationToken);
            },
            ct: ct);
    }

    private async Task<HydrationBatch> CaptureHydrationAsync(
        PreparedTicketPayload payload,
        CancellationToken ct)
    {
        CapturingHydrationTarget target = new(payload);
        HydrationCoordinator coordinator = new(
            target,
            hydrationFetcher,
            logger);
        HydrationAttemptResult result =
            await coordinator.HydrateWithResultAsync(payload.Key, ct);
        if (result is HydrationAttemptFailure failure)
        {
            throw new InvalidOperationException(
                $"Staged hydration failed for '{payload.Key}': {failure.Reason}");
        }
        return target.Batch
            ?? throw new InvalidOperationException(
                $"Staged hydration for '{payload.Key}' produced no batch.");
    }

    private sealed class CapturingHydrationTarget(PreparedTicketPayload payload)
        : IHydrationTargetDatabase
    {
        public string DatabasePath => string.Empty;
        public HydrationBatch? Batch { get; private set; }

        public Task<IReadOnlyList<string>> ListRelatedJiraKeysForTicketAsync(
            string ticketKey,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(
                payload.RelatedJiraTickets
                    .Select(value => value.AssociatedTicketKey)
                    .ToArray());

        public Task<IReadOnlyList<string>>
            ListRelatedZulipThreadIdsForTicketAsync(
                string ticketKey,
                CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(
                payload.RelatedZulipThreads
                    .Select(value => value.ZulipThreadId)
                    .ToArray());

        public Task<IReadOnlyList<string>> ListRelatedGitHubItemIdsForTicketAsync(
            string ticketKey,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(
                payload.RelatedGitHubItems
                    .Select(value => value.GitHubItemId)
                    .ToArray());

        public Task<IReadOnlyList<string>> ListReposForTicketAsync(
            string ticketKey,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(
                payload.Repos.Select(value => value.Repo).ToArray());

        public Task<IReadOnlyList<string>>
            ListUnresolvedOrMissingHydrationKeysAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task SaveHydrationAsync(
            HydrationBatch batch,
            CancellationToken ct)
        {
            Batch = batch;
            return Task.CompletedTask;
        }
    }
}
