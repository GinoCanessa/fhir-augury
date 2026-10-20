using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database.Records;

namespace FhirAugury.Processing.Jira.Common.Database;

public sealed class JiraAuthoringWorkItemStore(
    AuthoringRunStore authoringStore,
    JiraProcessingSourceTicketStore sourceStore)
    : IAuthoringQueueStore<JiraAuthoringWorkItem>
{
    public async Task<IReadOnlyList<JiraAuthoringWorkItem>> GetPendingAsync(
        string runId,
        int maxItems,
        CancellationToken ct)
    {
        IReadOnlyList<AuthoringRunItemRecord> runItems =
            await authoringStore.GetRunItemsAsync(runId, ct);
        List<JiraAuthoringWorkItem> items = [];
        foreach (AuthoringRunItemRecord runItem in runItems.Where(
                     item => item.Status is AuthoringStatusValues.Items.Pending or
                         AuthoringStatusValues.Items.Persisted))
        {
            JiraProcessingSourceTicketRecord? source = await sourceStore.GetByKeyAsync(
                runItem.BusinessKey,
                runItem.ItemKind,
                ct);
            if (source is null && runItem.AcceptedReceiptId is not null)
            {
                source = new JiraProcessingSourceTicketRecord
                {
                    Key = runItem.BusinessKey,
                    SourceTicketShape = runItem.ItemKind,
                    LastSyncedAt = runItem.CreatedAt,
                };
            }
            if (source is null ||
                (runItem.AcceptedReceiptId is null && !string.Equals(
                    JiraProcessingSourceTicketStore.GetSourceRevision(source),
                    runItem.ExpectedSourceRevision,
                    StringComparison.Ordinal)))
            {
                continue;
            }

            items.Add(new JiraAuthoringWorkItem(runItem, source));
            if (items.Count >= maxItems)
            {
                break;
            }
        }
        return items;
    }

    public async Task<AuthoringQueueClaim?> TryClaimAsync(
        JiraAuthoringWorkItem item,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.RunItem.AcceptedReceiptId) &&
            !string.IsNullOrWhiteSpace(item.RunItem.CurrentOperationId))
        {
            string? leaseId = await authoringStore.ClaimPersistedItemAsync(
                item.RunItem.Id,
                item.RunItem.AcceptedReceiptId,
                item.RunItem.CurrentOperationId,
                startedAt,
                ct);
            return leaseId is not null
                ? new AuthoringQueueClaim(
                    leaseId,
                    string.Empty,
                    item.RunItem.AttemptCount)
                : null;
        }

        AuthoringOperationClaim? claim = await authoringStore.ClaimItemAsync(
            item.RunItem.RunId,
            item.RunItem.Id,
            startedAt,
            ct);
        return claim is null
            ? null
            : new AuthoringQueueClaim(claim.OperationId, claim.OperationToken, claim.AttemptNumber);
    }

    public async Task ApplyResultAsync(
        JiraAuthoringWorkItem item,
        AuthoringQueueClaim claim,
        AuthoringWorkResult result,
        DateTimeOffset completedAt,
        CancellationToken ct)
    {
        switch (result.Disposition)
        {
            case AuthoringWorkDisposition.Persisted:
                return;
            case AuthoringWorkDisposition.Complete:
                if (string.IsNullOrWhiteSpace(result.ReceiptId))
                {
                    throw new InvalidOperationException("A completed authoring item requires a receipt ID.");
                }
                await authoringStore.MarkClaimCompleteAsync(
                    item.RunItem.Id,
                    result.ReceiptId,
                    claim.OperationId,
                    completedAt,
                    ct);
                return;
            case AuthoringWorkDisposition.RetryableError:
            case AuthoringWorkDisposition.PermanentError:
                await authoringStore.MarkClaimErrorAsync(
                    item.RunItem.Id,
                    claim.OperationId,
                    result.Error ?? "Authoring failed.",
                    completedAt,
                    ct);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(result), result.Disposition, "Unknown authoring result.");
        }
    }

    public async Task<AuthoringOrphanRecoveryResult> ResetOrphanedItemsAsync(
        string runId,
        TimeSpan olderThan,
        DateTimeOffset now,
        CancellationToken ct)
    {
        IReadOnlyList<AuthoringRunItemRecord> items = await authoringStore.GetRunItemsAsync(runId, ct);
        int recovered = 0;
        DateTimeOffset? nextRecoveryAt = null;
        foreach (AuthoringRunItemRecord item in items.Where(item =>
                     string.Equals(
                         item.Status,
                         AuthoringStatusValues.Items.InProgress,
                         StringComparison.Ordinal)))
        {
            DateTimeOffset claimStartedAt = item.PostPersistenceLeaseAcquiredAt ??
                item.StartedAt ??
                throw new InvalidOperationException(
                    $"In-progress item '{item.Id}' has no claim timestamp.");
            DateTimeOffset recoveryAt = claimStartedAt + olderThan;
            if (recoveryAt > now)
            {
                nextRecoveryAt = nextRecoveryAt is null ||
                    recoveryAt < nextRecoveryAt
                    ? recoveryAt
                    : nextRecoveryAt;
                continue;
            }

            string claimId = item.PostPersistenceLeaseId ??
                item.CurrentOperationId ??
                throw new InvalidOperationException($"Orphaned item '{item.Id}' has no current claim.");
            try
            {
                await authoringStore.RecoverOrphanedClaimAsync(item.Id, claimId, now, ct);
                recovered++;
            }
            catch (AuthoringConflictException ex)
                when (ex.Code is AuthoringConflictCode.StaleOperation or AuthoringConflictCode.MutationFenceUnavailable)
            {
            }
        }
        return new AuthoringOrphanRecoveryResult(recovered, nextRecoveryAt);
    }
}
