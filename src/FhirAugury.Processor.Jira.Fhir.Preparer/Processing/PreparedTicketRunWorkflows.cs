using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketRunWorkflowRegistry(
    AuthoringRunStore authoringStore,
    PreparerDatabase database,
    OrchestratorHydrationFetcher hydrationFetcher,
    ILogger<PreparedTicketRunWorkflowRegistry> logger)
{
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
