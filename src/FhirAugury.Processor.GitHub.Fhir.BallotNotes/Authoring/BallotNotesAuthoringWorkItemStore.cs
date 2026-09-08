using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;

public sealed record BallotNotesAuthoringWorkItem(
    AuthoringRunItemRecord RunItem,
    NotesHydrationRunItemRecord HydrationItem);

public sealed class BallotNotesAuthoringWorkItemStore(
    BallotNotesDatabase database,
    AuthoringRunStore authoringStore)
    : IAuthoringQueueStore<BallotNotesAuthoringWorkItem>
{
    public async Task<IReadOnlyList<BallotNotesAuthoringWorkItem>> GetPendingAsync(
        string runId,
        int maxItems,
        CancellationToken ct)
    {
        IReadOnlyList<AuthoringRunItemRecord> items =
            await authoringStore.GetRunItemsAsync(runId, ct);
        List<BallotNotesAuthoringWorkItem> pending = [];
        foreach (AuthoringRunItemRecord item in items.Where(value =>
                     value.Status is AuthoringStatusValues.Items.Pending or
                         AuthoringStatusValues.Items.Persisted))
        {
            NotesHydrationRunItemRecord? hydration =
                database.GetHydrationItemForRevision(
                    item.BusinessKey,
                    item.ItemKind,
                    item.ExpectedSourceRevision);
            Persistence.Models.NoteDetail? current =
                database.GetNote(item.BusinessKey);
            bool staleUnaccepted = item.AcceptedReceiptId is null &&
                (current is null ||
                 !string.Equals(
                     current.Note.Type,
                     item.ItemKind,
                     StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(
                     current.Note.CurrentEvidenceRevision,
                     item.ExpectedSourceRevision,
                     StringComparison.Ordinal));
            if (hydration is null || staleUnaccepted)
            {
                continue;
            }
            pending.Add(new BallotNotesAuthoringWorkItem(item, hydration));
            if (pending.Count >= maxItems)
            {
                break;
            }
        }
        return pending;
    }

    public async Task<AuthoringQueueClaim?> TryClaimAsync(
        BallotNotesAuthoringWorkItem item,
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
            return leaseId is null
                ? null
                : new AuthoringQueueClaim(
                    leaseId,
                    string.Empty,
                    item.RunItem.AttemptCount);
        }

        AuthoringOperationClaim? claim = await authoringStore.ClaimItemAsync(
            item.RunItem.RunId,
            item.RunItem.Id,
            startedAt,
            ct);
        return claim is null
            ? null
            : new AuthoringQueueClaim(
                claim.OperationId,
                claim.OperationToken,
                claim.AttemptNumber);
    }

    public async Task ApplyResultAsync(
        BallotNotesAuthoringWorkItem item,
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
                    throw new InvalidOperationException(
                        "A completed BallotNotes item requires a receipt ID.");
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
                    result.Error ?? "Ballot-note authoring failed.",
                    completedAt,
                    ct);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.Disposition,
                    "Unknown BallotNotes authoring result.");
        }
    }

    public async Task<int> ResetOrphanedItemsAsync(
        string runId,
        TimeSpan olderThan,
        DateTimeOffset now,
        CancellationToken ct)
    {
        IReadOnlyList<AuthoringRunItemRecord> items =
            await authoringStore.GetRunItemsAsync(runId, ct);
        int recovered = 0;
        foreach (AuthoringRunItemRecord item in items.Where(value =>
                     string.Equals(
                         value.Status,
                         AuthoringStatusValues.Items.InProgress,
                         StringComparison.Ordinal) &&
                     value.StartedAt <= now - olderThan))
        {
            string claimId = item.PostPersistenceLeaseId ??
                item.CurrentOperationId ??
                throw new InvalidOperationException(
                    $"Orphaned BallotNotes item '{item.Id}' has no current claim.");
            try
            {
                await authoringStore.RecoverOrphanedClaimAsync(
                    item.Id,
                    claimId,
                    now,
                    ct);
                recovered++;
            }
            catch (AuthoringConflictException ex)
                when (ex.Code is AuthoringConflictCode.StaleOperation or
                    AuthoringConflictCode.MutationFenceUnavailable)
            {
            }
        }
        return recovered;
    }
}
