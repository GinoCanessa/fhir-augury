using System.Collections.Concurrent;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketCanonicalEpochRecoveryService(
    PreparerDatabase database,
    AuthoringRunStore authoringStore,
    AuthoringRunControlService runControlService,
    PreparedTicketSnapshotMaterializer snapshotMaterializer,
    AuthoringRunSchedulerWakeSignal wakeSignal,
    ILogger<PreparedTicketCanonicalEpochRecoveryService> logger)
{
    private const string ProcessorKind = "jira-fhir";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        RecoveryLocks = new(StringComparer.Ordinal);

    public async Task<PreparedTicketCanonicalEpochRecoveryStartResult>
        StartAsync(
            string sourceRunId,
            CancellationToken ct = default)
    {
        PreparerDatabase.CanonicalEpochRecoveryCreation creation =
            await database.CreateCanonicalEpochRecoveryAsync(
                sourceRunId,
                ct: ct);
        wakeSignal.Signal();
        return new(
            await GetStatusAsync(creation.RunId, ct),
            creation.ExistingRun);
    }

    public async Task<PreparedTicketCanonicalEpochRecoveryStatusResult>
        GetStatusAsync(
            string runId,
            CancellationToken ct = default)
    {
        PreparerDatabase.CanonicalEpochRecoveryEvidence evidence =
            await database.GetCanonicalEpochRecoveryEvidenceAsync(runId, ct)
            ?? throw new KeyNotFoundException(
                $"Canonical-epoch recovery '{runId}' was not found.");
        AuthoringRunControlStatus status =
            await runControlService.GetStatusAsync(
                ProcessorKind,
                runId,
                ct);
        if (!string.Equals(
                status.Run.Purpose,
                PreparedTicketCanonicalEpochRecoveryContract.Purpose,
                StringComparison.Ordinal))
        {
            throw new KeyNotFoundException(
                $"Authoring run '{runId}' is not a canonical-epoch recovery.");
        }
        AuthoringRunRecord? fenced =
            await authoringStore.GetFencedRunAsync(ProcessorKind, ct);
        bool fenceHeld = string.Equals(
            fenced?.Id,
            runId,
            StringComparison.Ordinal);
        AuthoringSnapshotDescriptor? snapshot =
            evidence.Resolution is null
                ? null
                : await authoringStore.GetSnapshotDescriptorAsync(
                    evidence.Resolution.SnapshotId,
                    ct)
                  ?? throw new InvalidOperationException(
                      $"Resolved recovery snapshot '{evidence.Resolution.SnapshotId}' has no descriptor.");
        return new(
            status.Run,
            status.Items,
            evidence.Recipe.ToSourceAbandonment(),
            evidence.Recipe.ToFrozenState(),
            new(
                evidence.Journal.State,
                fenceHeld,
                evidence.Journal.LastRecoveryAttemptAt,
                evidence.Journal.FailureCode,
                evidence.Journal.FailureDetail),
            evidence.Resolution?.Proof,
            snapshot);
    }

    public async Task<PreparedTicketPublicationReconciliationStatusResult>
        AttachRecoveryLinkAsync(
            PreparedTicketPublicationReconciliationStatusResult status,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        PreparedTicketCanonicalEpochRecoveryLink? link =
            await database.GetCanonicalEpochRecoveryLinkAsync(
                status.Run.RunId,
                ct);
        return status with { CanonicalEpochRecovery = link };
    }

    public async Task RecoverPendingAsync(CancellationToken ct = default)
    {
        foreach (string runId in
                 await database.ListPendingCanonicalEpochRecoveriesAsync(ct))
        {
            try
            {
                _ = await RecoverAsync(runId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Canonical-epoch recovery {RunId} remains pending",
                    runId);
            }
        }
    }

    public async Task<AuthoringSnapshotDescriptor> RecoverAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        SemaphoreSlim recoveryLock = RecoveryLocks.GetOrAdd(
            runId,
            static _ => new SemaphoreSlim(1, 1));
        bool lockTaken = false;
        try
        {
            await recoveryLock.WaitAsync(ct);
            lockTaken = true;
            return await RecoverCoreAsync(runId, ct);
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PreparedTicketCanonicalEpochRecoveryException failure =
                ex as PreparedTicketCanonicalEpochRecoveryException ??
                new(
                    PreparedTicketCanonicalEpochRecoveryFailureCodes
                        .SnapshotRecoveryFailure,
                    ex.Message,
                    [runId]);
            await RetainFailureAsync(runId, failure, ex);
            throw failure;
        }
        finally
        {
            if (lockTaken)
            {
                recoveryLock.Release();
            }
        }
    }

    private async Task<AuthoringSnapshotDescriptor> RecoverCoreAsync(
        string runId,
        CancellationToken ct)
    {
        PreparerDatabase.CanonicalEpochRecoveryEvidence evidence =
            await database.GetCanonicalEpochRecoveryEvidenceAsync(runId, ct)
            ?? throw new KeyNotFoundException(
                $"Canonical-epoch recovery '{runId}' was not found.");
        if (evidence.Resolution is not null)
        {
            return await authoringStore.GetSnapshotDescriptorAsync(
                evidence.Resolution.SnapshotId,
                ct)
                ?? throw new InvalidOperationException(
                    $"Resolved recovery snapshot '{evidence.Resolution.SnapshotId}' has no descriptor.");
        }

        PreparedTicketCanonicalEpochRecoveryRecipe recipe =
            await database.ValidateCanonicalEpochRecoveryCurrentAsync(
                runId,
                ct);
        AuthoringRunRecord run =
            await authoringStore.GetRunAsync(runId, ct)
            ?? throw new KeyNotFoundException(
                $"Authoring run '{runId}' was not found.");
        if (run.Status is
            AuthoringStatusValues.Runs.Running or
            AuthoringStatusValues.Runs.Error)
        {
            await authoringStore.MarkRunFinalizingAsync(runId, ct: ct);
            run = await authoringStore.GetRunAsync(runId, ct)
                ?? throw new KeyNotFoundException(
                    $"Authoring run '{runId}' was not found.");
        }
        if (!string.Equals(
                run.Status,
                AuthoringStatusValues.Runs.Finalizing,
                StringComparison.Ordinal))
        {
            throw new PreparedTicketCanonicalEpochRecoveryException(
                PreparedTicketCanonicalEpochRecoveryFailureCodes
                    .RunNotRetryable,
                $"Recovery run '{runId}' cannot resume from status '{run.Status}'.",
                [recipe.SourceRunId, runId]);
        }

        PreparedTicketCanonicalEpochRecoveryProof proof =
            PreparerDatabase.CreateCanonicalEpochRecoveryProof(recipe);
        PreparedTicketPublicationCandidateSnapshot? candidate =
            await database.GetCanonicalEpochRecoveryCandidateAsync(
                runId,
                ct);
        candidate ??= await snapshotMaterializer
            .MaterializeCanonicalEpochRecoveryCandidateAsync(
                run,
                recipe,
                proof,
                ct: ct);

        if (!File.Exists(candidate.FinalPath))
        {
            await PreparedTicketSnapshotMaterializer
                .ValidateCanonicalEpochRecoverySnapshotAsync(
                    candidate,
                    candidate.TemporaryPath,
                    ct);
            Directory.CreateDirectory(
                Path.GetDirectoryName(candidate.FinalPath)
                ?? throw new InvalidOperationException(
                    $"Recovery snapshot path '{candidate.FinalPath}' has no parent directory."));
            try
            {
                File.Move(
                    candidate.TemporaryPath,
                    candidate.FinalPath,
                    overwrite: false);
            }
            catch (IOException) when (File.Exists(candidate.FinalPath))
            {
            }
        }

        await PreparedTicketSnapshotMaterializer
            .ValidateCanonicalEpochRecoverySnapshotAsync(
                candidate,
                candidate.FinalPath,
                ct);
        AuthoringSnapshotDescriptor descriptor =
            await database.CompleteCanonicalEpochRecoveryAsync(
                candidate,
                proof,
                ct);
        try
        {
            DeleteTemporaryArtifacts(candidate.TemporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex,
                "Recovery {RunId} is ready but temporary snapshot artifacts remain at {TemporaryPath}",
                runId,
                candidate.TemporaryPath);
        }
        logger.LogInformation(
            "Canonical epoch {AuthoringEpoch} recovered by run {RunId} with verified snapshot {SnapshotId}",
            recipe.AuthoringEpoch,
            runId,
            descriptor.SnapshotId);
        return descriptor;
    }

    private async Task RetainFailureAsync(
        string runId,
        PreparedTicketCanonicalEpochRecoveryException failure,
        Exception original)
    {
        try
        {
            await database.RecordCanonicalEpochRecoveryFailureAsync(
                runId,
                failure.FailureCode,
                failure.Message,
                CancellationToken.None);
        }
        catch (Exception recordFailure)
        {
            logger.LogError(
                recordFailure,
                "Could not persist canonical-epoch recovery failure for {RunId}",
                runId);
        }

        try
        {
            AuthoringRunRecord? run =
                await authoringStore.GetRunAsync(
                    runId,
                    CancellationToken.None);
            if (run?.Status is
                AuthoringStatusValues.Runs.Running or
                AuthoringStatusValues.Runs.Finalizing)
            {
                await authoringStore.MarkRunErrorAsync(
                    runId,
                    $"{failure.FailureCode}: {failure.Message}",
                    ct: CancellationToken.None);
            }
        }
        catch (Exception runFailure)
        {
            logger.LogError(
                runFailure,
                "Could not retain retryable error state for canonical-epoch recovery {RunId}",
                runId);
        }
        logger.LogError(
            original,
            "Canonical-epoch recovery {RunId} failed with {FailureCode}",
            runId,
            failure.FailureCode);
    }

    private static void DeleteTemporaryArtifacts(string path)
    {
        foreach (string candidate in
                 new[] { path, path + "-journal", path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }
}
