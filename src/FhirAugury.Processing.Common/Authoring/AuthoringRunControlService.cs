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
    public async Task<AuthoringRunControlStatus> GetStatusAsync(
        string processorKind,
        string runId,
        CancellationToken ct = default)
    {
        AuthoringRunRecord run = await GetOwnedRunAsync(processorKind, runId, ct);
        IReadOnlyList<AuthoringRunItemRecord> items =
            await store.GetRunItemsAsync(runId, ct);
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

        return new AuthoringRunControlStatus(
            new AuthoringRunStatus(
                run.Id,
                run.ProcessorKind,
                run.AuthoringEpoch,
                run.Status,
                run.DatabaseOnly,
                run.TotalItems,
                items.Count(item =>
                    string.Equals(
                        item.Status,
                        AuthoringStatusValues.Items.Complete,
                        StringComparison.Ordinal)),
                retryableErrorItems + supersededItems,
                run.CreatedAt,
                run.StartedAt,
                run.CompletedAt,
                run.Error,
                retryableErrorItems,
                supersededItems),
            items.Select(ToStatus).ToArray());
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
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        await GetOwnedItemAsync(processorKind, runId, itemId, ct);
        return await store.SupersedeErroredItemAsync(
            runId,
            itemId,
            request.Reason,
            ct: ct);
    }

    private AuthoringRunItemStatus ToStatus(AuthoringRunItemRecord item)
    {
        bool isError = string.Equals(
            item.Status,
            AuthoringStatusValues.Items.Error,
            StringComparison.Ordinal);
        bool isSuperseded = string.Equals(
            item.Status,
            AuthoringStatusValues.Items.Superseded,
            StringComparison.Ordinal);
        int? attemptsRemaining = isSuperseded
            ? 0
            : isError && item.AcceptedReceiptId is null
                ? Math.Max(0, retryPolicy.MaxAttempts - item.AttemptCount)
                : null;
        DateTimeOffset? nextAutomaticRetryAt = isError && item.CompletedAt is not null
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
            nextAutomaticRetryAt);
    }

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
