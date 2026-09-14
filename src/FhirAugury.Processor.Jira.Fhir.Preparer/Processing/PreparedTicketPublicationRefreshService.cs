using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
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
    OrchestratorHydrationFetcher metadataFetcher,
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

        AuthoringRunRecord refreshRun;
        try
        {
            refreshRun = await authoringStore.CreateMaintenanceRunAsync(
                coordinator.ProcessorKind,
                (connection, cancellationToken) =>
                    PreparerDatabase
                        .GetPublicationRefreshMaintenanceItemsAsync(
                            connection,
                            cancellationToken),
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

        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        List<PreparedTicketPublicationMetadata> metadata =
            new(inventory.Candidates.Count);
        long? sourceContentRevision = null;
        foreach (PreparedTicketPublicationRefreshCandidate candidate in
                 inventory.Candidates)
        {
            PublicationMetadataFetchResult result =
                await metadataFetcher.FetchPublicationMetadataAsync(
                    candidate.TicketKey,
                    hydratedAt,
                    ct);
            if (!result.IsSuccess)
            {
                PublicationMetadataFetchFailure failure =
                    result.Failure ??
                    new PublicationMetadataFetchFailure(
                        PublicationMetadataFetchFailureReason.InvalidResponse,
                        "The publication metadata response did not include a failure reason.");
                if (failure.Reason ==
                    PublicationMetadataFetchFailureReason.TicketNotFound)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.SourceRevisionMismatch,
                        $"{PreparedTicketPublicationRefreshFailureCodes.TicketNotFound}: Jira source ticket '{candidate.TicketKey}' no longer exists. {failure.Detail}");
                }
                throw new PreparedTicketPublicationRefreshStageException(
                    ToFailureCode(failure.Reason),
                    $"{candidate.TicketKey}: {failure.Detail}");
            }
            if (result.ObservedSourceRevision is null ||
                result.SourceProject is null ||
                result.SourceLastSuccessfulRefreshAt is null ||
                result.SourceContentRevision is null ||
                result.SourceContentRevision < 0 ||
                result.SourceIsStable != true ||
                result.PublicDisplayNamePolicyVersion !=
                    PublicDisplayNamePolicy.CurrentVersion)
            {
                throw new PreparedTicketPublicationRefreshStageException(
                    PreparedTicketPublicationRefreshFailureCodes
                        .InvalidSourceResponse,
                    $"{candidate.TicketKey}: The successful publication metadata response was incomplete.");
            }

            string expectedRevision =
                AuthoringSourceRevision.CanonicalizeTimestamp(
                    candidate.ExpectedSourceRevision);
            string observedRevision =
                AuthoringSourceRevision.CanonicalizeTimestamp(
                    result.ObservedSourceRevision);
            if (!string.Equals(
                    expectedRevision,
                    observedRevision,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"{PreparedTicketPublicationRefreshFailureCodes.SourceRevisionMismatch}: Jira source revision for '{candidate.TicketKey}' changed from '{expectedRevision}' to '{observedRevision}'.");
            }
            if (sourceContentRevision is null)
            {
                sourceContentRevision = result.SourceContentRevision.Value;
            }
            else if (sourceContentRevision.Value !=
                     result.SourceContentRevision.Value)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"{PreparedTicketPublicationRefreshFailureCodes.SourceGenerationConflict}: Publication metadata spans more than one Jira content revision.");
            }

            metadata.Add(
                new PreparedTicketPublicationMetadata(
                    result.TicketKey,
                    observedRevision,
                    result.Reporter,
                    result.Assignee,
                    result.InPersonRequesters,
                    result.SourceProject,
                    result.SourceLastSuccessfulRefreshAt.Value,
                    result.SourceContentRevision.Value,
                    true,
                    PublicDisplayNamePolicy.CurrentVersion,
                    result.HydratedAt));
        }

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

    private static string ToFailureCode(
        PublicationMetadataFetchFailureReason reason)
        => reason switch
        {
            PublicationMetadataFetchFailureReason.SourceUnavailable =>
                PreparedTicketPublicationRefreshFailureCodes
                    .SourceUnavailable,
            PublicationMetadataFetchFailureReason.TicketNotFound =>
                PreparedTicketPublicationRefreshFailureCodes
                    .TicketNotFound,
            PublicationMetadataFetchFailureReason.InvalidResponse =>
                PreparedTicketPublicationRefreshFailureCodes
                    .InvalidSourceResponse,
            PublicationMetadataFetchFailureReason.MissingSourceProvenance =>
                PreparedTicketPublicationRefreshFailureCodes
                    .MissingSourceProvenance,
            PublicationMetadataFetchFailureReason.UnstableSource =>
                PreparedTicketPublicationRefreshFailureCodes
                    .UnstableSource,
            PublicationMetadataFetchFailureReason
                .MissingProjectProvenance =>
                PreparedTicketPublicationRefreshFailureCodes
                    .MissingProjectProvenance,
            PublicationMetadataFetchFailureReason.PeoplePolicyNotCurrent =>
                PreparedTicketPublicationRefreshFailureCodes
                    .PeoplePolicyNotCurrent,
            _ => PreparedTicketPublicationRefreshFailureCodes
                .InvalidSourceResponse,
        };
}
