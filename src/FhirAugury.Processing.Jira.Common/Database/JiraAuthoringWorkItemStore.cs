using System.Globalization;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Jira.Common.Database;

public sealed class JiraAuthoringWorkItemStore(
    AuthoringRunStore authoringStore,
    JiraProcessingSourceTicketStore sourceStore,
    JiraAuthoringRunCoordinator coordinator)
    : IAuthoringQueueStore<JiraAuthoringWorkItem>
{
    public async Task<IReadOnlyList<JiraAuthoringWorkItem>> GetPendingAsync(
        int maxItems,
        CancellationToken ct)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            await coordinator.TryActivateNextQueuedRunAsync(ct);
            string? runId = await GetActiveRunIdAsync(ct);
            if (runId is null)
            {
                return [];
            }

            bool wholeRunSuperseded = await coordinator.SupersedeStaleItemsAsync(runId, ct);
            if (wholeRunSuperseded)
            {
                await coordinator.CreateScheduledRunAsync(ct: ct);
                continue;
            }

            IReadOnlyList<AuthoringRunItemRecord> runItems =
                await authoringStore.GetRunItemsAsync(runId, ct);
            List<JiraAuthoringWorkItem> items = [];
            foreach (AuthoringRunItemRecord runItem in runItems.Where(
                         item => item.Status is AuthoringStatusValues.Items.Pending or AuthoringStatusValues.Items.Persisted))
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
                    break;
                }

                items.Add(new JiraAuthoringWorkItem(runItem, source));
                if (items.Count >= maxItems)
                {
                    break;
                }
            }
            return items;
        }
        return [];
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

    public async Task<int> ResetOrphanedItemsAsync(
        TimeSpan olderThan,
        DateTimeOffset now,
        CancellationToken ct)
    {
        string? runId = await GetActiveRunIdAsync(ct);
        if (runId is null)
        {
            return 0;
        }

        IReadOnlyList<AuthoringRunItemRecord> items = await authoringStore.GetRunItemsAsync(runId, ct);
        AuthoringRunItemRecord[] orphaned = items
            .Where(item =>
                string.Equals(item.Status, AuthoringStatusValues.Items.InProgress, StringComparison.Ordinal) &&
                item.StartedAt <= now - olderThan)
            .ToArray();
        int recovered = 0;
        foreach (AuthoringRunItemRecord item in orphaned)
        {
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
        return recovered;
    }

    private async Task<string?> GetActiveRunIdAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = sourceStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.RunId
            FROM authoring_mutation_fences f
            INNER JOIN authoring_runs r ON r.Id = f.RunId
            WHERE f.ProcessorKind = @processorKind AND r.Status = @status
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@processorKind", coordinator.ProcessorKind);
        command.Parameters.AddWithValue("@status", AuthoringStatusValues.Runs.Running);
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
