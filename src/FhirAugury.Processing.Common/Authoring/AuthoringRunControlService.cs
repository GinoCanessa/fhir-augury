using System.Text.Json;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Common.Authoring;

public sealed record AuthoringRunControlStatus(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);

public sealed class AuthoringRunControlService(
    AuthoringRunStore store,
    AuthoringRetryPolicy retryPolicy)
{
    public async Task<AuthoringRunListResponse> ListAsync(
        string processorKind,
        int limit = AuthoringRunStore.DefaultOperatorRunLimit,
        CancellationToken ct = default)
    {
        AuthoringOperatorRunList result =
            await store.ListOperatorRunsAsync(processorKind, limit, ct);
        AuthoringRunRecord? fencedRun =
            await store.GetFencedRunAsync(processorKind, ct);

        return new AuthoringRunListResponse(
            result.Runs.Select(summary =>
            {
                bool isFenced = IsFencedRun(summary.Run, fencedRun);
                return ToStatus(
                    summary.Run,
                    summary.CompletedItems,
                    summary.RetryableErrorItems + summary.SupersededItems,
                    summary.RetryableErrorItems,
                    summary.SupersededItems,
                    isFenced ? summary.NextAutomaticRetryAt : null);
            }).ToArray(),
            result.Truncated);
    }

    public async Task<AuthoringRunControlStatus> GetStatusAsync(
        string processorKind,
        string runId,
        CancellationToken ct = default)
    {
        AuthoringRunRecord run = await GetOwnedRunAsync(processorKind, runId, ct);
        IReadOnlyList<AuthoringRunItemRecord> items =
            await store.GetRunItemsAsync(runId, ct);
        AuthoringRunRecord? fencedRun =
            await store.GetFencedRunAsync(processorKind, ct);
        bool isFenced = IsFencedRun(run, fencedRun);
        int retryableErrorItems = items.Count(item =>
            string.Equals(
                item.Status,
                AuthoringStatusValues.Items.Error,
                StringComparison.Ordinal));
        int supersededItems = items.Count(item =>
            string.Equals(
                item.Status,
                AuthoringStatusValues.Items.Superseded,
                StringComparison.Ordinal));
        AuthoringRunItemStatus[] itemStatuses = items
            .Select(item => ToStatus(item, run.Purpose, isFenced))
            .ToArray();

        return new AuthoringRunControlStatus(
            ToStatus(
                run,
                items.Count(item =>
                    string.Equals(
                        item.Status,
                        AuthoringStatusValues.Items.Complete,
                        StringComparison.Ordinal)),
                retryableErrorItems + supersededItems,
                retryableErrorItems,
                supersededItems,
                itemStatuses
                    .Select(item => item.NextAutomaticRetryAt)
                    .Where(value => value is not null)
                    .Min()),
            itemStatuses);
    }

    public async Task<AuthoringRetryResult> RetryItemAsync(
        string processorKind,
        string runId,
        string itemId,
        CancellationToken ct = default)
    {
        await GetOwnedItemAsync(processorKind, runId, itemId, ct);
        return await store.RetryItemAsync(itemId, ct: ct);
    }

    public async Task<AuthoringItemSupersedeResult> SupersedeItemAsync(
        string processorKind,
        string runId,
        string itemId,
        AuthoringItemSupersedeRequest request,
        CancellationToken ct = default)
    {
        AuthoringRunRecord run =
            await GetOwnedRunAsync(processorKind, runId, ct);
        _ = await GetOwnedItemAsync(processorKind, runId, itemId, ct);
        if (string.Equals(
                run.Purpose,
                AuthoringRunPurposeValues.PublicationReconciliation,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.ReconciliationCancelRequired,
                $"Publication reconciliation item '{itemId}' cannot be superseded. Cancel reconciliation '{runId}' instead.");
        }
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        return await store.SupersedeErroredItemAsync(
            runId,
            itemId,
            request.Reason,
            ct: ct);
    }

    private AuthoringRunStatus ToStatus(
        AuthoringRunRecord run,
        int completedItems,
        int failedItems,
        int retryableErrorItems,
        int supersededItems,
        DateTimeOffset? nextAutomaticRecoveryAt)
    {
        AuthoringRunCorpusComparison? corpusComparison = null;
        string? error = run.Error;
        if (AuthoringRunPurposeValues.IsMaintenance(run.Purpose))
        {
            try
            {
                corpusComparison = AuthoringMaintenanceRunRequest.ReadCorpusComparison(
                    run.RequestJson);
            }
            catch (Exception ex) when (
                ex is JsonException or ArgumentException or NotSupportedException)
            {
                const string metadataError =
                    "Maintenance request metadata is invalid or unsupported; corpus comparison is unavailable.";
                error = error is null ? metadataError : $"{error} {metadataError}";
            }
        }

        return new AuthoringRunStatus(
            run.Id,
            run.ProcessorKind,
            run.AuthoringEpoch,
            run.Status,
            run.DatabaseOnly,
            run.TotalItems,
            completedItems,
            failedItems,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            error,
            retryableErrorItems,
            supersededItems,
            ToState(run.Status, nextAutomaticRecoveryAt),
            run.Purpose,
            run.SourceRunId,
            corpusComparison);
    }

    private AuthoringRunItemStatus ToStatus(
        AuthoringRunItemRecord item,
        string runPurpose,
        bool isFenced)
    {
        bool isError = string.Equals(
            item.Status,
            AuthoringStatusValues.Items.Error,
            StringComparison.Ordinal);
        bool isSuperseded = string.Equals(
            item.Status,
            AuthoringStatusValues.Items.Superseded,
            StringComparison.Ordinal);
        bool hasAcceptedReceipt =
            !string.IsNullOrWhiteSpace(item.AcceptedReceiptId);
        bool hasAttemptsRemaining = isError &&
            !hasAcceptedReceipt &&
            retryPolicy.CanStartAnotherAuthoringAttempt(item.AttemptCount);
        int? attemptsRemaining = isSuperseded
            ? 0
            : isError && !hasAcceptedReceipt
                ? Math.Max(0, retryPolicy.MaxAttempts - item.AttemptCount)
                : null;
        bool canRetryNow = isFenced &&
            isError &&
            (hasAcceptedReceipt || hasAttemptsRemaining);
        bool canSupersede = isFenced &&
            isError &&
            !hasAcceptedReceipt &&
            !string.Equals(
                runPurpose,
                AuthoringRunPurposeValues.PublicationReconciliation,
                StringComparison.Ordinal);
        DateTimeOffset? nextAutomaticRetryAt =
            canRetryNow && item.CompletedAt is not null
            ? retryPolicy.GetNextAutomaticRetryAt(item.CompletedAt.Value)
            : null;

        return new AuthoringRunItemStatus(
            item.Id,
            item.RunId,
            item.BusinessKey,
            item.ItemKind,
            item.ExpectedSourceRevision,
            item.Status,
            item.CurrentOperationId,
            item.AcceptedReceiptId,
            item.AttemptCount,
            item.CreatedAt,
            item.StartedAt,
            item.CompletedAt,
            item.Error,
            attemptsRemaining,
            nextAutomaticRetryAt,
            isError ? item.Error : null,
            isSuperseded ? item.Error : null,
            new AuthoringAllowedActions(canRetryNow, canSupersede));
    }

    private static AuthoringRunStateInfo ToState(
        string status,
        DateTimeOffset? nextAutomaticRecoveryAt)
    {
        bool isTerminal = status switch
        {
            AuthoringStatusValues.Runs.Queued => false,
            AuthoringStatusValues.Runs.Running => false,
            AuthoringStatusValues.Runs.Finalizing => false,
            AuthoringStatusValues.Runs.Error => false,
            AuthoringStatusValues.Runs.Completed => true,
            AuthoringStatusValues.Runs.CompletedDatabaseOnly => true,
            AuthoringStatusValues.Runs.Superseded => true,
            _ => throw new InvalidOperationException(
                $"Unknown authoring run status '{status}'."),
        };
        return new AuthoringRunStateInfo(
            isTerminal,
            string.Equals(
                status,
                AuthoringStatusValues.Runs.Error,
                StringComparison.Ordinal),
            isTerminal ? null : nextAutomaticRecoveryAt);
    }

    private static bool IsFencedRun(
        AuthoringRunRecord run,
        AuthoringRunRecord? fencedRun)
        => fencedRun is not null &&
            string.Equals(run.Id, fencedRun.Id, StringComparison.Ordinal);

    private async Task<AuthoringRunRecord> GetOwnedRunAsync(
        string processorKind,
        string runId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        AuthoringRunRecord? run = await store.GetRunAsync(runId, ct);
        if (run is null ||
            !string.Equals(run.ProcessorKind, processorKind, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException(
                $"Authoring run '{runId}' was not found for processor '{processorKind}'.");
        }
        return run;
    }

    private async Task<AuthoringRunItemRecord> GetOwnedItemAsync(
        string processorKind,
        string runId,
        string itemId,
        CancellationToken ct)
    {
        await GetOwnedRunAsync(processorKind, runId, ct);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        AuthoringRunItemRecord? item = (await store.GetRunItemsAsync(runId, ct))
            .SingleOrDefault(candidate =>
                string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        return item ?? throw new KeyNotFoundException(
            $"Authoring item '{itemId}' was not found in run '{runId}'.");
    }
}
