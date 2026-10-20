using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public interface IPreparedTicketPublicationRefreshInterruptionHook
{
    Task AfterMetadataCommitAsync(
        string runId,
        PreparedTicketPublicationRefreshReceiptRecord receipt,
        CancellationToken ct);
}

public sealed class NoOpPreparedTicketPublicationRefreshInterruptionHook
    : IPreparedTicketPublicationRefreshInterruptionHook
{
    public Task AfterMetadataCommitAsync(
        string runId,
        PreparedTicketPublicationRefreshReceiptRecord receipt,
        CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class PreparedTicketPublicationRefreshStageException(
    string failureCode,
    string detail)
    : InvalidOperationException($"{failureCode}: {detail}")
{
    public string FailureCode { get; } = failureCode;
}

public sealed class PreparedTicketPublicationRefreshService(
    PreparerDatabase database,
    AuthoringRunStore authoringStore,
    AuthoringRunControlService runControlService,
    JiraAuthoringRunCoordinator coordinator,
    PreparedTicketPublicationBaselineReader baselineReader,
    PreparedTicketPublicationEnricher enricher,
    AuthoringRunSchedulerWakeSignal wakeSignal,
    ILogger<PreparedTicketPublicationRefreshService> logger,
    IPreparedTicketPublicationRefreshInterruptionHook? interruptionHook = null)
{
    public const string HttpClientName =
        "PreparedTicketPublicationRefresh";

    private readonly IPreparedTicketPublicationRefreshInterruptionHook
        _interruptionHook = interruptionHook ??
            new NoOpPreparedTicketPublicationRefreshInterruptionHook();

    public async Task<PreparedTicketPublicationRefreshResult> StartAsync(
        string sourceRunId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        await ValidateSourceRunAsync(sourceRunId, ct);
        PreparedTicketPublicationBaseline baseline = await baselineReader.ReadAsync(sourceRunId, ct);

        AuthoringRunRecord refreshRun;
        try
        {
            refreshRun = await authoringStore.CreateMaintenanceRunAsync(
                coordinator.ProcessorKind,
                (connection, cancellationToken) =>
                    baselineReader.CreateSelectionAsync(connection, baseline, cancellationToken),
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false,
                sourceRunId,
                ct: ct);
        }
        catch (AuthoringConflictException ex)
            when (ex.Code ==
                  AuthoringConflictCode.MutationFenceUnavailable)
        {
            AuthoringRunRecord? fencedRun =
                await authoringStore.GetFencedRunAsync(
                    coordinator.ProcessorKind,
                    CancellationToken.None);
            throw new AuthoringConflictException(
                ex.Code,
                ex.Message,
                fencedRun is null ? ex.RelatedRunIds : [fencedRun.Id]);
        }

        logger.LogInformation(
            "Created publication refresh run {RefreshRunId} from source run {SourceRunId}",
            refreshRun.Id,
            sourceRunId);
        wakeSignal.Signal();

        AuthoringRunControlStatus status =
            await runControlService.GetStatusAsync(
                coordinator.ProcessorKind,
                refreshRun.Id,
                ct);
        return new PreparedTicketPublicationRefreshResult(
            status.Run,
            status.Items);
    }

    internal async Task ValidateRunInventoryAsync(
        AuthoringRunRecord run,
        PreparedTicketPublicationRefreshInventory inventory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(inventory);
        if (!string.Equals(
                run.Purpose,
                AuthoringRunPurposeValues.PublicationRefresh,
                StringComparison.Ordinal) ||
            run.DatabaseOnly ||
            string.IsNullOrWhiteSpace(run.SourceRunId) ||
            string.Equals(run.Id, run.SourceRunId, StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.RunNotActive,
                $"Run '{run.Id}' is not a snapshot-producing publication refresh run.");
        }

        IReadOnlyList<AuthoringRunItemRecord> items =
            await authoringStore.GetRunItemsAsync(run.Id, ct);
        if (items
            .Select(item => item.BusinessKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != items.Count)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Publication refresh run '{run.Id}' contains duplicate ticket coordinates.");
        }
        Dictionary<string, AuthoringRunItemRecord> itemsByTicket =
            items.ToDictionary(
                item => item.BusinessKey,
                StringComparer.OrdinalIgnoreCase);
        if (items.Count != inventory.Candidates.Count)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Publication refresh run '{run.Id}' no longer covers the accepted corpus exactly once.");
        }

        foreach (PreparedTicketPublicationRefreshCandidate candidate in
                 inventory.Candidates)
        {
            if (!itemsByTicket.TryGetValue(
                    candidate.TicketKey,
                    out AuthoringRunItemRecord? item) ||
                !string.Equals(
                    item.ItemKind,
                    $"maintenance:{run.Id}:{candidate.ItemKind}",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.ExpectedSourceRevision,
                    candidate.ExpectedSourceRevision,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.AcceptedReceiptId,
                    candidate.ReceiptId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.Status,
                    AuthoringStatusValues.Items.Complete,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    $"Publication refresh run '{run.Id}' does not retain the accepted receipt coordinate for '{candidate.TicketKey}'.");
            }
        }
    }

    internal async Task ExecuteMetadataStageAsync(
        AuthoringRunRecord run,
        AuthoringRunStageLease lease,
        string inputFingerprint,
        PreparedTicketPublicationRefreshInventory inventory,
        CancellationToken ct)
    {
        PreparedTicketPublicationRefreshReceiptRecord? durableReceipt =
            await database.GetMatchingPublicationRefreshReceiptAsync(
                run.Id,
                lease.StageId,
                inputFingerprint,
                inventory.CorpusFingerprint,
                ct);
        if (durableReceipt is not null)
        {
            logger.LogInformation(
                "Reusing publication metadata receipt for refresh run {RefreshRunId}",
                run.Id);
            return;
        }

        IReadOnlyList<PreparedTicketPublicationMetadata> metadata =
            await enricher.FetchJiraMetadataAsync(inventory.Candidates, DateTimeOffset.UtcNow, ct);

        durableReceipt = await database.ApplyPublicationMetadataAsync(
            run.Id,
            lease,
            inputFingerprint,
            inventory,
            metadata,
            ct);
        await _interruptionHook.AfterMetadataCommitAsync(
            run.Id,
            durableReceipt,
            ct);
    }

    internal async Task ExecuteEnrichmentStageAsync(
        AuthoringRunRecord run,
        AuthoringRunStageLease lease,
        string inputFingerprint,
        PreparedTicketPublicationEnrichmentInput input,
        CancellationToken ct)
    {
        PreparedTicketPublicationRefreshReceiptRecord? receipt =
            await database.GetCommittedPublicationEnrichmentReceiptAsync(
                run.Id, lease, inputFingerprint, input, ct);
        if (receipt is not null)
        {
            logger.LogInformation(
                "Reusing publication enrichment receipt for refresh run {RefreshRunId}", run.Id);
            return;
        }

        PreparedTicketPublicationEnrichmentBatch batch = await enricher.FetchAsync(input, ct);
        receipt = await database.ApplyPublicationEnrichmentAsync(
            run.Id, lease, inputFingerprint, input, batch, ct);
        await _interruptionHook.AfterMetadataCommitAsync(run.Id, receipt, ct);
    }

    private async Task ValidateSourceRunAsync(
        string sourceRunId,
        CancellationToken ct)
    {
        AuthoringRunRecord sourceRun =
            await authoringStore.GetRunAsync(sourceRunId, ct)
            ?? throw new KeyNotFoundException(
                $"Source authoring run '{sourceRunId}' was not found.");
        if (!string.Equals(
                sourceRun.ProcessorKind,
                coordinator.ProcessorKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                sourceRun.Status,
                AuthoringStatusValues.Runs.Completed,
                StringComparison.Ordinal) ||
            sourceRun.DatabaseOnly ||
            string.IsNullOrWhiteSpace(sourceRun.SnapshotId))
        {
            throw new ArgumentException(
                $"Source authoring run '{sourceRunId}' is not a completed Preparer snapshot-producing run.",
                nameof(sourceRunId));
        }

        AuthoringSnapshotDescriptor? descriptor =
            await authoringStore.GetSnapshotDescriptorAsync(
                sourceRun.SnapshotId,
                ct);
        if (descriptor is null ||
            !string.Equals(
                descriptor.ProcessorKind,
                coordinator.ProcessorKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.RunId,
                sourceRunId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Source authoring run '{sourceRunId}' does not have a ready snapshot descriptor.",
                nameof(sourceRunId));
        }
    }

}
